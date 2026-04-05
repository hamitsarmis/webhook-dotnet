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
                "close_price" DECIMAL
            );

            CREATE TABLE IF NOT EXISTS "BadRequests" (
                "Id" SERIAL PRIMARY KEY,
                "timestamp" TIMESTAMP NOT NULL,
                "request_type" VARCHAR(100) NOT NULL,
                "body" TEXT NOT NULL,
                "reason" VARCHAR(500) NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Requests" (
                "Id" SERIAL PRIMARY KEY,
                "request_date" TIMESTAMP NOT NULL,
                "json" TEXT NOT NULL,
                "is_valid" SMALLINT NOT NULL DEFAULT 0
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
