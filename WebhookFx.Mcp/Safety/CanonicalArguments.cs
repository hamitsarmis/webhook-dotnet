using System.Globalization;
using WebhookFx.Core;

namespace WebhookFx.Mcp.Safety;

/// <summary>
/// The exact string a confirmation token signs. It contains the arguments of the write and
/// the state the preview saw, so a token is worthless for other arguments and after the
/// position has changed.
/// </summary>
public static class CanonicalArguments
{
    public static string Open(TradeArguments arguments, OpenDecision decision, int? existingSignalId) =>
        string.Join('|',
            "open_position",
            Field(arguments.Pair),
            arguments.Action,
            arguments.Lot.ToString("G29", CultureInfo.InvariantCulture),
            Field(arguments.EntryTag),
            decision,
            existingSignalId?.ToString(CultureInfo.InvariantCulture) ?? "none");

    public static string Close(string pair, int signalId) =>
        string.Join('|', "close_position", Field(pair), signalId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Length-prefixed, so a value containing the separator cannot imitate another field.</summary>
    private static string Field(string value) => $"{value.Length}:{value}";
}
