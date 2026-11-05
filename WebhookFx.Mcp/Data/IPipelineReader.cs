using WebhookFx.Core;

namespace WebhookFx.Mcp.Data;

/// <summary>A row of Signals. Times are UTC.</summary>
public sealed record SignalRecord(
    int Id, string Pair, string Action, string EntryTag, decimal Size,
    DateTime OpenTime, decimal OpenPrice, DateTime? CloseTime, decimal? ClosePrice, string? Source);

/// <param name="Error">Why the database could not be read; null when it could.</param>
public sealed record PipelineHealth(bool DatabaseReachable, long? OutboxBacklog, DateTime? LastSignalAt, string? Error);

public sealed record OutboxStatus(long Pending, long Published, long Failed, DateTime? OldestPendingAt);

/// <summary>Read-only view of the pipeline's tables. The MCP server never writes to them.</summary>
public interface IPipelineReader
{
    /// <summary>Never throws: an unreachable database is a health result, not an error.</summary>
    Task<PipelineHealth> GetHealthAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SignalRecord>> ListOpenPositionsAsync(CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SignalRecord>> GetSignalHistoryAsync(
        string? pair, DateTime? sinceUtc, int limit, CancellationToken cancellationToken);

    Task<OutboxStatus> GetOutboxStatusAsync(CancellationToken cancellationToken);

    /// <summary>The position an open on the pair is judged against (see PositionQueries).</summary>
    Task<OpenPosition?> FindLatestOpenAsync(string pair, CancellationToken cancellationToken);

    /// <summary>The position a close on the pair acts on (see PositionQueries).</summary>
    Task<OpenPosition?> FindOldestOpenAsync(string pair, CancellationToken cancellationToken);
}
