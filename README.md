# webhook

TradingView → MetaApi forex trade execution pipeline.

A webhook from TradingView lands at the .NET endpoint, gets persisted in Postgres with an outbox row, the outbox processor publishes it to RabbitMQ, and the Python signal processor turns it into a live MetaApi trade.

## Architecture

```
TradingView ──HTTP──▶ WebhookFx ──▶ Postgres (Signals + OutboxEvents)
                                            │
                                            ▼
                                  OutboxProcessor ──▶ RabbitMQ (topic: signals)
                                                              │
                                                              ▼
                                                      signal_processor ──▶ MetaApi
```

### Services

| Service | Stack | Responsibility |
| --- | --- | --- |
| [WebhookFx/](WebhookFx/) | .NET 8 minimal API | Receives webhooks (port 8089), IP-whitelists TradingView, deduplicates via SHA-256 idempotency keys, opens/reverses/closes positions in `Signals`, writes outbox row in same tx |
| [OutboxProcessor/](OutboxProcessor/) | .NET 8 worker | Drains `OutboxEvents` to RabbitMQ exchange `signals` using Postgres LISTEN/NOTIFY with a polling fallback |
| [signal_processor/](signal_processor/) | Python + pika + MetaApi SDK | Consumes `signals` exchange and executes market orders / closes through `ForexManager` |
| postgres | postgres:17 | Stores signals, outbox events, and request log |
| rabbitmq | rabbitmq:4-management | Topic exchange `signals` |

## Request shape

Webhook body posted to `POST /webhookfx`:

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

A request is treated as a close when `alert_message` or `comment` contains `Exit`, `SL`, or `TP`. See [open_position_request.json](open_position_request.json) and [close_position_request.json](close_position_request.json) for examples.

Position rules in [WebhookFx/Program.cs](WebhookFx/Program.cs):
- Same pair, same direction already open → no-op.
- Same pair, opposite direction → close existing, open new (reversal).
- Close request with no open position → 404.

## Running

The whole stack runs via Docker Compose. Only `webhookfx` exposes a host port (8089); other services are reachable only on the internal compose network.

```bash
cp signal_processor/.env.example signal_processor/.env
# fill in METAAPI_TOKEN and METAAPI_ACCOUNT_ID

# Optional: override the default postgres / rabbitmq credentials
cp .env.example .env
# edit .env if you want to change passwords from the published defaults

docker compose up -d --build
```

The `.NET` services read their Postgres connection string from the
`ConnectionStrings__Default` env var, which `docker-compose.yml` builds from
`POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD`. If `.env` is absent the
defaults from [.env.example](.env.example) are used.

See [docker-compose.yml](docker-compose.yml) for the full wiring.

## Configuration

- **Allowed TradingView IPs** are hardcoded in [WebhookFx/Middleware/IpWhitelistMiddleware.cs](WebhookFx/Middleware/IpWhitelistMiddleware.cs).
- **MetaApi credentials and trade manager flag** live in `signal_processor/.env` (see [.env.example](signal_processor/.env.example)).
- **Logs** are written to `./logs/` (mounted into `webhookfx`): `access.log` for every request and `errors.log` for exceptions.
