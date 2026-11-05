using System.Security.Cryptography;
using System.Text;

namespace WebhookFx.Core;

/// <summary>
/// Keys stored in Signals.IdempotencyKey and OutboxEvents.IdempotencyKey, and published as
/// the RabbitMQ message id: the first 32 hex characters of a SHA-256.
/// </summary>
public static class IdempotencyKeys
{
    /// <summary>Key of a signal and of the outbox event written for the same request.</summary>
    public static string ForSignal(string pair, string action, DateTime receivedAt, string entryTag) =>
        Hash($"{pair}_{action}_{receivedAt:O}_{entryTag}");

    /// <summary>Key of the extra "closed" event a reversal writes for the position it closes.</summary>
    public static string ForReversalClose(int closedSignalId, DateTime receivedAt) =>
        Hash($"reversal_close_{closedSignalId}_{receivedAt:O}");

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
}
