using System.ComponentModel;
using ModelContextProtocol.Server;
using WebhookFx.Mcp.Data;
using WebhookFx.Mcp.Safety;

namespace WebhookFx.Mcp.Tools;

/// <summary>Tools that only read. None of them can change a position.</summary>
[McpServerToolType]
public sealed class PipelineReadTools
{
    public const int DefaultHistoryLimit = 50;
    public const int MaxHistoryLimit = 200;

    private readonly IPipelineReader _reader;
    private readonly McpSettings _settings;
    private readonly ConfirmationTokenService _tokens;

    public PipelineReadTools(IPipelineReader reader, McpSettings settings, ConfirmationTokenService tokens)
    {
        _reader = reader;
        _settings = settings;
        _tokens = tokens;
    }

    [McpServerTool(Name = "get_health", Title = "Pipeline health", ReadOnly = true, OpenWorld = false)]
    [Description(
        "Check that the trade pipeline is reachable before doing anything else. Returns whether the database " +
        "answers, how many outbox events are still waiting to be published to the broker side, the time of " +
        "the most recent signal (UTC), and the server's safety mode: dry_run (writes are simulated) and " +
        "live_allowed (real orders are permitted). Read-only.")]
    public async Task<HealthResult> GetHealth(CancellationToken cancellationToken)
    {
        var health = await _reader.GetHealthAsync(cancellationToken);
        return new HealthResult(
            health.DatabaseReachable, health.OutboxBacklog, health.LastSignalAt,
            _settings.DryRun, _settings.AllowLive, health.Error);
    }

    [McpServerTool(Name = "list_open_positions", Title = "Open positions", ReadOnly = true, OpenWorld = false)]
    [Description(
        "List every position the pipeline considers open (signals without a close time), ordered by pair. " +
        "Use it to see what close_position would act on. For each pair, the position that close_position " +
        "would close carries a close_confirmation_token; pass that token to close_position within 60 seconds. " +
        "The token works once and only for that pair. Read-only.")]
    public async Task<OpenPositionsResult> ListOpenPositions(CancellationToken cancellationToken)
    {
        var open = await _reader.ListOpenPositionsAsync(cancellationToken);

        // A close acts on the oldest open signal of a pair, so only that one gets a token.
        var closable = open
            .GroupBy(p => p.Pair)
            .Select(g => g.OrderBy(p => p.OpenTime).ThenBy(p => p.Id).First().Id)
            .ToHashSet();

        var positions = open
            .Select(p => ToResult(p) with
            {
                CloseConfirmationToken = closable.Contains(p.Id)
                    ? _tokens.Issue(CanonicalArguments.Close(p.Pair, p.Id))
                    : null,
            })
            .ToList();

        return new OpenPositionsResult(
            positions.Count,
            positions,
            "close_confirmation_token is valid for 60 seconds, once, for close_position on that pair.");
    }

    [McpServerTool(Name = "get_signal_history", Title = "Signal history", ReadOnly = true, OpenWorld = false)]
    [Description(
        "Return recent signals, newest first, open and closed. Each row has the pair, action (buy/sell), size, " +
        "open and close time (UTC) and price, and source ('mcp' if this server opened it, null for TradingView). " +
        "Read-only.")]
    public async Task<SignalHistoryResult> GetSignalHistory(
        [Description("Only signals for this symbol, exactly as the pipeline stores it, for example \"GOLD\". Omit for all pairs.")]
        string? pair = null,
        [Description("Only signals opened at or after this instant, ISO 8601, for example \"2026-11-01T00:00:00Z\". Omit for no lower bound.")]
        DateTimeOffset? since = null,
        [Description("Maximum number of rows, 1 to 200. Default 50. Larger values are cut to 200.")]
        int limit = DefaultHistoryLimit,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = Math.Clamp(limit, 1, MaxHistoryLimit);
        var trimmedPair = string.IsNullOrWhiteSpace(pair) ? null : pair.Trim();

        var signals = await _reader.GetSignalHistoryAsync(
            trimmedPair, since?.UtcDateTime, effectiveLimit, cancellationToken);

        return new SignalHistoryResult(signals.Count, effectiveLimit, signals.Select(ToResult).ToList());
    }

    [McpServerTool(Name = "get_outbox_status", Title = "Outbox status", ReadOnly = true, OpenWorld = false)]
    [Description(
        "Show how the outbox between the webhook and RabbitMQ is doing: events pending publication, events " +
        "already published, events that failed, and the creation time (UTC) of the oldest pending event. " +
        "A growing pending count means signals are stored but not reaching the broker side. Read-only.")]
    public async Task<OutboxStatusResult> GetOutboxStatus(CancellationToken cancellationToken)
    {
        var status = await _reader.GetOutboxStatusAsync(cancellationToken);
        return new OutboxStatusResult(status.Pending, status.Published, status.Failed, status.OldestPendingAt);
    }

    private static SignalView ToResult(SignalRecord signal) => new(
        signal.Id, signal.Pair, signal.Action, signal.EntryTag, signal.Size,
        signal.OpenTime, signal.OpenPrice, signal.CloseTime, signal.ClosePrice, signal.Source);
}
