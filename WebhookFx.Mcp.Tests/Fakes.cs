using Microsoft.Extensions.Logging.Abstractions;
using WebhookFx.Core;
using WebhookFx.Mcp.Data;
using WebhookFx.Mcp.Safety;
using WebhookFx.Mcp.Tools;

namespace WebhookFx.Mcp.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The pipeline's tables, in memory.</summary>
public sealed class FakePipeline : IPipelineReader
{
    public List<SignalRecord> Signals { get; } = new();
    public PipelineHealth Health { get; set; } = new(true, 0, null, null);
    public OutboxStatus Outbox { get; set; } = new(0, 0, 0, null);
    public (string? Pair, DateTime? SinceUtc, int Limit)? LastHistoryQuery { get; private set; }

    public SignalRecord AddOpen(int id, string pair, string action, int minutesAfterNoon = 0)
    {
        var signal = new SignalRecord(
            id, pair, action, "gold_strategy", 0.01m,
            new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutesAfterNoon),
            4000m, null, null, null);
        Signals.Add(signal);
        return signal;
    }

    private IEnumerable<SignalRecord> Open => Signals.Where(s => s.CloseTime is null);

    public Task<PipelineHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(Health);

    public Task<IReadOnlyList<SignalRecord>> ListOpenPositionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SignalRecord>>(Open.OrderBy(s => s.Pair).ThenBy(s => s.OpenTime).ToList());

    public Task<IReadOnlyList<SignalRecord>> GetSignalHistoryAsync(
        string? pair, DateTime? sinceUtc, int limit, CancellationToken cancellationToken)
    {
        LastHistoryQuery = (pair, sinceUtc, limit);
        return Task.FromResult<IReadOnlyList<SignalRecord>>(
            Signals.Where(s => pair is null || s.Pair == pair)
                .Where(s => sinceUtc is null || s.OpenTime >= sinceUtc)
                .OrderByDescending(s => s.OpenTime).Take(limit).ToList());
    }

    public Task<OutboxStatus> GetOutboxStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Outbox);

    public Task<OpenPosition?> FindLatestOpenAsync(string pair, CancellationToken cancellationToken) =>
        Task.FromResult(ToPosition(Open.Where(s => s.Pair == pair).OrderByDescending(s => s.OpenTime).FirstOrDefault()));

    public Task<OpenPosition?> FindOldestOpenAsync(string pair, CancellationToken cancellationToken) =>
        Task.FromResult(ToPosition(Open.Where(s => s.Pair == pair).OrderBy(s => s.OpenTime).FirstOrDefault()));

    private static OpenPosition? ToPosition(SignalRecord? signal) =>
        signal is null ? null : new OpenPosition(signal.Id, signal.Action);
}

/// <summary>Stands in for POST /webhookfx and remembers every call.</summary>
public sealed class RecordingWebhook : IWebhookClient
{
    public List<(WebhookRequest Request, string IdempotencyKey)> Calls { get; } = new();
    public WebhookReply Reply { get; set; } = new(200, "Position opened.", false);
    public Exception? Failure { get; set; }

    public Task<WebhookReply> SendAsync(WebhookRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        Calls.Add((request, idempotencyKey));
        return Failure is null ? Task.FromResult(Reply) : Task.FromException<WebhookReply>(Failure);
    }
}

public sealed class RecordingAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new();
    public bool Unavailable { get; set; }

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw new InvalidOperationException("audit database is down");
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

/// <summary>The tools wired to fakes. Settings default to what an unconfigured server has.</summary>
public sealed class Harness
{
    public FakePipeline Pipeline { get; } = new();
    public RecordingWebhook Webhook { get; } = new();
    public RecordingAudit Audit { get; } = new();
    public ManualClock Clock { get; } = new();
    public McpSettings Settings { get; }
    public TradeTools Trade { get; }
    public PipelineReadTools Read { get; }

    public Harness(McpSettings? settings = null)
    {
        Settings = settings ?? new McpSettings();
        var tokens = new ConfirmationTokenService(Clock);
        Trade = new TradeTools(Pipeline, Webhook, Audit, tokens, Settings, NullLogger<TradeTools>.Instance);
        Read = new PipelineReadTools(Pipeline, Settings, tokens);
    }

    /// <summary>Both switches set and a key present: the only configuration that sends orders.</summary>
    public static McpSettings Live => new()
    {
        DryRun = false,
        AllowLive = true,
        InternalKey = new string('k', 40),
    };

    public async Task<string> PreviewTokenAsync(string pair = "GOLD", string action = "buy", decimal lot = 0.01m, string entryTag = "manual")
    {
        var preview = await Trade.PreviewOpenPosition(pair, action, lot, entryTag);
        return preview.ConfirmationToken ?? throw new InvalidOperationException($"No token: {preview.Explanation}");
    }

    public async Task<string> CloseTokenAsync(string pair = "GOLD")
    {
        var open = await Read.ListOpenPositions(CancellationToken.None);
        return open.Positions.Single(p => p.Pair == pair && p.CloseConfirmationToken is not null).CloseConfirmationToken!;
    }
}
