using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace WebhookFx.Tests;

/// <summary>
/// A request carrying the right X-Internal-Key is let through without being on the IP
/// whitelist, and what it opens is marked source = 'mcp'. Nothing else about the whitelist changes.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class InternalKeyTests : IAsyncLifetime
{
    private const string OpenBody = """
        {"action":"buy","pair":"GOLD","entry_tag":"agent","alert_message":"mcp open","comment":"mcp","price":0,"allow_multiple":false,"lot":0.01}
        """;

    private readonly PipelineFixture _fixture;

    public InternalKeyTests(PipelineFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Valid_key_from_an_ip_outside_the_whitelist_is_accepted_and_marked_as_mcp()
    {
        var response = await Post(PipelineFixture.UnknownIp, PipelineFixture.InternalKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Position opened.", await response.Content.ReadFromJsonAsync<string>());
        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.Equal("mcp", signal.Source);
    }

    [Fact]
    public async Task Request_without_a_key_from_a_whitelisted_ip_has_no_source()
    {
        var response = await Post(PipelineFixture.TradingViewIp, key: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.Null(signal.Source);
    }

    [Fact]
    public async Task Request_without_a_key_from_an_ip_outside_the_whitelist_is_still_forbidden()
    {
        var response = await Post(PipelineFixture.UnknownIp, key: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await _fixture.CountAsync("Signals"));
    }

    [Theory]
    [InlineData(PipelineFixture.UnknownIp)]
    [InlineData(PipelineFixture.TradingViewIp)]
    public async Task Wrong_key_is_forbidden_even_from_a_whitelisted_ip(string remoteIp)
    {
        var response = await Post(remoteIp, "not-the-key-not-the-key-not-the-key");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await _fixture.CountAsync("Signals"));
    }

    [Fact]
    public async Task Valid_key_opens_the_door_for_that_request_only()
    {
        var withKey = await Post(PipelineFixture.UnknownIp, PipelineFixture.InternalKey);
        var withoutKey = await Post(PipelineFixture.UnknownIp, key: null);

        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutKey.StatusCode);
    }

    private async Task<HttpResponseMessage> Post(string remoteIp, string? key)
    {
        using var client = _fixture.CreateClient(remoteIp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhookfx")
        {
            Content = new StringContent(OpenBody, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
            request.Headers.Add("X-Internal-Key", key);
        return await client.SendAsync(request);
    }
}
