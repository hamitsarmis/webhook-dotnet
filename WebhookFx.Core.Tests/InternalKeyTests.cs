namespace WebhookFx.Core.Tests;

public sealed class InternalKeyTests
{
    private const string Configured = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Presented_key_equal_to_the_configured_one_matches()
    {
        Assert.True(InternalKey.Matches(Configured, Configured));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdeF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef ")]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_does_not_match(string? presented)
    {
        Assert.False(InternalKey.Matches(presented, Configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_matches_when_no_key_is_configured(string? configured)
    {
        Assert.False(InternalKey.Matches("", configured));
        Assert.False(InternalKey.Matches("anything", configured));
        Assert.False(InternalKey.Matches(null, configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_configuration_switches_the_internal_path_off(string? configured)
    {
        Assert.Null(InternalKey.Validate(configured));
    }

    [Fact]
    public void Configured_key_shorter_than_32_characters_is_a_configuration_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => InternalKey.Validate("short-key"));

        Assert.Contains("at least 32 characters", error.Message);
    }

    [Fact]
    public void Long_enough_key_is_accepted_as_is()
    {
        Assert.Equal(Configured, InternalKey.Validate(Configured));
    }
}
