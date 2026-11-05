using System.Text.Json.Serialization;

namespace WebhookFx.Mcp.Tools;

public sealed record HealthResult(
    [property: JsonPropertyName("database_reachable")] bool DatabaseReachable,
    [property: JsonPropertyName("outbox_backlog")] long? OutboxBacklog,
    [property: JsonPropertyName("last_signal_at")] DateTime? LastSignalAt,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("live_allowed")] bool LiveAllowed,
    [property: JsonPropertyName("error")] string? Error);

public sealed record SignalView(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("pair")] string Pair,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("entry_tag")] string EntryTag,
    [property: JsonPropertyName("size")] decimal Size,
    [property: JsonPropertyName("open_time")] DateTime OpenTime,
    [property: JsonPropertyName("open_price")] decimal OpenPrice,
    [property: JsonPropertyName("close_time")] DateTime? CloseTime,
    [property: JsonPropertyName("close_price")] decimal? ClosePrice,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("close_confirmation_token")] string? CloseConfirmationToken = null);

public sealed record OpenPositionsResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("positions")] IReadOnlyList<SignalView> Positions,
    [property: JsonPropertyName("note")] string Note);

public sealed record SignalHistoryResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("signals")] IReadOnlyList<SignalView> Signals);

public sealed record OutboxStatusResult(
    [property: JsonPropertyName("pending")] long Pending,
    [property: JsonPropertyName("published")] long Published,
    [property: JsonPropertyName("failed")] long Failed,
    [property: JsonPropertyName("oldest_pending_at")] DateTime? OldestPendingAt);

/// <param name="Would">"open", "no_op", "reversal", or "rejected" when the arguments are not acceptable.</param>
public sealed record PreviewResult(
    [property: JsonPropertyName("would")] string Would,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("existing_position_id")] int? ExistingPositionId,
    [property: JsonPropertyName("confirmation_token")] string? ConfirmationToken,
    [property: JsonPropertyName("expires_in_seconds")] int? ExpiresInSeconds,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("live_allowed")] bool LiveAllowed);

/// <param name="Status">"dry_run", "executed", "refused" or "failed".</param>
/// <param name="DryRun">True when the server was in dry-run mode, in which case no request was sent.</param>
/// <param name="Detail">What happened, or why nothing did.</param>
/// <param name="HttpStatus">Status of POST /webhookfx; null when no request was sent.</param>
/// <param name="Response">The endpoint's answer; null when no request was sent.</param>
/// <param name="CorrelationId">Value of AgentActions.correlation_id for this call.</param>
public sealed record TradeResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("http_status")] int? HttpStatus,
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("correlation_id")] string CorrelationId);
