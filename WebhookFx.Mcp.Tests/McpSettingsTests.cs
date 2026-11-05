using Microsoft.Extensions.Configuration;

namespace WebhookFx.Mcp.Tests;

public sealed class McpSettingsTests
{
    [Fact]
    public void Unconfigured_server_is_in_dry_run_with_live_trading_not_allowed()
    {
        var settings = Load();

        Assert.True(settings.DryRun);
        Assert.False(settings.AllowLive);
        Assert.Equal(0.1m, settings.MaxLot);
        Assert.Null(settings.InternalKey);
        Assert.Equal(new Uri("http://localhost:8089"), settings.WebhookUrl);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void Only_the_word_false_turns_dry_run_off(string value)
    {
        Assert.False(Load(("MCP_DRY_RUN", value)).DryRun);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("flase")]
    [InlineData("")]
    [InlineData("true")]
    public void Any_other_value_leaves_dry_run_on(string value)
    {
        Assert.True(Load(("MCP_DRY_RUN", value)).DryRun);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("ture", false)]
    [InlineData("", false)]
    public void Only_the_word_true_allows_live_trading(string value, bool expected)
    {
        Assert.Equal(expected, Load(("MCP_ALLOW_LIVE", value)).AllowLive);
    }

    [Fact]
    public void Maximum_lot_and_webhook_url_can_be_set()
    {
        var settings = Load(("MCP_MAX_LOT", "0.5"), ("WEBHOOKFX_URL", "http://webhookfx:8089"));

        Assert.Equal(0.5m, settings.MaxLot);
        Assert.Equal(new Uri("http://webhookfx:8089"), settings.WebhookUrl);
    }

    [Theory]
    [InlineData("MCP_MAX_LOT", "a lot")]
    [InlineData("MCP_MAX_LOT", "0")]
    [InlineData("MCP_MAX_LOT", "-1")]
    [InlineData("WEBHOOKFX_URL", "localhost:8089")]
    [InlineData("WEBHOOKFX_INTERNAL_KEY", "too-short")]
    public void Values_that_make_no_sense_stop_the_server_at_startup(string name, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Load((name, value)));
    }

    private static McpSettings Load(params (string Name, string Value)[] values) =>
        McpSettings.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(v => v.Name, v => (string?)v.Value))
                .Build());
}
