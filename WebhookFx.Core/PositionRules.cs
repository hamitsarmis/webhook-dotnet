namespace WebhookFx.Core;

/// <summary>What an open request does to a pair, given the position that is already open on it.</summary>
public enum OpenDecision
{
    /// <summary>Nothing is open on the pair: open a new position.</summary>
    Open,

    /// <summary>A position in the same direction is already open: do nothing.</summary>
    NoOp,

    /// <summary>A position in another direction is open: close it, then open the new one.</summary>
    Reversal,
}

/// <summary>
/// The decisions the pipeline takes about a signal, with no I/O. Shared by the webhook
/// endpoint and by anything that wants to predict what the endpoint will do.
/// </summary>
public static class PositionRules
{
    /// <summary>
    /// A request closes a position when its alert message or its comment contains
    /// "Exit", "SL" or "TP" anywhere, in any casing. Everything else opens one.
    /// </summary>
    public static bool IsCloseRequest(WebhookRequest request) =>
        MentionsClose(request.AlertMessage) || MentionsClose(request.Comment);

    /// <param name="existingOpenAction">Action of the open signal on the pair, or null when there is none.</param>
    /// <param name="requestedAction">Action of the incoming open request.</param>
    public static OpenDecision DecideOpen(string? existingOpenAction, string requestedAction)
    {
        if (existingOpenAction is null)
            return OpenDecision.Open;

        return IsSameDirection(existingOpenAction, requestedAction)
            ? OpenDecision.NoOp
            : OpenDecision.Reversal;
    }

    /// <summary>
    /// Two actions point the same way when both are long ("Long" or "buy") or both are short
    /// ("Short" or "sell"). An action that names neither never matches.
    /// </summary>
    public static bool IsSameDirection(string existingAction, string requestedAction) =>
        (IsLong(existingAction) && IsLong(requestedAction))
        || (IsShort(existingAction) && IsShort(requestedAction));

    private static bool IsLong(string action) => Mentions(action, "Long") || Mentions(action, "buy");

    private static bool IsShort(string action) => Mentions(action, "Short") || Mentions(action, "sell");

    private static bool MentionsClose(string text) =>
        Mentions(text, "Exit") || Mentions(text, "SL") || Mentions(text, "TP");

    private static bool Mentions(string text, string word) =>
        text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
