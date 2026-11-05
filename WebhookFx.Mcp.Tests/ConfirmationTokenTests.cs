using WebhookFx.Mcp.Safety;

namespace WebhookFx.Mcp.Tests;

public sealed class ConfirmationTokenTests
{
    private const string Arguments = "open_position|4:GOLD|buy|0.01|6:manual|Open|none";

    private readonly ManualClock _clock = new();
    private readonly ConfirmationTokenService _tokens;

    public ConfirmationTokenTests()
    {
        _tokens = new ConfirmationTokenService(_clock);
    }

    [Fact]
    public void Token_is_valid_for_the_arguments_it_was_issued_for()
    {
        var token = _tokens.Issue(Arguments);

        var redemption = _tokens.Redeem(token, Arguments);

        Assert.Equal(TokenCheck.Valid, redemption.Check);
        Assert.Matches("^[0-9A-F]{32}$", redemption.Nonce);
    }

    // ---- replay -------------------------------------------------------------------------

    [Fact]
    public void Token_cannot_be_redeemed_twice()
    {
        var token = _tokens.Issue(Arguments);
        _tokens.Redeem(token, Arguments);

        var second = _tokens.Redeem(token, Arguments);

        Assert.Equal(TokenCheck.AlreadyUsed, second.Check);
        Assert.Null(second.Nonce);
    }

    [Fact]
    public void Two_tokens_for_the_same_arguments_are_different_and_each_works_once()
    {
        var first = _tokens.Issue(Arguments);
        var second = _tokens.Issue(Arguments);

        Assert.NotEqual(first, second);
        Assert.Equal(TokenCheck.Valid, _tokens.Redeem(first, Arguments).Check);
        Assert.Equal(TokenCheck.Valid, _tokens.Redeem(second, Arguments).Check);
    }

    // ---- expiry -------------------------------------------------------------------------

    [Fact]
    public void Token_is_still_valid_after_60_seconds()
    {
        var token = _tokens.Issue(Arguments);
        _clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(TokenCheck.Valid, _tokens.Redeem(token, Arguments).Check);
    }

    [Fact]
    public void Token_has_expired_after_61_seconds()
    {
        var token = _tokens.Issue(Arguments);
        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(TokenCheck.Expired, _tokens.Redeem(token, Arguments).Check);
    }

    [Fact]
    public void Expiry_written_into_the_token_cannot_be_extended()
    {
        var token = _tokens.Issue(Arguments);
        var parts = token.Split('.');
        parts[1] = (long.Parse(parts[1]) + 3600).ToString();
        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(TokenCheck.Mismatch, _tokens.Redeem(string.Join('.', parts), Arguments).Check);
    }

    // ---- tamper -------------------------------------------------------------------------

    [Theory]
    [InlineData("open_position|4:GOLD|buy|0.02|6:manual|Open|none")]
    [InlineData("open_position|4:GOLD|sell|0.01|6:manual|Open|none")]
    [InlineData("open_position|6:EURUSD|buy|0.01|6:manual|Open|none")]
    [InlineData("open_position|4:GOLD|buy|0.01|6:manual|Reversal|7")]
    [InlineData("close_position|4:GOLD|7")]
    public void Token_is_refused_for_any_other_arguments(string otherArguments)
    {
        var token = _tokens.Issue(Arguments);

        Assert.Equal(TokenCheck.Mismatch, _tokens.Redeem(token, otherArguments).Check);
    }

    [Fact]
    public void Refused_attempt_with_other_arguments_does_not_burn_the_token()
    {
        var token = _tokens.Issue(Arguments);
        _tokens.Redeem(token, "close_position|4:GOLD|7");

        Assert.Equal(TokenCheck.Valid, _tokens.Redeem(token, Arguments).Check);
    }

    [Fact]
    public void Token_with_an_altered_signature_is_refused()
    {
        var token = _tokens.Issue(Arguments);
        var last = token[^1];
        var altered = token[..^1] + (last == 'A' ? 'B' : 'A');

        Assert.Equal(TokenCheck.Mismatch, _tokens.Redeem(altered, Arguments).Check);
    }

    [Fact]
    public void Token_with_a_swapped_nonce_is_refused()
    {
        var parts = _tokens.Issue(Arguments).Split('.');
        parts[2] = new string('0', 32);

        Assert.Equal(TokenCheck.Mismatch, _tokens.Redeem(string.Join('.', parts), Arguments).Check);
    }

    [Fact]
    public void Token_from_another_server_process_is_refused()
    {
        var otherProcess = new ConfirmationTokenService(_clock);
        var foreign = otherProcess.Issue(Arguments);

        Assert.Equal(TokenCheck.Mismatch, _tokens.Redeem(foreign, Arguments).Check);
    }

    // ---- not a token --------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("v1.123.ABC")]
    [InlineData("v2.1793880060.0123456789ABCDEF0123456789ABCDEF.c2ln")]
    [InlineData("v1.soon.0123456789ABCDEF0123456789ABCDEF.c2ln")]
    [InlineData("v1.1793880060.SHORT.c2ln")]
    public void Anything_that_is_not_a_token_is_malformed(string? token)
    {
        Assert.Equal(TokenCheck.Malformed, _tokens.Redeem(token, Arguments).Check);
    }
}
