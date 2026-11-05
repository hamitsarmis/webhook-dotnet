using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WebhookFx.Mcp.Tests;

/// <summary>
/// Starts the real server as a child process and talks to it over stdio with the SDK's
/// client, the way Claude Code does. No database is running: get_health has to say so.
/// </summary>
public sealed class StdioServerTests : IAsyncLifetime
{
    private static readonly string[] ExpectedTools =
    [
        "close_position",
        "get_health",
        "get_outbox_status",
        "get_signal_history",
        "list_open_positions",
        "open_position",
        "preview_open_position",
    ];

    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "webhookfx",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "WebhookFx.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                // Nothing listens on port 1; the short timeout keeps get_health quick.
                ["ConnectionStrings__Default"] = "Host=127.0.0.1;Port=1;Database=webhookfx;Username=nobody;Timeout=2",
                ["MCP_DRY_RUN"] = null,
                ["MCP_ALLOW_LIVE"] = null,
                ["WEBHOOKFX_INTERNAL_KEY"] = null,
            },
        });

        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync() => await _client.DisposeAsync();

    [Fact]
    public async Task Server_offers_exactly_the_seven_tools()
    {
        var tools = await _client.ListToolsAsync();

        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Every_tool_and_every_parameter_is_described()
    {
        var tools = await _client.ListToolsAsync();

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} has no description");

            if (!tool.JsonSchema.TryGetProperty("properties", out var properties))
                continue;

            foreach (var parameter in properties.EnumerateObject())
            {
                Assert.True(
                    parameter.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString()),
                    $"{tool.Name}.{parameter.Name} has no description");
            }
        }
    }

    [Fact]
    public async Task Write_tools_take_the_documented_arguments()
    {
        var tools = await _client.ListToolsAsync();

        Assert.Equal(
            ["action", "confirmation_token", "entry_tag", "lot", "pair"],
            RequiredArguments(tools.Single(t => t.Name == "open_position")));
        Assert.Equal(
            ["confirmation_token", "pair"],
            RequiredArguments(tools.Single(t => t.Name == "close_position")));
        Assert.Equal(
            ["action", "entry_tag", "lot", "pair"],
            RequiredArguments(tools.Single(t => t.Name == "preview_open_position")));
    }

    [Fact]
    public async Task Get_health_answers_and_reports_dry_run_by_default()
    {
        var result = await _client.CallToolAsync("get_health");

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var health = JsonDocument.Parse(text).RootElement;
        Assert.False(health.GetProperty("database_reachable").GetBoolean());
        Assert.True(health.GetProperty("dry_run").GetBoolean());
        Assert.False(health.GetProperty("live_allowed").GetBoolean());
        Assert.False(string.IsNullOrEmpty(health.GetProperty("error").GetString()));
    }

    private static string[] RequiredArguments(McpClientTool tool) =>
        tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();
}
