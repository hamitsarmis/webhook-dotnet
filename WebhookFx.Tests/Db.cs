using Npgsql;

namespace WebhookFx.Tests;

public sealed record SignalRow(
    int Id, string Action, string Pair, string EntryTag, decimal OpenPrice, decimal Size,
    DateTime? CloseTime, decimal? ClosePrice, string? IdempotencyKey);

public sealed record OutboxRow(int Id, int SignalId, string EventType, string Payload, string? IdempotencyKey);

/// <summary>Read-only helpers the tests use to look at what the endpoint wrote.</summary>
public static class Db
{
    public static async Task<List<SignalRow>> SignalsAsync(this PipelineFixture fixture)
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "action", "pair", "entry_tag", "open_price", "size", "close_time", "close_price", "IdempotencyKey"
            FROM "Signals" ORDER BY "Id"
            """;
        var rows = new List<SignalRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SignalRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDecimal(4), reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return rows;
    }

    public static async Task<List<OutboxRow>> OutboxAsync(this PipelineFixture fixture)
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "SignalId", "EventType", "Payload"::text, "IdempotencyKey"
            FROM "OutboxEvents" ORDER BY "Id"
            """;
        var rows = new List<OutboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

    public static async Task<long> CountAsync(this PipelineFixture fixture, string table)
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""SELECT COUNT(*) FROM "{table}" """;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
