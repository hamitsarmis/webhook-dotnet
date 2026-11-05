# WebhookFx.Mcp

Details behind the "Agent access (MCP)" section of the README: how to run the server, what each safety layer does, and how to read the audit trail.

## Running it

The server is a console program that speaks MCP over stdio. stdout carries the protocol; every log line goes to stderr.

```bash
# 1. Start the pipeline with Postgres published on loopback
docker compose -f docker-compose.yml -f docker-compose.mcp.yml up -d --build

# 2. Register the server with Claude Code (from the repository root)
claude mcp add webhookfx \
  -e ConnectionStrings__Default="Host=127.0.0.1;Port=5432;Database=webhookfx;Username=postgres;Password=postgres" \
  -e WEBHOOKFX_URL=http://127.0.0.1:8089 \
  -- dotnet run --project WebhookFx.Mcp

# 3. In Claude Code: /mcp lists the server and its 7 tools
```

Started like this the server is in dry-run mode and has no internal key. All read tools work. The write tools validate, audit and answer `"status": "dry_run"`.

## Going live

Three things have to be true at the same time. Each is set by the operator, none by the agent.

| Where | Setting | Value |
|---|---|---|
| `.env` of the compose stack | `WEBHOOKFX_INTERNAL_KEY` | at least 32 characters, for example `openssl rand -hex 32` |
| environment of the MCP server | `WEBHOOKFX_INTERNAL_KEY` | the same value |
| environment of the MCP server | `MCP_DRY_RUN` | `false` |
| environment of the MCP server | `MCP_ALLOW_LIVE` | `true` |

What happens when one is missing:

| `MCP_DRY_RUN` | `MCP_ALLOW_LIVE` | key | A confirmed write |
|---|---|---|---|
| unset, or any value other than the word `false` | any | any | dry run, no HTTP call |
| `false` | unset, or any value other than the word `true` | any | refused |
| `false` | `true` | unset | refused |
| `false` | `true` | set, wrong | sent, API answers 403, reported as `failed` |
| `false` | `true` | set, right | sent |

The two words are matched without regard to casing or surrounding spaces; `MCP_DRY_RUN=flase` is a dry run. `get_health` and every preview report the current mode (`dry_run`, `live_allowed`).

## The path of a write

```
open_position(pair, action, lot, entry_tag, confirmation_token)
   │
   ├─ arguments valid?            pair pattern, action buy|sell, 0 < lot <= MCP_MAX_LOT, entry_tag 1..100
   ├─ switches allow it?          dry run, or live with key
   ├─ read the open position      same query the API uses (PositionQueries)
   ├─ run the shared rules        PositionRules.DecideOpen -> open | no_op | reversal
   ├─ redeem the token            signature covers arguments + decision + position id
   │
   ├─ dry run:  audit "dry_run"   -> answer, nothing sent
   └─ live:     audit "submitted" -> POST /webhookfx -> audit "executed: ...", "failed: ..." or "unknown: ..."
```

Every "no" on the way down ends in an audit row `refused: <reason>` and an answer with `"status": "refused"`. If the audit row cannot be written, nothing is sent.

`close_position` follows the same path. Its token comes from `list_open_positions` and is bound to the pair and to the ID of the signal a close would act on (the oldest open one).

## Confirmation tokens

```
v1.<expiry, unix seconds>.<nonce, 32 hex>.<HMAC-SHA256, base64url>
```

The HMAC covers `v1.<expiry>.<nonce>` and a canonical string of what was previewed:

```
open_position|<pair>|<action>|<lot>|<entry_tag>|<Open|NoOp|Reversal>|<id of the open signal, or none>
close_position|<pair>|<id of the signal to close>
```

Free-text fields are length-prefixed, so a value containing `|` cannot imitate another field.

| Situation | Result |
|---|---|
| other pair, action, lot or entry_tag than previewed | refused, token stays usable for the original arguments |
| the position on the pair changed since the preview | refused |
| more than 60 seconds old | refused |
| used before | refused |
| issued by another server process, or altered | refused |

The signing key is 32 random bytes generated at start. It is never written anywhere, so a restart invalidates all outstanding tokens. Redeemed nonces are kept in memory until their token would have expired anyway.

The nonce doubles as the `Idempotency-Key` of the request to `/webhookfx`. The API stores it with the outcome, so even a replay that somehow got past the server would be answered from the record instead of trading again.

## The internal key

`POST /webhookfx` normally accepts only four TradingView addresses. A request with the header `X-Internal-Key` is judged on the key alone:

- equal to `WEBHOOKFX_INTERNAL_KEY`: admitted, and a signal it opens is stored with `source = 'mcp'`;
- anything else, or no key configured: 403, also from a whitelisted address.

The comparison hashes both values and uses `CryptographicOperations.FixedTimeEquals`. A configured key shorter than 32 characters stops `WebhookFx` and the MCP server at startup. The whitelist itself is not changed by any of this.

## Database access

The server reads two tables and inserts into one. It does not need the owner's credentials:

```sql
CREATE ROLE webhookfx_mcp LOGIN PASSWORD '<choose one>';
GRANT SELECT ON "Signals", "OutboxEvents" TO webhookfx_mcp;
GRANT INSERT ON "AgentActions" TO webhookfx_mcp;
GRANT USAGE ON SEQUENCE "AgentActions_id_seq" TO webhookfx_mcp;
```

With that role every tool works, and the server cannot change a signal, cannot edit or delete an audit row, and cannot read the idempotency keys. The tables are created by `WebhookFx` at startup, so start the API once before creating the role.

## Audit trail

```sql
"AgentActions" (id, tool, args jsonb, dry_run, outcome, correlation_id, created_at)
```

| `outcome` | Meaning |
|---|---|
| `dry_run` | a confirmed write in dry-run mode; nothing was sent |
| `refused: <reason>` | stopped before sending |
| `submitted` | about to be sent; written before the HTTP call |
| `executed: <status> <text>` | the API accepted it |
| `failed: <status> <text>` | the API answered and said no; nothing was changed |
| `unknown: <error>` | the API did not answer (connection error, or the 15-second timeout); the order may or may not have been stored |

`args` holds the tool's arguments without the token. A live write has two rows with the same `correlation_id`, and that ID is the idempotency key the API stored:

```sql
-- everything the agent did today
SELECT created_at, tool, dry_run, outcome, args FROM "AgentActions"
WHERE created_at >= CURRENT_DATE ORDER BY id;

-- submitted orders with no definite answer (timeout, or the server died mid-call): did they arrive?
-- "Outcome" is NULL when the API never stored the request, otherwise what it did with it.
SELECT a.correlation_id, a.tool, a.created_at, k."Outcome"
FROM "AgentActions" a LEFT JOIN "IdempotencyKeys" k ON k."Key" = a.correlation_id
WHERE a.outcome = 'submitted'
  AND NOT EXISTS (SELECT 1 FROM "AgentActions" r
                  WHERE r.correlation_id = a.correlation_id
                    AND (r.outcome LIKE 'executed:%' OR r.outcome LIKE 'failed:%'));

-- signals opened by the agent
SELECT "Id", pair, action, size, open_time, close_time FROM "Signals" WHERE source = 'mcp';
```

## Limits

- stdio transport only.
- `action` is limited to `buy` and `sell`, the two values the Python worker passes to the broker.
- The server has no price feed. Its requests carry `price: 0`, so `open_price` and `close_price` of agent-made changes are 0. Orders fill at market.
- The position is re-read just before the request is sent, not locked. A TradingView signal arriving in that gap is not detected by the token; the API's own rules still apply to both requests.
- Refusals, failures and unknown outcomes are returned as a normal tool result with a `status` field, not as an MCP error.
- `close_position` closes what the API's close rule closes: the oldest open signal of the pair, and the worker then closes every position on that symbol.
