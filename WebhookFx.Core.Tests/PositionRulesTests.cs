namespace WebhookFx.Core.Tests;

/// <summary>
/// Case-by-case tests of the rules. The close-detection cases still state what the inline code
/// in WebhookFx/Program.cs did before the rules were moved here, surprising or not.
/// </summary>
public sealed class PositionRulesTests
{
    // ---- open or close? ---------------------------------------------------------------

    [Theory]
    [InlineData("Scripted Exit Long", "")]
    [InlineData("", "Scripted Exit All")]
    [InlineData("exit", "")]
    [InlineData("SL hit", "")]
    [InlineData("", "sl")]
    [InlineData("TP 1 reached", "")]
    [InlineData("", "tp")]
    public void Request_is_a_close_when_alert_message_or_comment_contains_exit_sl_or_tp(
        string alertMessage, string comment)
    {
        Assert.True(PositionRules.IsCloseRequest(Request(alertMessage: alertMessage, comment: comment)));
    }

    [Theory]
    [InlineData("Scripted Long", "Scripted Long")]
    [InlineData("Scripted Entry", "Scripted Entry")]
    [InlineData("", "")]
    [InlineData("buy", "sell")]
    public void Request_is_an_open_otherwise(string alertMessage, string comment)
    {
        Assert.False(PositionRules.IsCloseRequest(Request(alertMessage: alertMessage, comment: comment)));
    }

    /// <summary>
    /// The match is a substring match, so ordinary words that happen to contain
    /// "sl", "tp" or "exit" turn an open into a close.
    /// </summary>
    [Theory]
    [InlineData("Tesla breakout")]
    [InlineData("output above range")]
    [InlineData("flexitime entry")]
    public void Substring_match_also_fires_inside_unrelated_words(string alertMessage)
    {
        Assert.True(PositionRules.IsCloseRequest(Request(alertMessage: alertMessage, comment: "")));
    }

    [Fact]
    public void Action_field_plays_no_part_in_close_detection()
    {
        Assert.False(PositionRules.IsCloseRequest(Request(action: "exit", alertMessage: "", comment: "")));
    }

    // ---- what an open does ------------------------------------------------------------

    [Theory]
    [InlineData("buy")]
    [InlineData("Long")]
    [InlineData("")]
    public void Open_on_a_pair_with_no_position_opens(string requestedAction)
    {
        Assert.Equal(OpenDecision.Open, PositionRules.DecideOpen(null, requestedAction));
    }

    [Theory]
    [InlineData("Long", "Long")]
    [InlineData("Enter Long", "long")]
    [InlineData("LONG", "go long now")]
    [InlineData("Short", "short")]
    [InlineData("enter short", "SHORT")]
    [InlineData("buy", "buy")]
    [InlineData("BUY", "buy")]
    [InlineData("sell", "sell")]
    [InlineData("sell", "SELL")]
    public void Same_direction_is_a_no_op(string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.NoOp, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    [Theory]
    [InlineData("Long", "Short")]
    [InlineData("short", "enter long")]
    [InlineData("buy", "sell")]
    [InlineData("sell", "buy")]
    public void Opposite_direction_is_a_reversal(string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.Reversal, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    /// <summary>"buy" and "Long" are the same direction, and so are "sell" and "Short".</summary>
    [Theory]
    [InlineData("Long", "buy")]
    [InlineData("buy", "Enter Long")]
    [InlineData("Short", "sell")]
    [InlineData("sell", "short")]
    public void Buy_means_long_and_sell_means_short(string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.NoOp, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    [Theory]
    [InlineData("Long", "sell")]
    [InlineData("buy", "Short")]
    public void Mixed_vocabulary_in_opposite_directions_is_a_reversal(string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.Reversal, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    /// <summary>An action that names no direction never matches the open position, so it reverses it.</summary>
    [Theory]
    [InlineData("hold", "hold")]
    [InlineData("buy", "")]
    [InlineData("", "sell")]
    public void Action_without_a_direction_against_an_open_position_is_a_reversal(
        string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.Reversal, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    private static WebhookRequest Request(
        string action = "buy", string alertMessage = "", string comment = "") => new()
    {
        Action = action,
        Pair = "GOLD",
        EntryTag = "gold_strategy",
        AlertMessage = alertMessage,
        Comment = comment,
        Price = 4569.44m,
        AllowMultiple = true,
        Lot = 0.01m,
    };
}
