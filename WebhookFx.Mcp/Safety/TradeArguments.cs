using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace WebhookFx.Mcp.Safety;

/// <summary>Arguments of an open, after validation and normalisation.</summary>
public sealed partial record TradeArguments(string Pair, string Action, decimal Lot, string EntryTag)
{
    public const int MaxEntryTagLength = 100;

    /// <summary>
    /// Returns the normalised arguments, or the reason they are not acceptable.
    /// The action is limited to "buy" and "sell": those are the only values the broker side understands.
    /// </summary>
    public static bool TryCreate(
        string? pair, string? action, decimal lot, string? entryTag, decimal maxLot,
        [NotNullWhen(true)] out TradeArguments? arguments, [NotNullWhen(false)] out string? problem)
    {
        arguments = null;

        if (!TryNormalisePair(pair, out var normalisedPair, out problem))
            return false;

        var normalisedAction = action?.Trim().ToLowerInvariant();
        if (normalisedAction is not ("buy" or "sell"))
        {
            problem = "action must be buy or sell.";
            return false;
        }

        if (lot <= 0)
        {
            problem = "lot must be greater than 0.";
            return false;
        }

        if (lot > maxLot)
        {
            problem = $"lot {lot} is above the configured maximum of {maxLot} ({McpSettings.MaxLotVariable}).";
            return false;
        }

        var normalisedTag = entryTag?.Trim();
        if (string.IsNullOrEmpty(normalisedTag) || normalisedTag.Length > MaxEntryTagLength)
        {
            problem = $"entry_tag must be 1 to {MaxEntryTagLength} characters.";
            return false;
        }

        arguments = new TradeArguments(normalisedPair, normalisedAction, lot, normalisedTag);
        return true;
    }

    public static bool TryNormalisePair(
        string? pair, [NotNullWhen(true)] out string? normalised, [NotNullWhen(false)] out string? problem)
    {
        normalised = pair?.Trim();
        if (normalised is null || !PairPattern().IsMatch(normalised))
        {
            normalised = null;
            problem = "pair must be 1 to 50 characters: letters, digits and . _ # / : -";
            return false;
        }

        problem = null;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._#/:\-]{1,50}$")]
    private static partial Regex PairPattern();
}
