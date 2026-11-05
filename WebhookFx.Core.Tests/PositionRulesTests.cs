namespace WebhookFx.Core.Tests;

/// <summary>
/// Parity tests: each case states what the inline code in WebhookFx/Program.cs did before the
/// rules were moved here. Some of it is surprising; it is pinned as found, not as intended.
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
    public void Same_direction_is_a_no_op_when_both_actions_say_long_or_both_say_short(
        string existingAction, string requestedAction)
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

    /// <summary>
    /// The direction check only knows the words "Long" and "Short". The payloads this pipeline
    /// actually receives say "buy" and "sell", so a repeated "buy" is not seen as the same
    /// direction and becomes a reversal: the position is closed and reopened.
    /// </summary>
    [Theory]
    [InlineData("buy", "buy")]
    [InlineData("sell", "sell")]
    [InlineData("BUY", "buy")]
    public void Repeated_buy_or_sell_is_not_recognised_as_same_direction_and_becomes_a_reversal(
        string existingAction, string requestedAction)
    {
        Assert.Equal(OpenDecision.Reversal, PositionRules.DecideOpen(existingAction, requestedAction));
    }

    [Theory]
    [InlineData("Long", "buy")]
    [InlineData("buy", "Long")]
    [InlineData("hold", "hold")]
    public void Anything_else_against_an_open_position_is_a_reversal(string existingAction, string requestedAction)
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
