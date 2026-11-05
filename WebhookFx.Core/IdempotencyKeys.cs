using System.Security.Cryptography;
using System.Text;

namespace WebhookFx.Core;

/// <summary>
/// Keys the pipeline derives itself. They are stored in Signals.IdempotencyKey and
/// OutboxEvents.IdempotencyKey and published as the RabbitMQ message id: the first 32 hex
/// characters of a SHA-256. They contain the receive time, so they identify a row, not a
/// request; recognising a repeated request needs a key from the caller (see SignalService).
/// </summary>
public static class IdempotencyKeys
{
    /// <summary>Key of a signal and of the outbox event written for the same request.</summary>
    public static string ForSignal(string pair, string action, DateTime receivedAt, string entryTag) =>
        Hash($"{pair}_{action}_{receivedAt:O}_{entryTag}");

    /// <summary>Key of the extra "closed" event a reversal writes for the position it closes.</summary>
    public static string ForReversalClose(int closedSignalId, DateTime receivedAt) =>
        Hash($"reversal_close_{closedSignalId}_{receivedAt:O}");

    /// <summary>Fingerprint of a request body, stored next to a caller-supplied key.</summary>
    public static string RequestHash(string rawBody) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
}
