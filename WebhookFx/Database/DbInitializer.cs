using Npgsql;

namespace WebhookFx.Database;

public static class DbInitializer
{
    public static async Task InitializeAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "Signals" (
                "Id" SERIAL PRIMARY KEY,
                "open_time" TIMESTAMP NOT NULL,
                "action" VARCHAR(50) NOT NULL,
                "pair" VARCHAR(50) NOT NULL,
                "entry_tag" VARCHAR(100) NOT NULL,
                "alert_message" VARCHAR(500) NOT NULL,
                "comment" VARCHAR(500) NOT NULL,
                "open_price" DECIMAL NOT NULL,
                "allow_multiple" BOOLEAN NOT NULL,
                "size" DECIMAL NOT NULL,
                "close_time" TIMESTAMP,
                "close_price" DECIMAL,
                "IdempotencyKey" VARCHAR(255) UNIQUE
            );

            CREATE TABLE IF NOT EXISTS "Requests" (
                "Id" SERIAL PRIMARY KEY,
                "request_date" TIMESTAMP NOT NULL,
                "json" TEXT NOT NULL,
                "is_valid" SMALLINT NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS "OutboxEvents" (
                "Id"             SERIAL PRIMARY KEY,
                "SignalId"       INT          NOT NULL REFERENCES "Signals"("Id"),
                "EventType"      VARCHAR(100) NOT NULL DEFAULT 'signal.created',
                "Payload"        JSONB        NOT NULL,
                "RetryCount"     INT          NOT NULL DEFAULT 0,
                "CreatedAt"      TIMESTAMP    NOT NULL DEFAULT NOW(),
                "ProcessedAt"    TIMESTAMP,
                "FailedAt"       TIMESTAMP,
                "ErrorMessage"   TEXT,
                "IdempotencyKey" VARCHAR(255) UNIQUE
            );

            CREATE OR REPLACE FUNCTION notify_outbox_insert()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('outbox_new', NEW."Id"::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_outbox_notify ON "OutboxEvents";
            CREATE TRIGGER trg_outbox_notify
                AFTER INSERT ON "OutboxEvents"
                FOR EACH ROW EXECUTE FUNCTION notify_outbox_insert();
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
