namespace WebhookFx.Core;

/// <summary>What handling one webhook request did.</summary>
public enum SignalOutcome
{
    /// <summary>A new position was opened on a pair that had none.</summary>
    Opened,

    /// <summary>The open position was closed and a new one opened in its place.</summary>
    Reversed,

    /// <summary>A position in the same direction was already open; nothing was written.</summary>
    AlreadyOpen,

    /// <summary>The open position on the pair was closed.</summary>
    Closed,

    /// <summary>A close was requested but the pair had no open position; nothing was written.</summary>
    NothingToClose,

    /// <summary>The idempotency key was already used for a different request; nothing was written.</summary>
    IdempotencyKeyConflict,
}

/// <param name="Outcome">What the request did, or did the first time it was seen.</param>
/// <param name="Replayed">True when the answer comes from the idempotency record and nothing was done now.</param>
public sealed record SignalResult(SignalOutcome Outcome, bool Replayed = false);
