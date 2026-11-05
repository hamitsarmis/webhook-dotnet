namespace WebhookFx.Mcp.Data;

/// <param name="Tool">Name of the write tool.</param>
/// <param name="ArgumentsJson">The arguments it was called with, as a JSON object. Never the confirmation token.</param>
/// <param name="DryRun">Whether the server was in dry-run mode.</param>
/// <param name="Outcome">What happened: "dry_run", "refused: ...", "submitted", "executed: ...", "failed: ...".</param>
/// <param name="CorrelationId">Shared by all rows of one tool call; equals the Idempotency-Key sent to the webhook when one was sent.</param>
public sealed record AuditEntry(string Tool, string ArgumentsJson, bool DryRun, string Outcome, string CorrelationId);

/// <summary>Append-only record of what the write tools were asked to do and what they did.</summary>
public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}
