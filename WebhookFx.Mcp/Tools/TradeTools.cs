using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebhookFx.Core;
using WebhookFx.Mcp.Data;
using WebhookFx.Mcp.Safety;

namespace WebhookFx.Mcp.Tools;

/// <summary>
/// The preview tool and the two tools that can change a position. A write goes through
/// POST /webhookfx, never into the database, and only after: valid arguments, the safety
/// switches, a confirmation token that matches the preview, and an audit row.
/// </summary>
[McpServerToolType]
public sealed class TradeTools
{
    public const string OpenTool = "open_position";
    public const string CloseTool = "close_position";

    private readonly IPipelineReader _reader;
    private readonly IWebhookClient _webhook;
    private readonly IAuditLog _audit;
    private readonly ConfirmationTokenService _tokens;
    private readonly McpSettings _settings;
    private readonly ILogger<TradeTools> _logger;

    public TradeTools(
        IPipelineReader reader, IWebhookClient webhook, IAuditLog audit,
        ConfirmationTokenService tokens, McpSettings settings, ILogger<TradeTools> logger)
    {
        _reader = reader;
        _webhook = webhook;
        _audit = audit;
        _tokens = tokens;
        _settings = settings;
        _logger = logger;
    }

    [McpServerTool(Name = "preview_open_position", Title = "Preview an open", ReadOnly = true, OpenWorld = false)]
    [Description(
        "Step 1 of opening a position. Changes nothing. Runs the pipeline's own position rules against the " +
        "current state and returns what open_position would do: \"open\" (nothing is open on the pair), " +
        "\"no_op\" (a position in the same direction is already open) or \"reversal\" (a position in the " +
        "other direction is open and would be closed first). Also returns a confirmation_token. Show the " +
        "preview to the user, and only if they agree call open_position with the same arguments and the " +
        "token. The token is valid for 60 seconds, once, and only while the position state stays as previewed.")]
    public async Task<PreviewResult> PreviewOpenPosition(
        [Description("Symbol exactly as the broker and the pipeline name it, for example \"GOLD\" or \"EURUSD\".")]
        string pair,
        [Description("Direction: \"buy\" for long or \"sell\" for short.")]
        string action,
        [Description("Order size in lots, for example 0.01. Must be above 0 and not above the server's maximum (get it from a rejected preview; default 0.1).")]
        decimal lot,
        [Description("Short label saying which strategy or reason this trade belongs to, for example \"manual_gold\". 1 to 100 characters.")]
        string entry_tag,
        CancellationToken cancellationToken = default)
    {
        if (!TradeArguments.TryCreate(pair, action, lot, entry_tag, _settings.MaxLot, out var arguments, out var problem))
            return new PreviewResult("rejected", problem, null, null, null, _settings.DryRun, _settings.AllowLive);

        var existing = await _reader.FindLatestOpenAsync(arguments.Pair, cancellationToken);
        var decision = PositionRules.DecideOpen(existing?.Action, arguments.Action);
        var token = _tokens.Issue(CanonicalArguments.Open(arguments, decision, existing?.Id));

        return new PreviewResult(
            Would: Name(decision),
            Explanation: $"{Describe(arguments, decision, existing)} {DescribeMode()}",
            ExistingPositionId: existing?.Id,
            ConfirmationToken: token,
            ExpiresInSeconds: (int)ConfirmationTokenService.Lifetime.TotalSeconds,
            DryRun: _settings.DryRun,
            LiveAllowed: _settings.AllowLive);
    }

    [McpServerTool(Name = OpenTool, Title = "Open a position", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Step 2 of opening a position: sends the order to the trade pipeline. Requires the confirmation_token " +
        "from preview_open_position and exactly the same pair, action, lot and entry_tag. Refuses if the token " +
        "is missing, expired, already used, issued for other arguments, or if the position on the pair changed " +
        "since the preview; in that case preview again. In dry-run mode (the default) nothing is sent and the " +
        "result says \"dry_run\": true. A real order can place a trade with real money. Every call is written " +
        "to the audit log, refusals included.")]
    public async Task<TradeResult> OpenPosition(
        [Description("Same value as in the preview.")]
        string pair,
        [Description("Same value as in the preview: \"buy\" or \"sell\".")]
        string action,
        [Description("Same value as in the preview.")]
        decimal lot,
        [Description("Same value as in the preview.")]
        string entry_tag,
        [Description("The confirmation_token returned by preview_open_position for these arguments.")]
        string confirmation_token,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var argumentsJson = JsonSerializer.Serialize(new { pair, action, lot, entry_tag });

        if (!TradeArguments.TryCreate(pair, action, lot, entry_tag, _settings.MaxLot, out var arguments, out var problem))
            return await RefuseAsync(OpenTool, argumentsJson, correlationId, problem, cancellationToken);

        if (RefusalFromSwitches() is { } switchRefusal)
            return await RefuseAsync(OpenTool, argumentsJson, correlationId, switchRefusal, cancellationToken);

        var existing = await _reader.FindLatestOpenAsync(arguments.Pair, cancellationToken);
        var decision = PositionRules.DecideOpen(existing?.Action, arguments.Action);

        var redemption = _tokens.Redeem(confirmation_token, CanonicalArguments.Open(arguments, decision, existing?.Id));
        if (redemption.Check != TokenCheck.Valid)
            return await RefuseAsync(OpenTool, argumentsJson, correlationId, Explain(redemption.Check), cancellationToken);

        var request = new WebhookRequest
        {
            Action = arguments.Action,
            Pair = arguments.Pair,
            EntryTag = arguments.EntryTag,
            AlertMessage = OpenMarker,
            Comment = OpenMarker,
            Price = 0,
            AllowMultiple = false,
            Lot = arguments.Lot,
        };

        return await SubmitAsync(
            OpenTool, argumentsJson, redemption.Nonce!, request,
            Describe(arguments, decision, existing), cancellationToken);
    }

    [McpServerTool(Name = CloseTool, Title = "Close a position", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Close the open position on a pair. Requires the close_confirmation_token that list_open_positions " +
        "returned for that pair, so call list_open_positions first, show the user which position would be " +
        "closed, and only if they agree call this tool within 60 seconds. Refuses if nothing is open on the " +
        "pair, or if the token is missing, expired, already used, or was issued for another position. In " +
        "dry-run mode (the default) nothing is sent and the result says \"dry_run\": true. A real close ends " +
        "a real trade. Every call is written to the audit log, refusals included.")]
    public async Task<TradeResult> ClosePosition(
        [Description("Symbol of the position to close, exactly as list_open_positions shows it.")]
        string pair,
        [Description("The close_confirmation_token that list_open_positions returned for this pair.")]
        string confirmation_token,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var argumentsJson = JsonSerializer.Serialize(new { pair });

        if (!TradeArguments.TryNormalisePair(pair, out var normalisedPair, out var problem))
            return await RefuseAsync(CloseTool, argumentsJson, correlationId, problem, cancellationToken);

        if (RefusalFromSwitches() is { } switchRefusal)
            return await RefuseAsync(CloseTool, argumentsJson, correlationId, switchRefusal, cancellationToken);

        var position = await _reader.FindOldestOpenAsync(normalisedPair, cancellationToken);
        if (position is null)
        {
            return await RefuseAsync(
                CloseTool, argumentsJson, correlationId, $"No position is open on {normalisedPair}.", cancellationToken);
        }

        var redemption = _tokens.Redeem(confirmation_token, CanonicalArguments.Close(normalisedPair, position.Id));
        if (redemption.Check != TokenCheck.Valid)
            return await RefuseAsync(CloseTool, argumentsJson, correlationId, Explain(redemption.Check), cancellationToken);

        var request = new WebhookRequest
        {
            Action = "close",
            Pair = normalisedPair,
            EntryTag = "mcp",
            AlertMessage = CloseMarker,
            Comment = CloseMarker,
            Price = 0,
            AllowMultiple = false,
            Lot = 0,
        };

        return await SubmitAsync(
            CloseTool, argumentsJson, redemption.Nonce!, request,
            $"Close the {position.Action} position on {normalisedPair} (signal {position.Id}).", cancellationToken);
    }

    /// <summary>
    /// alert_message and comment of an open sent by this server. The endpoint decides open or
    /// close from these two fields, so the text must not contain "Exit", "SL" or "TP".
    /// </summary>
    public const string OpenMarker = "MCP open";

    /// <summary>alert_message and comment of a close; "Exit" is what makes the endpoint close.</summary>
    public const string CloseMarker = "MCP Exit";

    // ---- the path every accepted write takes ------------------------------------------

    private async Task<TradeResult> SubmitAsync(
        string tool, string argumentsJson, string tokenNonce, WebhookRequest request, string intent,
        CancellationToken cancellationToken)
    {
        // One token, one idempotency key: the endpoint will not act twice on the same token
        // even if this process is restarted and forgets which tokens it has redeemed.
        var correlationId = tokenNonce;

        if (_settings.DryRun)
        {
            _logger.LogWarning(
                "DRY RUN {Tool} {Arguments}: {Intent} No HTTP call was made.", tool, argumentsJson, intent);

            if (await TryAuditAsync(tool, argumentsJson, "dry_run", correlationId, cancellationToken) is { } auditFailure)
                return auditFailure;

            return new TradeResult(
                "dry_run", DryRun: true,
                $"Dry run, nothing was sent. Would have done: {intent}",
                HttpStatus: null, Response: null, correlationId);
        }

        // Live from here on. No audit row, no order.
        if (await TryAuditAsync(tool, argumentsJson, "submitted", correlationId, cancellationToken) is { } notAudited)
            return notAudited;

        _logger.LogWarning("LIVE {Tool} {Arguments}: {Intent}", tool, argumentsJson, intent);

        WebhookReply reply;
        try
        {
            reply = await _webhook.SendAsync(request, idempotencyKey: tokenNonce, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // No answer is not a "no". A timeout can hit after the API has stored the order, so
            // this is recorded as unknown; the idempotency key tells later whether it arrived.
            _logger.LogError(ex, "LIVE {Tool} got no answer from the webhook.", tool);
            await TryAuditAsync(tool, argumentsJson, $"unknown: {ex.Message}", correlationId, CancellationToken.None);
            return new TradeResult(
                "unknown", DryRun: false,
                $"No answer from the pipeline: {ex.Message} The request may or may not have arrived; check list_open_positions before doing anything else.",
                HttpStatus: null, Response: null, correlationId);
        }

        var succeeded = reply.StatusCode is >= 200 and < 300;
        var status = succeeded ? "executed" : "failed";
        var outcome = $"{status}: {reply.StatusCode} {reply.Text}";
        _logger.LogWarning("LIVE {Tool} answered {Outcome}", tool, outcome);

        var auditFailureAfterSend = await TryAuditAsync(tool, argumentsJson, outcome, correlationId, CancellationToken.None);
        var detail = succeeded
            ? $"Sent to the pipeline. It answered: {reply.Text}"
            : $"The pipeline rejected the request with {reply.StatusCode}: {reply.Text}";
        if (auditFailureAfterSend is not null)
            detail += " WARNING: the result could not be written to the audit log; the \"submitted\" row is there.";

        return new TradeResult(status, DryRun: false, detail, reply.StatusCode, reply.Text, correlationId);
    }

    private async Task<TradeResult> RefuseAsync(
        string tool, string argumentsJson, string correlationId, string reason, CancellationToken cancellationToken)
    {
        _logger.LogWarning("REFUSED {Tool} {Arguments}: {Reason}", tool, argumentsJson, reason);

        if (await TryAuditAsync(tool, argumentsJson, $"refused: {reason}", correlationId, cancellationToken) is { } auditFailure)
            return auditFailure;

        return new TradeResult("refused", _settings.DryRun, reason, HttpStatus: null, Response: null, correlationId);
    }

    /// <summary>Writes an audit row. Returns null on success, otherwise the result to hand back.</summary>
    private async Task<TradeResult?> TryAuditAsync(
        string tool, string argumentsJson, string outcome, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            await _audit.RecordAsync(
                new AuditEntry(tool, argumentsJson, _settings.DryRun, outcome, correlationId), cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Audit log write failed for {Tool} ({Outcome}).", tool, outcome);
            return new TradeResult(
                "failed", _settings.DryRun,
                $"The audit log is unavailable ({ex.Message}). Nothing was sent.",
                HttpStatus: null, Response: null, correlationId);
        }
    }

    /// <summary>Null when the switches allow going on (dry run, or live with everything in place).</summary>
    private string? RefusalFromSwitches()
    {
        if (_settings.DryRun)
            return null;

        if (!_settings.AllowLive)
        {
            return $"Live trading is not allowed: {McpSettings.DryRunVariable} is false but " +
                   $"{McpSettings.AllowLiveVariable} is not true. Both must be set by the operator.";
        }

        if (_settings.InternalKey is null)
            return $"{InternalKey.ConfigurationName} is not set, so the webhook cannot be called.";

        return null;
    }

    private string DescribeMode() =>
        _settings.DryRun ? "The server is in dry-run mode: open_position will simulate and send nothing."
        : _settings.AllowLive ? "The server is LIVE: open_position will send a real order."
        : "Live trading is not allowed on this server: open_position will refuse.";

    private static string Describe(TradeArguments arguments, OpenDecision decision, OpenPosition? existing) => decision switch
    {
        OpenDecision.Open =>
            $"No position is open on {arguments.Pair}. Open a {arguments.Action} of {arguments.Lot} lot.",
        OpenDecision.NoOp =>
            $"A {existing!.Action} position is already open on {arguments.Pair} (signal {existing.Id}). Nothing changes.",
        OpenDecision.Reversal =>
            $"A {existing!.Action} position is open on {arguments.Pair} (signal {existing.Id}). " +
            $"Close it, then open a {arguments.Action} of {arguments.Lot} lot.",
        _ => throw new InvalidOperationException($"Unhandled decision {decision}."),
    };

    private static string Name(OpenDecision decision) => decision switch
    {
        OpenDecision.Open => "open",
        OpenDecision.NoOp => "no_op",
        OpenDecision.Reversal => "reversal",
        _ => throw new InvalidOperationException($"Unhandled decision {decision}."),
    };

    private static string Explain(TokenCheck check) => check switch
    {
        TokenCheck.Malformed => "confirmation_token is missing or is not a token from this server. Preview first.",
        TokenCheck.Mismatch =>
            "confirmation_token does not match: the arguments differ from the preview, or the position changed since. Preview again.",
        TokenCheck.Expired => "confirmation_token has expired (60 seconds). Preview again.",
        TokenCheck.AlreadyUsed => "confirmation_token was already used. Preview again.",
        _ => throw new InvalidOperationException($"Unhandled token check {check}."),
    };
}
