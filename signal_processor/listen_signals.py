import os
import asyncio
import concurrent.futures
import logging
import threading
from functools import partial

import pika
import json
from dotenv import load_dotenv

from forex_manager import ForexManager

load_dotenv()

# ---------------------------------------------------------------------------
# Logger setup – mirrors forex_manager's pattern, writes to signals.log
# ---------------------------------------------------------------------------
logger = logging.getLogger("ListenSignals")
logger.setLevel(logging.DEBUG)

if not logger.handlers:
    formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')

    console_handler = logging.StreamHandler()
    console_handler.setLevel(logging.INFO)
    console_handler.setFormatter(formatter)
    logger.addHandler(console_handler)

    file_handler = logging.FileHandler('signals.log')
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)

RABBITMQ_HOST = os.getenv("RABBITMQ_HOST", "localhost")
RABBITMQ_PORT = int(os.getenv("RABBITMQ_PORT", "5672"))
RABBITMQ_USERNAME = os.getenv("RABBITMQ_USERNAME", "guest")
RABBITMQ_PASSWORD = os.getenv("RABBITMQ_PASSWORD", "guest")
RABBITMQ_EXCHANGE = os.getenv("RABBITMQ_EXCHANGE", "signals")

METAAPI_TOKEN = os.getenv("METAAPI_TOKEN")
METAAPI_ACCOUNT_ID = os.getenv("METAAPI_ACCOUNT_ID")

# Allow up to N unacked messages in flight so multiple signals can be processed
# concurrently on the asyncio loop (per-symbol locks enforce ordering).
PREFETCH_COUNT = int(os.getenv("SIGNAL_PREFETCH_COUNT", "20"))

ENABLE_TRADE_MANAGER = os.getenv("ENABLE_TRADE_MANAGER", "").lower() in ("1", "true", "yes", "on")


async def _process_signal(event_type, signal, forex_manager):
    """Run a single signal on the asyncio loop. Raises on transient failure."""
    if event_type == "signal.closed":
        logger.info("[CLOSE] %s %s %s @ %s | tag=%s comment=\"%s\"",
                    signal['action'].upper(), signal['lot'], signal['pair'],
                    signal['price'], signal.get('entry_tag'), signal.get('comment'))
        await forex_manager.close_positions_for_symbol(signal['pair'])
    elif event_type == "signal.created":
        logger.info("[OPEN] %s %s %s @ %s | tag=%s comment=\"%s\"",
                    signal['action'].upper(), signal['lot'], signal['pair'],
                    signal['price'], signal.get('entry_tag'), signal.get('comment'))
        await forex_manager.open_market_order(
            symbol=signal['pair'],
            side=signal['action'],
            volume=float(signal['lot']),
        )
    else:
        logger.warning("Unknown EventType: %s", event_type)


def _ack_safe(connection, channel, delivery_tag):
    connection.add_callback_threadsafe(
        lambda: channel.basic_ack(delivery_tag=delivery_tag)
    )


def _nack_safe(connection, channel, delivery_tag, requeue):
    connection.add_callback_threadsafe(
        lambda: channel.basic_nack(delivery_tag=delivery_tag, requeue=requeue)
    )


def _schedule_signal(event_type, signal, forex_manager, loop, connection, channel, delivery_tag, attempt):
    """Schedule _process_signal on the asyncio loop and wire up ack/nack/retry."""
    future = asyncio.run_coroutine_threadsafe(
        _process_signal(event_type, signal, forex_manager),
        loop,
    )

    def _on_done(fut):
        try:
            fut.result()
            _ack_safe(connection, channel, delivery_tag)
        except concurrent.futures.CancelledError:
            # The asyncio task was cancelled mid-flight (usually because
            # MetaApi's RPC connection was torn down by an internal reconnect).
            # open_market_order's _ensure_ready() will block until the
            # connection is healthy again, so a single retry is safe.
            if attempt == 0:
                logger.warning("Signal task cancelled, retrying once")
                _schedule_signal(event_type, signal, forex_manager, loop,
                                 connection, channel, delivery_tag, attempt=1)
            else:
                logger.error("Signal task cancelled again after retry, giving up",
                             exc_info=True)
                _ack_safe(connection, channel, delivery_tag)
        except (asyncio.TimeoutError, concurrent.futures.TimeoutError) as e:
            logger.error("Signal timed out, requeuing: %s", e, exc_info=True)
            _nack_safe(connection, channel, delivery_tag, requeue=True)
        except Exception as e:
            logger.error("Failed to process signal: %s", e, exc_info=True)
            _ack_safe(connection, channel, delivery_tag)

    future.add_done_callback(_on_done)


def on_message(channel, method, properties, body, *, forex_manager, loop, connection):
    """
    Pika consumer callback. Schedules the signal on the asyncio loop and
    returns immediately so the consumer thread can pick up the next message.
    Ack/nack happens from the IO thread once the asyncio task finishes.
    """
    delivery_tag = method.delivery_tag

    try:
        message = json.loads(body)
    except json.JSONDecodeError:
        logger.warning("Non-JSON message: %s", body.decode())
        channel.basic_ack(delivery_tag=delivery_tag)
        return

    event_type = message.get("EventType", "")
    signal = message.get("Payload", {})
    if isinstance(signal, str):
        try:
            signal = json.loads(signal)
        except json.JSONDecodeError:
            logger.warning("Payload is not valid JSON: %s", signal)
            channel.basic_ack(delivery_tag=delivery_tag)
            return

    _schedule_signal(event_type, signal, forex_manager, loop,
                     connection, channel, delivery_tag, attempt=0)


def run_consumer(channel, queue_name, forex_manager, loop, connection):
    """Runs pika blocking consumer in a separate thread."""
    callback = partial(
        on_message,
        forex_manager=forex_manager,
        loop=loop,
        connection=connection,
    )
    channel.basic_consume(queue=queue_name, on_message_callback=callback)

    logger.info("Listening for signals on topic exchange '%s'... (Ctrl+C to exit)", RABBITMQ_EXCHANGE)
    channel.start_consuming()


async def async_main():
    forex_manager = ForexManager(
        token=METAAPI_TOKEN,
        account_id=METAAPI_ACCOUNT_ID,
        enable_trade_manager=ENABLE_TRADE_MANAGER,
    )
    await forex_manager.start()
    await forex_manager.wait_ready(timeout=60)

    credentials = pika.PlainCredentials(RABBITMQ_USERNAME, RABBITMQ_PASSWORD)
    connection = pika.BlockingConnection(
        pika.ConnectionParameters(host=RABBITMQ_HOST, port=RABBITMQ_PORT, credentials=credentials)
    )
    channel = connection.channel()

    channel.exchange_declare(exchange=RABBITMQ_EXCHANGE, exchange_type="topic", durable=True)
    channel.basic_qos(prefetch_count=PREFETCH_COUNT)

    result = channel.queue_declare(queue="", exclusive=True)
    queue_name = result.method.queue

    channel.queue_bind(exchange=RABBITMQ_EXCHANGE, queue=queue_name, routing_key="#")

    loop = asyncio.get_running_loop()
    consumer_thread = threading.Thread(
        target=run_consumer,
        args=(channel, queue_name, forex_manager, loop, connection),
        daemon=True,
    )
    consumer_thread.start()

    try:
        # Keep the asyncio loop running so background tasks (heartbeat, trade manager) work
        while consumer_thread.is_alive():
            await asyncio.sleep(1)
    except KeyboardInterrupt:
        logger.info("Stopping...")
    finally:
        channel.stop_consuming()
        consumer_thread.join(timeout=5)
        await forex_manager.stop()
        connection.close()


if __name__ == "__main__":
    asyncio.run(async_main())
