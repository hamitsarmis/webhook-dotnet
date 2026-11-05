using Npgsql;

namespace WebhookFx.Core;

/// <summary>An open row in Signals, reduced to what the rules need.</summary>
public sealed record OpenPosition(int Id, string Action);

public static class PositionQueries
{
    /// <summary>
    /// The position an open request on <paramref name="pair"/> is judged against:
    /// the most recently opened signal that has not been closed, or null.
    /// </summary>
    public static async Task<OpenPosition?> FindLatestOpenAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string pair)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT "Id", "action" FROM "Signals"
            WHERE "pair" = @pair AND "close_time" IS NULL
            ORDER BY "open_time" DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("pair", pair);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new OpenPosition(reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    /// <summary>
    /// The position a close request on <paramref name="pair"/> acts on: the oldest signal that
    /// has not been closed, or null. Same selection as the UPDATE in SignalService.CloseAsync.
    /// </summary>
    public static async Task<OpenPosition?> FindOldestOpenAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string pair)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT "Id", "action" FROM "Signals"
            WHERE "pair" = @pair AND "close_time" IS NULL
            ORDER BY "open_time" ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("pair", pair);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new OpenPosition(reader.GetInt32(0), reader.GetString(1))
            : null;
    }
}
