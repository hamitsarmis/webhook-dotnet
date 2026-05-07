import asyncio
import logging
import os
import random
from typing import Optional, Literal, Dict, Any
import traceback
from datetime import datetime, timezone

from metaapi_cloud_sdk import MetaApi

Side = Literal["buy", "sell"]


class ForexManager:
  """
  Using MetaApi Cloud SDK:
    - Connect / Sync
    - Open position with market order
    - Close position with market order (using positionId)
    - Check connection using heartbeat, reconnect if needed

  Requirements:
    pip install metaapi-cloud-sdk
  """

  def __init__(
    self,
    token: str,
    account_id: str,
    *,
    heartbeat_interval_sec: int = 10,
    reconnect_base_delay_sec: float = 2.0,
    reconnect_max_delay_sec: float = 30.0,
    rpc_timeout_sec: float = 30.0,
    ready_wait_timeout_sec: float = 60.0,
    enable_trade_manager: bool = False,
    logger: Optional[logging.Logger] = None
  ):
    self.token = token
    self.account_id = account_id

    self.heartbeat_interval_sec = heartbeat_interval_sec
    self.reconnect_base_delay_sec = reconnect_base_delay_sec
    self.reconnect_max_delay_sec = reconnect_max_delay_sec
    self.rpc_timeout_sec = rpc_timeout_sec
    self.ready_wait_timeout_sec = ready_wait_timeout_sec
    self.enable_trade_manager = enable_trade_manager

    self.log = logger or logging.getLogger("ForexManager")
    self.log.setLevel(logging.WARNING)
    
    # Add handlers if not already present
    if not self.log.handlers:
      formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
      
      # Console handler
      console_handler = logging.StreamHandler()
      console_handler.setLevel(logging.INFO)
      console_handler.setFormatter(formatter)
      self.log.addHandler(console_handler)
      
      # File handler - log to app.log
      file_handler = logging.FileHandler('app.log')
      file_handler.setLevel(logging.WARNING)
      file_handler.setFormatter(formatter)
      self.log.addHandler(file_handler)

    self._api: Optional[MetaApi] = None
    self._account = None
    self._conn = None

    self._ready = asyncio.Event()
    self._stop = asyncio.Event()
    self._heartbeat_task: Optional[asyncio.Task] = None

    # only one reconnect attempt at a time
    self._reconnect_lock = asyncio.Lock()

    # Trade management state: {position_id: {"open_price", "direction", "symbol", "volume", "peak_profit"}}
    self._trade_state: Dict[str, Dict[str, Any]] = {}
    self._trade_manager_task: Optional[asyncio.Task] = None
    self._trade_manager_interval_sec = 5  # Check every 5 seconds

    # --- MFE/MAE-optimized parameters from 90-day backtest ---
    self.initial_sl_distance = 15.0
    self.breakeven_profit = 15.0
    self.trail_activation = 20.0
    self.trail_distance = 5.0

    # Pending re-entry after SL: {symbol: {"entry_price", "direction", "volume"}}
    self._pending_reentry: Dict[str, Dict[str, Any]] = {}

    # Last signal tracking
    self.last_signal: Optional[Dict[str, Any]] = None

    # Concurrency: per-symbol locks + close-all barrier.
    # - Opens for different symbols run in parallel.
    # - Opens for the same symbol serialize.
    # - close_all_positions() drains every symbol lock before running, and
    #   sets _close_all_in_progress so newly arriving opens wait it out.
    self._symbol_locks: Dict[str, asyncio.Lock] = {}
    self._close_all_lock = asyncio.Lock()
    self._close_all_in_progress = asyncio.Event()

  # ----------------------------
  # Public lifecycle
  # ----------------------------
  async def start(self) -> None:
    """Connects and starts heartbeat and trade management tasks."""
    self._stop.clear()
    await self._connect_and_sync()
    self._heartbeat_task = asyncio.create_task(self._heartbeat_loop(), name="forexmanager-heartbeat")
    if self.enable_trade_manager:
      self._trade_manager_task = asyncio.create_task(self._trade_manager_loop(), name="forexmanager-trade-manager")
    else:
      self.log.warning("Trade manager disabled (set ENABLE_TRADE_MANAGER=1 to enable)")

  async def stop(self) -> None:
    """Stops heartbeat and closes connection."""
    self._stop.set()
    if self._heartbeat_task:
      self._heartbeat_task.cancel()
      try:
        await self._heartbeat_task
      except asyncio.CancelledError:
        pass
      self._heartbeat_task = None

    if self._trade_manager_task:
      self._trade_manager_task.cancel()
      try:
        await self._trade_manager_task
      except asyncio.CancelledError:
        pass
      self._trade_manager_task = None

    await self._disconnect()

  async def wait_ready(self, timeout: Optional[float] = None) -> bool:
    """Waits until bot is ready and synchronized."""
    try:
      await asyncio.wait_for(self._ready.wait(), timeout=timeout)
      return True
    except asyncio.TimeoutError:
      return False

  # ----------------------------
  # Trading ops
  # ----------------------------
  @staticmethod
  def _current_task_is_cancelling() -> bool:
    """True iff our current asyncio task is being cancelled via task.cancel().

    Distinguishes a legitimate outer cancel (e.g. shutdown) from the SDK
    cancelling an in-flight RPC on its internal socket — the latter raises
    CancelledError inside our await without marking our task as cancelling.
    """
    task = asyncio.current_task()
    return task is not None and task.cancelling() > 0

  def _get_symbol_lock(self, symbol: str) -> asyncio.Lock:
    """Returns (and lazily creates) the asyncio.Lock for a symbol."""
    lock = self._symbol_locks.get(symbol)
    if lock is None:
      lock = asyncio.Lock()
      self._symbol_locks[symbol] = lock
    return lock

  async def _wait_for_close_all_done(self) -> None:
    """If a close-all is in progress, block until it finishes."""
    while self._close_all_in_progress.is_set():
      await asyncio.sleep(0.01)

  async def open_market_order(
    self,
    symbol: str,
    side: Side,
    volume: float,
    *,
    magic: Optional[int] = None,
    slippage: Optional[float] = None,
    update_last_signal: bool = True,
  ) -> Dict[str, Any]:

    await self._ensure_ready()
    await self._wait_for_close_all_done()

    params = {
        "symbol": symbol,
        "volume": volume
    }

    if magic is not None:
        params["magic"] = magic
    if slippage is not None:
        params["slippage"] = slippage

    sym_lock = self._get_symbol_lock(symbol)
    async with sym_lock:
      try:
        return await self._execute_market_order(side, params, symbol, volume, update_last_signal)
      except asyncio.CancelledError:
        if self._current_task_is_cancelling():
          raise
        # SDK cancelled our RPC during an internal reconnect — transient.
        self.log.warning("open_market_order cancelled mid-RPC (SDK reconnect). Triggering reconnect...")
      except Exception as e:
        self.log.warning(
            "open_market_order failed (%s). Triggering reconnect...", repr(e)
        )

      await self._trigger_reconnect()

      # Retry once after reconnect
      try:
          self.log.warning("Retrying open_market_order after reconnect...")
          return await self._execute_market_order(side, params, symbol, volume, update_last_signal)
      except Exception as retry_e:
          self.log.warning("Retry also failed (%s). Giving up.", repr(retry_e))
          raise

  async def _execute_market_order(self, side, params, symbol, volume, update_last_signal):
    if side == "buy":
        response = await asyncio.wait_for(
            self._conn.create_market_buy_order(**params),
            timeout=self.rpc_timeout_sec,
        )
    else:
        response = await asyncio.wait_for(
            self._conn.create_market_sell_order(**params),
            timeout=self.rpc_timeout_sec,
        )

    position_id = response["positionId"]

    # Poll for the position to appear instead of a fixed sleep.
    # MetaApi can take 50-300ms to reflect a new market order.
    position = await self._poll_for_position(position_id)
    open_price = position["openPrice"]

    sl_distance = self.initial_sl_distance

    if update_last_signal:
        if side == "buy":
            sl = open_price - sl_distance
            tp = open_price + 2.0
        else:
            sl = open_price + sl_distance
            tp = open_price - 2.0
        await asyncio.wait_for(
            self._conn.modify_position(position_id, stop_loss=sl, take_profit=tp),
            timeout=self.rpc_timeout_sec,
        )
        self.log.warning(f"Set SL={sl:.2f}, TP={tp:.2f} for position {position_id} (distance={sl_distance:.1f})")

    if update_last_signal:
        self.last_signal = {
            "date_created": datetime.now(timezone.utc).isoformat(),
            "action": side,
            "price": open_price,
            "first_entry_price": open_price,
            "symbol": symbol,
            "volume": volume,
            "has_profit": False,
            "has_pending_order": False,
        }
        await self.delete_all_pending_orders()
        self.log.warning(f"Last signal set: {self.last_signal}")

    self._trade_state[position_id] = {
        "open_price": open_price,
        "direction": side,
        "symbol": symbol,
        "volume": volume,
        "peak_profit": 0.0,
    }
    self.log.warning(
        f"Trade state for {position_id}: entry={open_price}, dir={side}, "
        f"SL={sl_distance:.1f}, BE={self.breakeven_profit:.1f}, "
        f"trail@{self.trail_activation:.1f} dist={self.trail_distance:.1f}"
    )

    return response

  async def close_position_market(
    self,
    position_id: str,
    *,
    volume: Optional[float] = None,
  ) -> Dict[str, Any]:
    """
    Closes position with market order.
    MetaApi RPC: close_position (using positionId)

    If volume is None closes the whole position (if account/broker allows it).
    """
    await self._ensure_ready()

    try:
      if volume is not None:
        return await asyncio.wait_for(
          self._conn.close_position(position_id, volume),
          timeout=self.rpc_timeout_sec,
        )
      else:
        return await asyncio.wait_for(
          self._conn.close_position(position_id),
          timeout=self.rpc_timeout_sec,
        )
    except asyncio.CancelledError:
      if self._current_task_is_cancelling():
        raise
      self.log.warning("close_position_market cancelled mid-RPC (SDK reconnect). Triggering reconnect...")
      await self._trigger_reconnect()
      raise
    except Exception as e:
      self.log.warning("close_position_market failed (%s). Triggering reconnect...", repr(e))
      await self._trigger_reconnect()
      raise

  async def get_positions(self) -> Dict[str, Any]:
    """Retrieves all open positions from MetaApi."""
    await self._ensure_ready()
    try:
      return await asyncio.wait_for(
        self._conn.get_positions(),
        timeout=self.rpc_timeout_sec,
      )
    except asyncio.CancelledError:
      if self._current_task_is_cancelling():
        raise
      self.log.warning("get_positions cancelled mid-RPC (SDK reconnect). Triggering reconnect...")
      await self._trigger_reconnect()
      raise
    except Exception as e:
      self.log.warning("get_positions failed (%s). Triggering reconnect...", repr(e))
      await self._trigger_reconnect()
      raise

  async def _poll_for_position(
    self,
    position_id: str,
    *,
    poll_interval_sec: float = 0.05,
    max_wait_sec: float = 3.0,
  ) -> Dict[str, Any]:
    """Poll get_position until it returns, or max_wait_sec elapses."""
    deadline = asyncio.get_event_loop().time() + max_wait_sec
    last_err: Optional[BaseException] = None
    while True:
      try:
        return await asyncio.wait_for(
          self._conn.get_position(position_id),
          timeout=self.rpc_timeout_sec,
        )
      except Exception as e:
        last_err = e
        if asyncio.get_event_loop().time() >= deadline:
          raise
        await asyncio.sleep(poll_interval_sec)

  async def close_all_positions(self) -> None:
    """
    Atomically closes every open position and deletes pending orders.
    Drains in-flight per-symbol opens before running so a racing open
    cannot create a position the close-all snapshot would miss.
    """
    await self._ensure_ready()
    async with self._close_all_lock:
      self._close_all_in_progress.set()
      try:
        # Acquire every known symbol lock so any in-flight open finishes
        # before we snapshot positions. New opens will see the flag and wait.
        sym_locks = list(self._symbol_locks.values())
        acquired: list = []
        try:
          for lock in sym_locks:
            await lock.acquire()
            acquired.append(lock)

          positions = await self.get_positions()
          if positions:
            await asyncio.gather(
              *(self.close_position_market(pos["id"]) for pos in positions),
              return_exceptions=True,
            )
          await self.delete_all_pending_orders()
          self.last_signal = None
        finally:
          for lock in acquired:
            lock.release()
      finally:
        self._close_all_in_progress.clear()

  # ----------------------------
  # Internals: connect / sync / heartbeat
  # ----------------------------
  async def _connect_and_sync(self) -> None:
    self._ready.clear()

    self.log.warning("Connecting to MetaApi...")
    self._api = MetaApi(self.token)

    self._account = await self._api.metatrader_account_api.get_account(self.account_id)

    if self._account.state != "DEPLOYED":
      self.log.warning("Account not deployed, deploying...")
      await self._account.deploy()

    self.log.warning("Waiting for MetaApi server connection...")
    await self._account.wait_connected()

    self.log.warning("Creating RPC connection...")
    self._conn = self._account.get_rpc_connection()  # ✅ no await

    await self._conn.connect()  # ✅ await needed
    self.log.warning("Waiting for synchronization...")
    await self._conn.wait_synchronized()  # ✅ await needed

    self.log.warning("READY ✅")
    self._ready.set()

  async def _ensure_ready(self) -> None:
    if self._ready.is_set():
      return
    try:
      await asyncio.wait_for(self._ready.wait(), timeout=self.ready_wait_timeout_sec)
    except asyncio.TimeoutError:
      raise RuntimeError(
        f"ForexManager not ready after {self.ready_wait_timeout_sec}s "
        "(reconnect in progress or start() never called)."
      )

  async def _heartbeat_loop(self) -> None:
    """
    Tests connection health by calling get_account_information.
    If there is an issue, reconnects automatically.
    """
    self.log.warning("Heartbeat started (%ss interval)", self.heartbeat_interval_sec)

    while not self._stop.is_set():
      await asyncio.sleep(self.heartbeat_interval_sec)

      if not self._ready.is_set() or self._conn is None:
        continue

      try:
        # The most reliable way: an RPC call
        # such as get_account_information / get_symbol_price / get_positions.
        await asyncio.wait_for(
          self._conn.get_account_information(),
          timeout=self.rpc_timeout_sec,
        )
      except Exception as e:
        self.log.warning("Heartbeat check failed (%s). Reconnecting...", repr(e))
        await self._trigger_reconnect()

    self.log.warning("Heartbeat stopped.")

  # ----------------------------
  # Trade Management
  # ----------------------------
  async def _trade_manager_loop(self) -> None:
    """
    Background task that monitors positions and applies PFE (Peak Favorable Excursion) strategy.

    Rules:
    - Track how far price moved in our favor from entry (peak excursion)
    - Trailing stop adjusts SL as profit grows
    - On SL hit: re-enter at original entry via limit order
    - Peak tracking resets on each new cycle
    """
    self.log.warning("Trade manager started (%ss interval)", self._trade_manager_interval_sec)

    while not self._stop.is_set():
      try:
        await asyncio.sleep(self._trade_manager_interval_sec)

        if not self._ready.is_set():
          self.log.warning("Ready is not set")
          continue

        await asyncio.wait_for(self._manage_all_positions(), timeout=30)
      except asyncio.CancelledError:
        self.log.warning("Trade manager task cancelled, exiting loop")
        raise
      except asyncio.TimeoutError:
        self.log.warning("Trade manager timed out after 30s, will retry next cycle")
      except BaseException as e:
        self.log.warning("Trade manager error (will retry): %s", repr(e))
        traceback.print_exc()

    self.log.warning("Trade manager stopped.")

  async def _manage_all_positions(self) -> None:
    """Check all open positions and apply trailing stop logic."""
    if not self._ready.is_set() or self._conn is None:
      return

    try:
      positions = await self._conn.get_positions()
    except Exception as e:
      self.log.warning("Failed to get positions for management: %s", repr(e))
      return

    # Adjust polling interval based on whether positions exist
    if len(positions) == 0:
      if self._trade_manager_interval_sec != 5:
        self._trade_manager_interval_sec = 5
        self.log.warning("No positions - trade manager interval set to 5s")

      # Check closed positions profit if last_signal exists and has_profit is False
      if self.last_signal is not None and not self.last_signal.get("has_profit"):
        await self._check_closed_positions_profit()
    else:
      if self._trade_manager_interval_sec != 1:
        self._trade_manager_interval_sec = 1
        self.log.warning(f"Positions found ({len(positions)}) - trade manager interval set to 1s")

      # If a position exists, any prior "pending order" should be considered resolved.
      if self.last_signal is not None:
        self.last_signal["has_pending_order"] = False

    # Clean up state for closed positions
    open_position_ids = {p.get("id") for p in positions}
    closed_ids = [pid for pid in self._trade_state if pid not in open_position_ids]
    for pid in closed_ids:
      del self._trade_state[pid]
      self.log.warning(f"Position {pid} closed, removed from trade state")

    for position in positions:
      pos_id = position.get("id")
      # If position is untracked, check if it's a re-entry from a pending limit order
      if pos_id and pos_id not in self._trade_state:
        symbol = position.get("symbol")
        if symbol in self._pending_reentry:
          reentry = self._pending_reentry.pop(symbol)
          self._trade_state[pos_id] = {
              "open_price": reentry["entry_price"],
              "direction": reentry["direction"],
              "symbol": symbol,
              "volume": reentry["volume"],
              "peak_profit": 0.0,
          }
          self.log.warning(f"Re-entry detected for {symbol}: position {pos_id} initialized with entry={reentry['entry_price']}")

      await self._manage_single_position(position)

  async def _manage_single_position(self, position: Dict[str, Any]) -> None:
    """
    MFE/MAE-optimized trailing stop logic (from 90-day backtest).

    Strategy:
      1. Initial SL at entry ± 15 (set on open, catches 75% of winner MAE dips)
      2. profit >= $15 → SL moves to breakeven (entry price)
      3. profit >= $20 → trailing activates: SL follows peak_profit - $5
         This captures the large MFE moves (median winner MFE = $57)
         while giving $5 breathing room to avoid premature exits.

    The trail only ratchets forward (never loosens).
    Re-entry on SL hit is handled by _check_closed_positions_profit.
    """
    position_id = position.get("id")
    if not position_id:
      self.log.warning(f"Position id {position_id} doesn't exist, returning...")
      return

    if position_id not in self._trade_state:
      self.log.warning(f"Position id {position_id} isn't in trade state, returning...")
      return

    state = self._trade_state[position_id]
    open_price = state["open_price"]
    direction = state["direction"]
    current_profit = position.get("profit", 0) or 0

    # Update peak profit tracker
    if current_profit > state["peak_profit"]:
      state["peak_profit"] = current_profit
      self.log.info(f"New peak profit for {position_id}: {current_profit:.2f}")

    peak_profit = state["peak_profit"]

    self.log.info(f"Managing {position_id}: entry={open_price}, dir={direction}, profit={current_profit:.2f}, peak={peak_profit:.2f}")

    be_threshold = self.breakeven_profit
    trail_act = self.trail_activation
    trail_dist = self.trail_distance

    # Direction multiplier: buy = +1 (SL below entry), sell = -1 (SL above entry)
    d = 1 if direction == "buy" else -1

    if peak_profit >= trail_act:
      # Trailing mode: SL follows peak profit with trail_distance gap
      trail_offset = peak_profit - trail_dist
      new_sl = open_price + (trail_offset * d)
    elif peak_profit >= be_threshold:
      # Breakeven mode: lock in entry price
      new_sl = open_price
    else:
      # Below breakeven threshold — initial SL is already set, nothing to adjust
      return

    # Only ratchet forward (never loosen the SL)
    current_sl = position.get("stopLoss")
    if current_sl is not None:
      if direction == "buy" and new_sl <= current_sl:
        return
      if direction == "sell" and new_sl >= current_sl:
        return

    try:
      await asyncio.wait_for(
        self._conn.modify_position(position_id, stop_loss=new_sl),
        timeout=self.rpc_timeout_sec,
      )
      self.log.warning(f"Trailing SL for {position_id}: profit={current_profit:.2f}, peak={peak_profit:.2f}, new SL={new_sl:.2f}")
    except Exception as e:
      self.log.error(f"Failed to modify SL for {position_id}: {e}")

  async def _check_closed_positions_profit(self) -> None:
    """
    Check closed positions since last signal.
    - If profit >= 100000: signal complete, clear last_signal.
    - If profit >= 0 but below threshold: under-performed signal, re-enter at
      last close price + 1 (momentum confirmation) to capture remaining move.
    - If SL was hit (profit < 0): place re-entry at first_entry_price with SL.
    - Repeats until a new signal arrives.
    """
    if self.last_signal is None:
      self.log.warning("_check_closed_positions_profit: last_signal is None, returning")
      return

    try:
      signal_date = datetime.fromisoformat(self.last_signal["date_created"])
      if signal_date.tzinfo is None:
        signal_date = signal_date.replace(tzinfo=timezone.utc)

      history = await self._conn.get_deals_by_time_range(signal_date, datetime.now(timezone.utc))

      if not history:
        self.log.warning("_check_closed_positions_profit: No history deals found, returning")
        return

      total_profit = 0.0
      deal_count = 0
      last_close_price = None

      if isinstance(history, dict):
        deals_list = history.get("deals", [])
      else:
        deals_list = history

      for deal in deals_list:
        if not isinstance(deal, dict):
          continue
        if deal.get("entryType") == "DEAL_ENTRY_OUT":
          profit = deal.get("profit", 0) or 0
          total_profit += profit
          deal_count += 1
          last_close_price = deal.get("price")

      if deal_count == 0:
        self.log.warning("_check_closed_positions_profit: No DEAL_ENTRY_OUT deals found, returning")
        return

      if total_profit >= 0:
        if total_profit >= 100000:
          # Signal complete
          self.last_signal["has_profit"] = True
          self.log.warning(f"Profit: {total_profit:.2f}. Signal complete.")
          await self.delete_all_pending_orders()
          self.last_signal = None
          return
        else:
          # Profit below threshold — re-enter at close price + 1 for momentum confirmation
          self.log.warning(
            f"Profit {total_profit:.2f} below threshold. Re-entering for more."
          )
          # Fall through to re-entry logic below with close price

      # Re-entry logic (SL hit or under-target profit)
      if self.last_signal.get("has_pending_order"):
        # Verify the order still exists before trusting the flag
        try:
          orders = await self._conn.get_orders()
          if orders and len(orders) > 0:
            self.log.warning(f"Re-entry pending (profit={total_profit:.2f}), order still exists, returning")
            return
          else:
            self.last_signal["has_pending_order"] = False
            self.log.warning("Pending order no longer exists, will re-create re-entry order")
        except Exception:
          return

      side = self.last_signal["action"]
      first_entry = self.last_signal["first_entry_price"]
      symbol = self.last_signal.get("symbol", "GOLD")
      volume = self.last_signal.get("volume", 0.01)

      sl_dist = self.initial_sl_distance

      # Choose re-entry price based on reason
      if total_profit >= 0 and last_close_price is not None:
        # Under-target profit: re-enter at close price + 1 (momentum confirmation)
        momentum_offset = 1.0
        if side == "buy":
          reentry_price = round(last_close_price + momentum_offset, 2)
        else:
          reentry_price = round(last_close_price - momentum_offset, 2)
        reason = "under-target"
      else:
        # SL hit: re-enter at first entry price (existing behavior)
        reentry_price = first_entry
        reason = "SL hit"

      if side == "buy":
        sl = reentry_price - sl_dist
      else:
        sl = reentry_price + sl_dist

      # Get current price to decide between limit and stop order
      price_data = await self._conn.get_symbol_price(symbol)
      current_ask = price_data["ask"]
      current_bid = price_data["bid"]

      self.log.warning(
        f"{reason} (profit={total_profit:.2f}). Re-entering: {side} {symbol} "
        f"at {reentry_price:.2f}, SL={sl:.2f}, ask={current_ask}, bid={current_bid}"
      )

      try:
        if side == "buy":
          if reentry_price < current_ask:
            order_response = await self._conn.create_limit_buy_order(
              symbol=symbol, volume=volume, open_price=reentry_price,
              stop_loss=sl,
            )
          else:
            order_response = await self._conn.create_stop_buy_order(
              symbol=symbol, volume=volume, open_price=reentry_price,
              stop_loss=sl,
            )
        else:
          if reentry_price > current_bid:
            order_response = await self._conn.create_limit_sell_order(
              symbol=symbol, volume=volume, open_price=reentry_price,
              stop_loss=sl,
            )
          else:
            order_response = await self._conn.create_stop_sell_order(
              symbol=symbol, volume=volume, open_price=reentry_price,
              stop_loss=sl,
            )

        self.last_signal["has_pending_order"] = True
        self._pending_reentry[symbol] = {
            "entry_price": reentry_price,
            "direction": side,
            "volume": volume,
        }
        self.log.warning(f"Re-entry order created ({reason}): {order_response}")

      except Exception as order_error:
        self.log.warning(f"Failed to create re-entry order: {order_error}")
        traceback.print_exc()

    except Exception as e:
      self.log.warning(f"Error checking closed positions profit: {e}")
      traceback.print_exc()

  async def delete_all_pending_orders(self) -> Dict[str, Any]:
    """
    Delete all pending/limit orders.
    Returns a dict with status and details of deleted orders.
    """
    await self._ensure_ready()
    
    try:
      # Get all pending orders
      orders = await asyncio.wait_for(
        self._conn.get_orders(),
        timeout=self.rpc_timeout_sec,
      )

      if not orders:
        self.log.warning("No pending orders to delete")
        return {"status": "success", "deleted_count": 0, "orders": []}

      order_ids = [o.get("id") for o in orders if o.get("id")]

      async def _cancel(oid: str):
        try:
          await asyncio.wait_for(
            self._conn.cancel_order(oid),
            timeout=self.rpc_timeout_sec,
          )
          self.log.warning(f"Deleted pending order: {oid}")
          return oid
        except Exception as e:
          self.log.warning(f"Failed to delete order {oid}: {e}")
          return None

      results = await asyncio.gather(*(_cancel(oid) for oid in order_ids))
      deleted_orders = [oid for oid in results if oid]

      # Reset has_pending_order flag in last_signal if exists
      if self.last_signal is not None:
        self.last_signal["has_pending_order"] = False

      self.log.warning(f"Deleted {len(deleted_orders)} pending orders")
      return {
        "status": "success",
        "deleted_count": len(deleted_orders),
        "orders": deleted_orders
      }

    except asyncio.TimeoutError as e:
      self.log.warning("delete_all_pending_orders timed out. Triggering reconnect...")
      await self._trigger_reconnect()
      return {"status": "error", "error": "timeout"}
    except Exception as e:
      self.log.warning(f"Error deleting pending orders: {e}")
      traceback.print_exc()
      return {"status": "error", "error": str(e)}

  async def _trigger_reconnect(self) -> None:
    """
    Makes sure no more than one reconnection attempt is in progress at a time.
    Applies backoff + jitter.
    """
    async with self._reconnect_lock:
      if self._stop.is_set():
        return

      # Another task already reconnected while we waited for the lock
      if self._ready.is_set():
        return

      self._ready.clear()

      delay = self.reconnect_base_delay_sec
      while not self._stop.is_set():
        try:
          await self._disconnect()
          await self._connect_and_sync()
          return
        except Exception as e:
          jitter = random.uniform(0, 0.5)
          sleep_for = min(self.reconnect_max_delay_sec, delay + jitter)
          self.log.warning("Reconnect failed (%s). Retrying in %.1fs...", repr(e), sleep_for)
          await asyncio.sleep(sleep_for)
          delay = min(self.reconnect_max_delay_sec, delay * 2)

  async def _disconnect(self) -> None:
    self._ready.clear()

    try:
      if self._conn:
        await self._conn.close()
        # Give MetaApi internal tasks (unsubscribe etc.) time to complete
        await asyncio.sleep(1)
    except Exception:
      pass

    self._conn = None
    self._account = None
    self._api = None


# ----------------------------
# Example usage
# ----------------------------
async def main():
  logging.basicConfig(level=logging.INFO)

  fm = ForexManager(
    token="YOUR_METAAPI_TOKEN",
    account_id="YOUR_ACCOUNT_ID",
    # region="agiliumtrade.agiliumtrade.ai",  # if needed
    heartbeat_interval_sec=10,
  )

  await fm.start()
  await fm.wait_ready(timeout=60)

  order = await fm.open_market_order(
    symbol="EURUSD",
    side="buy",
    volume=0.01,
    stop_loss=None,
  )
  print("ORDER:", order)

  pos = await fm.get_positions()
  print("POSITIONS:", pos)

  await fm.stop()


if __name__ == "__main__":
  asyncio.run(main())
