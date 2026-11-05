using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WebhookFx.Tests;

/// <summary>
/// A caller may send an idempotency key, as the Idempotency-Key header or as the body field
/// "idempotency_key". A repeat with the same key gets the first answer again and writes nothing.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class IdempotencyKeyTests : IAsyncLifetime
{
    private const string ReplayHeader = "Idempotent-Replayed";

    private readonly PipelineFixture _fixture;
    private readonly HttpClient _client;

    public IdempotencyKeyTests(PipelineFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Repeat_with_the_same_key_gets_the_first_answer_and_writes_nothing()
    {
        var first = await Post(Open("buy"), key: "alert-1");
        var second = await Post(Open("buy"), key: "alert-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Position opened.", await Text(first));
        Assert.False(first.Headers.Contains(ReplayHeader));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Position opened.", await Text(second));
        Assert.Equal("true", second.Headers.GetValues(ReplayHeader).Single());

        Assert.Single(await _fixture.SignalsAsync());
        Assert.Single(await _fixture.OutboxAsync());
    }

    [Fact]
    public async Task Key_in_the_body_works_like_the_header()
    {
        var body = Open("buy", idempotencyKey: "alert-1");

        await Post(body);
        var second = await Post(body);

        Assert.Equal("Position opened.", await Text(second));
        Assert.True(second.Headers.Contains(ReplayHeader));
        Assert.Single(await _fixture.SignalsAsync());
    }

    [Fact]
    public async Task Header_wins_over_the_body_field()
    {
        var body = Open("buy", idempotencyKey: "from-body");

        await Post(body, key: "from-header");
        var sameHeader = await Post(body, key: "from-header");
        var otherHeader = await Post(body, key: "another-header");

        Assert.True(sameHeader.Headers.Contains(ReplayHeader));
        Assert.False(otherHeader.Headers.Contains(ReplayHeader));
    }

    [Fact]
    public async Task Replay_answers_from_the_record_even_when_the_position_has_changed_since()
    {
        await Post(Open("buy"), key: "open-1");
        await Post(Close(), key: "close-1");

        var replayedOpen = await Post(Open("buy"), key: "open-1");
        var replayedClose = await Post(Close(), key: "close-1");

        Assert.Equal("Position opened.", await Text(replayedOpen));
        Assert.Equal("Position closed.", await Text(replayedClose));
        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.NotNull(signal.CloseTime);
        Assert.Equal(2, (await _fixture.OutboxAsync()).Count);
    }

    [Fact]
    public async Task Answers_that_wrote_nothing_are_replayed_too()
    {
        var nothingToClose = await Post(Close(), key: "close-1");
        await Post(Open("buy"), key: "open-1");
        var alreadyOpen = await Post(Open("buy"), key: "open-2");

        var replayedClose = await Post(Close(), key: "close-1");
        var replayedNoOp = await Post(Open("buy"), key: "open-2");

        Assert.Equal(HttpStatusCode.NotFound, nothingToClose.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replayedClose.StatusCode);
        Assert.Equal("No open position found for this pair.", await Text(replayedClose));
        Assert.Equal("Position already open in the same direction.", await Text(alreadyOpen));
        Assert.Equal("Position already open in the same direction.", await Text(replayedNoOp));
        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.Null(signal.CloseTime);
    }

    [Fact]
    public async Task Reversal_is_replayed_without_reversing_again()
    {
        await Post(Open("buy"), key: "open-1");
        await Post(Open("sell"), key: "reverse-1");

        var replay = await Post(Open("sell"), key: "reverse-1");

        Assert.Equal("Position reversed.", await Text(replay));
        Assert.Equal(2, (await _fixture.SignalsAsync()).Count);
        Assert.Equal(3, (await _fixture.OutboxAsync()).Count);
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_rejected_and_writes_nothing()
    {
        await Post(Open("buy", price: 100m), key: "alert-1");

        var response = await Post(Open("buy", price: 101m), key: "alert-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Idempotency key was already used for a different request.", await Text(response));
        Assert.Single(await _fixture.SignalsAsync());
        Assert.Single(await _fixture.OutboxAsync());
    }

    [Fact]
    public async Task Requests_arriving_at_the_same_time_with_one_key_open_one_position()
    {
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Post(Open("buy"), key: "alert-1")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(7, responses.Count(r => r.Headers.Contains(ReplayHeader)));
        Assert.Single(await _fixture.SignalsAsync());
        Assert.Single(await _fixture.OutboxAsync());
    }

    [Fact]
    public async Task Different_keys_are_separate_requests()
    {
        await Post(Open("buy"), key: "alert-1");

        var second = await Post(Open("sell"), key: "alert-2");

        Assert.Equal("Position reversed.", await Text(second));
        Assert.False(second.Headers.Contains(ReplayHeader));
    }

    [Fact]
    public async Task Requests_without_a_key_are_never_replayed()
    {
        await Post(Open("buy"));
        await Post(Close());

        var again = await Post(Open("buy"));

        Assert.Equal("Position opened.", await Text(again));
        Assert.False(again.Headers.Contains(ReplayHeader));
        Assert.Equal(2, (await _fixture.SignalsAsync()).Count);
    }

    [Fact]
    public async Task Key_longer_than_255_characters_is_a_bad_request()
    {
        var response = await Post(Open("buy"), key: new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Idempotency key is longer than 255 characters.", await Text(response));
        Assert.Equal(0, await _fixture.CountAsync("Signals"));
    }

    // ---- helpers ------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Post(Dictionary<string, object> body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhookfx")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private static Dictionary<string, object> Open(
        string action, decimal price = 4569.44m, string? idempotencyKey = null) =>
        Signal(action, "Scripted Entry", "Scripted Entry", price, idempotencyKey);

    private static Dictionary<string, object> Close() =>
        Signal("sell", "Scripted Exit Long", "Scripted Exit All", 4569.44m, null);

    private static Dictionary<string, object> Signal(
        string action, string alertMessage, string comment, decimal price, string? idempotencyKey)
    {
        var body = new Dictionary<string, object>
        {
            ["action"] = action,
            ["pair"] = "GOLD",
            ["entry_tag"] = "gold_strategy",
            ["alert_message"] = alertMessage,
            ["comment"] = comment,
            ["price"] = price,
            ["allow_multiple"] = true,
            ["lot"] = 0.01m,
        };
        if (idempotencyKey is not null)
            body["idempotency_key"] = idempotencyKey;
        return body;
    }

    private static async Task<string?> Text(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<string>();
}
