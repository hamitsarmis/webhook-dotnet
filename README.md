# webhook-dotnet

Turns TradingView alerts into broker orders, and lets an AI agent read and operate the same pipeline through an MCP server.

A TradingView webhook lands at a .NET 10 endpoint, which stores the signal in Postgres together with an outbox row in one transaction. An outbox processor wakes on Postgres `LISTEN/NOTIFY` and publishes the event to RabbitMQ. A Python worker consumes it and places or closes the order through MetaApi. The MCP server reads the same tables, and when it writes it posts to the same endpoint, so the position rules, the idempotency keys and the outbox stay in one place.

## Architecture

```
 TradingView ──HTTP──▶ ┌───────────┐      ┌──────────────────────────────┐
  (IP whitelist)       │ WebhookFx │─────▶│ Postgres                     │
                       │  :8089    │  tx  │  Signals · OutboxEvents      │
 Claude Code           └───────────┘      │  IdempotencyKeys · Requests  │
     │ stdio                 ▲            │  AgentActions                │
     ▼                       │            └──────┬───────────────▲───────┘
 ┌───────────────┐  POST + X-Internal-Key        │ NOTIFY        │
 │ WebhookFx.Mcp │───────────┘                   ▼               │ SELECT,
 │  7 tools      │                       ┌─────────────────┐     │ INSERT audit
 │  dry-run      │───────────────────────┼─────────────────┼─────┘
 └───────────────┘                       │ OutboxProcessor │
                                         └────────┬────────┘
                                                  ▼
                              RabbitMQ (topic exchange "signals")
                                                  ▼
                                   signal_processor ──▶ MetaApi ──▶ broker
```

## Components

**`WebhookFx/`** — ASP.NET Core minimal API on port 8089. Admits requests from four TradingView addresses, or requests that carry the internal key. Parses the body, hands it to `SignalService`, and maps the outcome to an HTTP answer. Every request goes to `logs/access.log`; every body that reaches the handler goes to the `Requests` table.

**`WebhookFx.Core/`** — class library shared by the API and the MCP server. `PositionRules` decides open or close and open / no-op / reversal with no I/O. `SignalService` applies a request and writes the outbox rows in the same transaction. `DbInitializer` creates the schema.

**`OutboxProcessor/`** — worker that drains `OutboxEvents` to the RabbitMQ exchange `signals`. It listens on the Postgres channel `outbox_new` and also polls every 30 seconds as a fallback.

**`signal_processor/`** — Python worker (pika, MetaApi SDK). `signal.created` opens a market order for `pair`, `action`, `lot`; `signal.closed` closes all positions on the symbol.

**`WebhookFx.Mcp/`** — MCP server over stdio for Claude Code and other MCP clients. Four read tools, one preview tool, two write tools. See [Agent access (MCP)](#agent-access-mcp).

**Tests** — `WebhookFx.Core.Tests` (rules, keys), `WebhookFx.Tests` (the endpoint against a real Postgres via Testcontainers), `WebhookFx.Mcp.Tests` (tokens, tool handlers against fakes, the server over stdio).

## Quick start

```bash
cp signal_processor/.env.example signal_processor/.env   # fill in METAAPI_TOKEN, METAAPI_ACCOUNT_ID
cp .env.example .env                                     # optional: change the default credentials
docker compose up -d --build
dotnet test webhook.slnx                                 # needs Docker for the Postgres container
```

Only `webhookfx` publishes a host port (8089). TradingView posts to `http://<host>:8089/webhookfx`.

## Request shape

```json
{
  "action": "buy",
  "pair": "GOLD",
  "entry_tag": "gold_strategy",
  "alert_message": "Scripted Long",
  "comment": "Scripted Long",
  "price": 4569.440,
  "allow_multiple": true,
  "lot": 0.01
}
```

A request is a close when `alert_message` or `comment` contains `Exit`, `SL` or `TP`; anything else is an open. Examples: [open_position_request.json](open_position_request.json), [close_position_request.json](close_position_request.json).

Position rules, in [WebhookFx.Core/PositionRules.cs](WebhookFx.Core/PositionRules.cs):

- Same pair, same direction already open → no-op. `buy` and `Long` are one direction, `sell` and `Short` the other.
- Same pair, opposite direction → close the open position, then open the new one (reversal).
- Close request with nothing open → 404.

## Idempotency

A caller can send an idempotency key, as the `Idempotency-Key` header or as an `idempotency_key` field in the body (the header wins).

- The first request with a key is handled normally. Its outcome is stored in `IdempotencyKeys` in the same transaction as the signal and outbox rows.
- A later request with the same key and the same body gets the same status and text again, plus the response header `Idempotent-Replayed: true`, and writes nothing.
- The same key with a different body is answered with 422.
- Requests arriving at the same time with one key are serialised by the database: one is handled, the others are replays.

A TradingView alert message can build a key from its placeholders, for example `"idempotency_key": "{{ticker}}-{{strategy.order.id}}-{{time}}"`. Requests without a key are handled each on its own.

## Agent access (MCP)

`WebhookFx.Mcp` exposes the pipeline to an agent. Register it with Claude Code from the repository root:

```json
{
  "mcpServers": {
    "webhookfx": {
      "command": "dotnet",
      "args": ["run", "--project", "WebhookFx.Mcp"],
      "env": {
        "ConnectionStrings__Default": "Host=127.0.0.1;Port=5432;Database=webhookfx;Username=postgres;Password=postgres",
        "WEBHOOKFX_URL": "http://127.0.0.1:8089"
      }
    }
  }
}
```

Save that as `.mcp.json`, or run `claude mcp add webhookfx -- dotnet run --project WebhookFx.Mcp`. The server needs to reach Postgres; `docker-compose.mcp.yml` publishes it on loopback.

| Tool | Kind | What it does |
|---|---|---|
| `get_health` | read | database reachable, outbox backlog, time of the last signal, safety mode |
| `list_open_positions` | read | open signals; the one a close would act on carries a `close_confirmation_token` |
| `get_signal_history` | read | recent signals, newest first; filters `pair`, `since`; `limit` up to 200 |
| `get_outbox_status` | read | pending, published and failed outbox events |
| `preview_open_position` | read | runs the shared rules, says open / no_op / reversal, returns a `confirmation_token` |
| `open_position` | write | needs the token; posts to `/webhookfx` |
| `close_position` | write | needs the close token; posts to `/webhookfx` |

A session in the default configuration:

```
> preview_open_position(pair="GOLD", action="buy", lot=0.01, entry_tag="manual")
< {"would":"open","explanation":"No position is open on GOLD. Open a buy of 0.01 lot. ...","confirmation_token":"v1.1793873738.…","expires_in_seconds":60,"dry_run":true,"live_allowed":false}
> open_position(pair="GOLD", action="buy", lot=0.01, entry_tag="manual", confirmation_token="v1.1793873738.…")
< {"status":"dry_run","dry_run":true,"detail":"Dry run, nothing was sent. Would have done: No position is open on GOLD. Open a buy of 0.01 lot.","correlation_id":"6685EF43951671E06D6E37F3175E2625"}
```

Security model, in the order a write passes through it:

1. **Two switches.** `MCP_DRY_RUN` defaults to `true`: write tools validate, log and audit, and make no HTTP call. Turning it off is not enough; `MCP_ALLOW_LIVE` must also be `true`, otherwise the write is refused. Only the words `false` and `true` flip them (in any casing); any other value, a typo included, keeps the safe setting.
2. **Confirmation token.** HMAC-SHA256 over the arguments and the position state the preview saw. Valid 60 seconds, once. It is refused for other arguments, after the position has changed, on a second use and after a restart of the server.
3. **Lot cap.** `MCP_MAX_LOT`, default `0.1`.
4. **Audit row first.** Every write call inserts into `AgentActions`, refusals and dry runs included. A live order is sent only after its `submitted` row is stored.
5. **Internal key.** The server posts to `/webhookfx` with `X-Internal-Key`. The API compares it in constant time against `WEBHOOKFX_INTERNAL_KEY`; a valid key admits that one request, the IP whitelist is unchanged, and the signal is stored with `source = 'mcp'`. The token's nonce is sent as the `Idempotency-Key`, so one token cannot produce two orders.

Setup for live use, the least-privilege database role, the audit queries and the token format are in [docs/mcp.md](docs/mcp.md).

## Configuration

| Variable | Purpose | Default |
|---|---|---|
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | Postgres database and credentials; compose builds `ConnectionStrings__Default` from them | `webhookfx`, `postgres`, `postgres` |
| `RABBITMQ_USER`, `RABBITMQ_PASSWORD` | RabbitMQ credentials for the broker and the Python worker | `guest`, `guest` |
| `WEBHOOKFX_INTERNAL_KEY` | Shared secret for the internal path, at least 32 characters. Empty closes the path. | empty |
| `ConnectionStrings__Default` | Postgres connection string for `WebhookFx`, `OutboxProcessor` and `WebhookFx.Mcp` | set by compose; required for the MCP server |
| `RabbitMQ__Host` | RabbitMQ host for `OutboxProcessor` | `localhost`; `rabbitmq` in compose |
| `Polling__FallbackIntervalMs`, `Polling__BatchSize` | `OutboxProcessor` fallback poll interval and batch size | `30000`, `50` |
| `METAAPI_TOKEN`, `METAAPI_ACCOUNT_ID` | MetaApi credentials, in `signal_processor/.env` | required |
| `ENABLE_TRADE_MANAGER`, `ENABLE_INITIAL_SL_TP` | Python worker: trailing/breakeven manager; stop-loss and take-profit right after a fill | off |
| `SIGNAL_PREFETCH_COUNT` | Python worker: unacknowledged messages in flight | `20` |
| `MCP_DRY_RUN` | MCP server: `false` turns dry-run off | `true` |
| `MCP_ALLOW_LIVE` | MCP server: `true` allows real orders once dry-run is off | `false` |
| `MCP_MAX_LOT` | MCP server: largest lot a write tool accepts | `0.1` |
| `WEBHOOKFX_URL` | MCP server: base URL of the API | `http://localhost:8089` |
| `MCP_POSTGRES_PORT` | Host port for Postgres in `docker-compose.mcp.yml` (loopback only) | `5432` |

Allowed TradingView addresses are hard-coded in [WebhookFx/Middleware/IpWhitelistMiddleware.cs](WebhookFx/Middleware/IpWhitelistMiddleware.cs).

## Design decisions

- **Outbox instead of publishing from the request.** The signal and its event are committed together, so a crash between "stored" and "published" loses nothing. Trade-off: one more service, and delivery to RabbitMQ is at-least-once.
- **`LISTEN/NOTIFY` with a polling fallback.** An insert trigger notifies the processor, so there is no polling delay in the normal case; the 30-second poll covers a missed notification. Trade-off: a dedicated database connection that must stay open.
- **Agent writes go through the webhook endpoint.** The MCP server's code never writes to `Signals`, and with the database role in [docs/mcp.md](docs/mcp.md) it cannot. Rules, idempotency and outbox exist once. Trade-off: the API must be up, and the agent's orders carry price 0 because the server has no price feed.
- **Preview, then confirm.** A token ties the write to what the user saw, including the state of the position. Trade-off: two calls per write, and a token is lost if the server restarts.
- **Idempotency keys come from the caller.** Only the caller knows that two deliveries are the same request. Trade-off: a caller that sends no key gets no protection.
- **Raw Npgsql, no ORM.** A handful of statements, each visible where it runs. Trade-off: schema changes are hand-written `IF NOT EXISTS` statements, with no migration history.

## Caveats

- The Python worker does not deduplicate redelivered RabbitMQ messages, and it reads from an exclusive queue: events published while it is down never reach it.
- `OutboxProcessor` declares a durable queue `signals.process` that nothing consumes. It grows with every event.
- An outbox event whose publish fails is marked failed and not retried.
- `OutboxProcessor` connects to RabbitMQ as `guest`/`guest` regardless of `RABBITMQ_USER` and `RABBITMQ_PASSWORD`.
- Close detection is a substring match. An alert message such as "Tesla breakout" contains "sl" and is handled as a close.
- A close request closes the oldest open signal of the pair; an open is judged against the newest. `allow_multiple` is stored and not used.
- Idempotency keys are never deleted.
- Signals opened or closed through MCP are stored with price 0.
- A confirmation token is checked against the position just before the request is sent; a TradingView signal that lands in between is not detected.
- The MCP transport is stdio only. There is no HTTP transport and no MCP service in compose.
- This places real orders with real money. Run it against a demo account first.

## License

MIT, see [LICENSE](LICENSE).
