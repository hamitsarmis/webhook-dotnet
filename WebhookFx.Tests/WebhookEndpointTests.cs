using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WebhookFx.Tests;

/// <summary>
/// What POST /webhookfx does, observed from outside: HTTP status, response text and the rows
/// it leaves in Signals and OutboxEvents.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private readonly PipelineFixture _fixture;
    private readonly HttpClient _tradingView;

    public WebhookEndpointTests(PipelineFixture fixture)
    {
        _fixture = fixture;
        _tradingView = fixture.CreateClient();
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        _tradingView.Dispose();
        return Task.CompletedTask;
    }

    // ---- who may call -------------------------------------------------------------------

    [Fact]
    public async Task Request_from_an_ip_outside_the_whitelist_is_forbidden_and_writes_nothing()
    {
        using var stranger = _fixture.CreateClient(PipelineFixture.UnknownIp);

        var response = await stranger.PostAsync("/webhookfx", Json(Open("buy")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await _fixture.CountAsync("Signals"));
        Assert.Equal(0, await _fixture.CountAsync("OutboxEvents"));
    }

    [Fact]
    public async Task Unknown_path_is_not_found()
    {
        var response = await _tradingView.GetAsync("/anything-else");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- malformed input ----------------------------------------------------------------

    [Fact]
    public async Task Body_that_is_not_json_is_a_bad_request()
    {
        var response = await _tradingView.PostAsync("/webhookfx", Raw("this is not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid JSON body.", await Text(response));
        Assert.Equal(0, await _fixture.CountAsync("Signals"));
    }

    [Fact]
    public async Task Json_null_body_is_a_bad_request()
    {
        var response = await _tradingView.PostAsync("/webhookfx", Raw("null"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Empty request body.", await Text(response));
    }

    // ---- open ---------------------------------------------------------------------------

    [Fact]
    public async Task Open_on_a_pair_with_no_position_stores_the_signal_and_one_created_event()
    {
        var response = await _tradingView.PostAsync("/webhookfx", Json(Open("buy", price: 4569.44m, lot: 0.01m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Position opened.", await Text(response));

        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.Equal("buy", signal.Action);
        Assert.Equal("GOLD", signal.Pair);
        Assert.Equal("gold_strategy", signal.EntryTag);
        Assert.Equal(4569.44m, signal.OpenPrice);
        Assert.Equal(0.01m, signal.Size);
        Assert.Null(signal.CloseTime);
        Assert.Matches("^[0-9A-F]{32}$", signal.IdempotencyKey);

        var created = Assert.Single(await _fixture.OutboxAsync());
        Assert.Equal("signal.created", created.EventType);
        Assert.Equal(signal.Id, created.SignalId);
        Assert.Equal(signal.IdempotencyKey, created.IdempotencyKey);
        Assert.Equal("buy", Field(created.Payload, "action"));
        Assert.Equal("GOLD", Field(created.Payload, "pair"));
    }

    [Fact]
    public async Task Positions_on_different_pairs_do_not_affect_each_other()
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open("buy", pair: "GOLD")));

        var response = await _tradingView.PostAsync("/webhookfx", Json(Open("sell", pair: "EURUSD")));

        Assert.Equal("Position opened.", await Text(response));
        var signals = await _fixture.SignalsAsync();
        Assert.Equal(2, signals.Count);
        Assert.All(signals, s => Assert.Null(s.CloseTime));
    }

    // ---- same direction -----------------------------------------------------------------

    [Theory]
    [InlineData("buy", "buy")]
    [InlineData("sell", "sell")]
    [InlineData("Enter Long", "long")]
    [InlineData("Short", "enter short")]
    [InlineData("Long", "buy")]
    public async Task Second_open_in_the_same_direction_is_a_no_op(string firstAction, string secondAction)
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open(firstAction)));

        var response = await _tradingView.PostAsync("/webhookfx", Json(Open(secondAction)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Position already open in the same direction.", await Text(response));
        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.Null(signal.CloseTime);
        Assert.Single(await _fixture.OutboxAsync());
    }

    // ---- reversal -----------------------------------------------------------------------

    [Theory]
    [InlineData("buy", "sell")]
    [InlineData("Long", "Short")]
    public async Task Open_in_the_opposite_direction_closes_the_position_and_opens_a_new_one(
        string firstAction, string secondAction)
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open(firstAction, price: 100m)));

        var response = await _tradingView.PostAsync("/webhookfx", Json(Open(secondAction, price: 105.5m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Position reversed.", await Text(response));

        var signals = await _fixture.SignalsAsync();
        Assert.Equal(2, signals.Count);
        Assert.NotNull(signals[0].CloseTime);
        Assert.Equal(105.5m, signals[0].ClosePrice);
        Assert.Equal(secondAction, signals[1].Action);
        Assert.Null(signals[1].CloseTime);

        var outbox = await _fixture.OutboxAsync();
        Assert.Equal(3, outbox.Count);
        Assert.Equal(("signal.created", signals[0].Id), (outbox[0].EventType, outbox[0].SignalId));
        Assert.Equal(("signal.closed", signals[0].Id), (outbox[1].EventType, outbox[1].SignalId));
        Assert.Equal(("signal.created", signals[1].Id), (outbox[2].EventType, outbox[2].SignalId));
        Assert.Equal(3, outbox.Select(e => e.IdempotencyKey).Distinct().Count());
        Assert.Equal(signals[1].IdempotencyKey, outbox[2].IdempotencyKey);
    }

    // ---- close --------------------------------------------------------------------------

    [Fact]
    public async Task Close_request_closes_the_open_position_and_emits_a_closed_event()
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open("buy", price: 100m)));

        var response = await _tradingView.PostAsync("/webhookfx", Json(Close(price: 110m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Position closed.", await Text(response));

        var signal = Assert.Single(await _fixture.SignalsAsync());
        Assert.NotNull(signal.CloseTime);
        Assert.Equal(110m, signal.ClosePrice);

        var outbox = await _fixture.OutboxAsync();
        Assert.Equal(2, outbox.Count);
        Assert.Equal(("signal.closed", signal.Id), (outbox[1].EventType, outbox[1].SignalId));
        Assert.Equal("Scripted Exit All", Field(outbox[1].Payload, "comment"));
    }

    [Fact]
    public async Task Close_request_with_no_open_position_is_not_found_and_writes_nothing()
    {
        var response = await _tradingView.PostAsync("/webhookfx", Json(Close()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No open position found for this pair.", await Text(response));
        Assert.Equal(0, await _fixture.CountAsync("OutboxEvents"));
    }

    [Fact]
    public async Task Close_request_leaves_positions_on_other_pairs_open()
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open("buy", pair: "GOLD")));
        await _tradingView.PostAsync("/webhookfx", Json(Open("buy", pair: "EURUSD")));

        await _tradingView.PostAsync("/webhookfx", Json(Close(pair: "GOLD")));

        var signals = await _fixture.SignalsAsync();
        Assert.NotNull(signals.Single(s => s.Pair == "GOLD").CloseTime);
        Assert.Null(signals.Single(s => s.Pair == "EURUSD").CloseTime);
    }

    [Theory]
    [InlineData("Scripted Exit Long", "")]
    [InlineData("", "exit")]
    [InlineData("SL hit", "")]
    [InlineData("", "tp 1 reached")]
    public async Task Request_is_a_close_when_alert_message_or_comment_mentions_exit_sl_or_tp(
        string alertMessage, string comment)
    {
        await _tradingView.PostAsync("/webhookfx", Json(Open("buy")));

        var response = await _tradingView.PostAsync(
            "/webhookfx", Json(Signal("sell", "GOLD", alertMessage, comment, 1m, 0.01m)));

        Assert.Equal("Position closed.", await Text(response));
    }

    // ---- helpers ------------------------------------------------------------------------

    private static object Open(string action, string pair = "GOLD", decimal price = 4569.44m, decimal lot = 0.01m) =>
        Signal(action, pair, "Scripted Entry", "Scripted Entry", price, lot);

    private static object Close(string pair = "GOLD", decimal price = 4569.44m) =>
        Signal("sell", pair, "Scripted Exit Long", "Scripted Exit All", price, 0.01m);

    private static object Signal(
        string action, string pair, string alertMessage, string comment, decimal price, decimal lot) => new
    {
        action,
        pair,
        entry_tag = "gold_strategy",
        alert_message = alertMessage,
        comment,
        price,
        allow_multiple = true,
        lot,
    };

    private static StringContent Json(object body) => Raw(JsonSerializer.Serialize(body));

    private static StringContent Raw(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<string?> Text(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<string>();

    private static string? Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetString();
}
