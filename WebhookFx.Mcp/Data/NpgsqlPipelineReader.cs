using Npgsql;
using WebhookFx.Core;

namespace WebhookFx.Mcp.Data;

public sealed class NpgsqlPipelineReader : IPipelineReader
{
    private const string SignalColumns = """
        "Id", "pair", "action", "entry_tag", "size", "open_time", "open_price", "close_time", "close_price", "source"
        """;

    private readonly McpSettings _settings;

    public NpgsqlPipelineReader(McpSettings settings)
    {
        _settings = settings;
    }

    public async Task<PipelineHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM "OutboxEvents" WHERE "ProcessedAt" IS NULL AND "FailedAt" IS NULL),
                    (SELECT MAX("open_time") FROM "Signals")
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new PipelineHealth(
                DatabaseReachable: true,
                OutboxBacklog: reader.GetInt64(0),
                LastSignalAt: reader.IsDBNull(1) ? null : Utc(reader.GetDateTime(1)),
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PipelineHealth(DatabaseReachable: false, OutboxBacklog: null, LastSignalAt: null, Error: ex.Message);
        }
    }

    public async Task<IReadOnlyList<SignalRecord>> ListOpenPositionsAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SignalColumns} FROM "Signals"
            WHERE "close_time" IS NULL
            ORDER BY "pair", "open_time"
            """;
        return await ReadSignalsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<SignalRecord>> GetSignalHistoryAsync(
        string? pair, DateTime? sinceUtc, int limit, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SignalColumns} FROM "Signals"
            WHERE (@pair::text IS NULL OR "pair" = @pair)
              AND (@since::timestamp IS NULL OR "open_time" >= @since)
            ORDER BY "open_time" DESC
            LIMIT @limit
            """;
        cmd.Parameters.Add(new NpgsqlParameter("pair", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)pair ?? DBNull.Value });
        // open_time is a timestamp without time zone holding UTC; compare it as such.
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.Timestamp)
        {
            Value = sinceUtc is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Unspecified) : DBNull.Value,
        });
        cmd.Parameters.AddWithValue("limit", limit);
        return await ReadSignalsAsync(cmd, cancellationToken);
    }

    public async Task<OutboxStatus> GetOutboxStatusAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE "ProcessedAt" IS NULL AND "FailedAt" IS NULL),
                COUNT(*) FILTER (WHERE "ProcessedAt" IS NOT NULL),
                COUNT(*) FILTER (WHERE "FailedAt" IS NOT NULL),
                MIN("CreatedAt") FILTER (WHERE "ProcessedAt" IS NULL AND "FailedAt" IS NULL)
            FROM "OutboxEvents"
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new OutboxStatus(
            Pending: reader.GetInt64(0),
            Published: reader.GetInt64(1),
            Failed: reader.GetInt64(2),
            OldestPendingAt: reader.IsDBNull(3) ? null : Utc(reader.GetDateTime(3)));
    }

    public async Task<OpenPosition?> FindLatestOpenAsync(string pair, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await PositionQueries.FindLatestOpenAsync(conn, tx: null, pair);
    }

    public async Task<OpenPosition?> FindOldestOpenAsync(string pair, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await PositionQueries.FindOldestOpenAsync(conn, tx: null, pair);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
            throw new InvalidOperationException("ConnectionStrings__Default is not set.");

        var conn = new NpgsqlConnection(_settings.ConnectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<SignalRecord>> ReadSignalsAsync(
        NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var rows = new List<SignalRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SignalRecord(
                Id: reader.GetInt32(0),
                Pair: reader.GetString(1),
                Action: reader.GetString(2),
                EntryTag: reader.GetString(3),
                Size: reader.GetDecimal(4),
                OpenTime: Utc(reader.GetDateTime(5)),
                OpenPrice: reader.GetDecimal(6),
                CloseTime: reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7)),
                ClosePrice: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                Source: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    /// <summary>The pipeline stores UTC in columns without a time zone.</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
