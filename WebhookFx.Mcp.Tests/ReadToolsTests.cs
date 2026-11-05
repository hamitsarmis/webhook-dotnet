using WebhookFx.Mcp.Data;
using WebhookFx.Mcp.Tools;

namespace WebhookFx.Mcp.Tests;

public sealed class ReadToolsTests
{
    [Fact]
    public async Task Health_reports_the_pipeline_and_the_safety_mode()
    {
        var h = new Harness();
        var lastSignal = new DateTime(2026, 11, 5, 11, 59, 0, DateTimeKind.Utc);
        h.Pipeline.Health = new PipelineHealth(true, 3, lastSignal, null);

        var health = await h.Read.GetHealth(CancellationToken.None);

        Assert.True(health.DatabaseReachable);
        Assert.Equal(3, health.OutboxBacklog);
        Assert.Equal(lastSignal, health.LastSignalAt);
        Assert.True(health.DryRun);
        Assert.False(health.LiveAllowed);
    }

    [Fact]
    public async Task Health_reports_an_unreachable_database_instead_of_failing()
    {
        var h = new Harness();
        h.Pipeline.Health = new PipelineHealth(false, null, null, "Connection refused");

        var health = await h.Read.GetHealth(CancellationToken.None);

        Assert.False(health.DatabaseReachable);
        Assert.Equal("Connection refused", health.Error);
    }

    [Fact]
    public async Task Open_positions_carry_a_close_token_only_for_the_one_a_close_would_act_on()
    {
        var h = new Harness();
        h.Pipeline.AddOpen(1, "GOLD", "buy", minutesAfterNoon: 0);
        h.Pipeline.AddOpen(2, "GOLD", "buy", minutesAfterNoon: 5);
        h.Pipeline.AddOpen(3, "EURUSD", "sell", minutesAfterNoon: 2);

        var open = await h.Read.ListOpenPositions(CancellationToken.None);

        Assert.Equal(3, open.Count);
        Assert.NotNull(open.Positions.Single(p => p.Id == 1).CloseConfirmationToken);
        Assert.Null(open.Positions.Single(p => p.Id == 2).CloseConfirmationToken);
        Assert.NotNull(open.Positions.Single(p => p.Id == 3).CloseConfirmationToken);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(100000, 200)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task History_limit_is_kept_between_1_and_200(int requested, int applied)
    {
        var h = new Harness();

        var history = await h.Read.GetSignalHistory(limit: requested);

        Assert.Equal(applied, history.Limit);
        Assert.Equal(applied, h.Pipeline.LastHistoryQuery!.Value.Limit);
    }

    [Fact]
    public async Task History_passes_the_pair_and_the_lower_bound_in_utc()
    {
        var h = new Harness();
        var since = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.FromHours(3));

        await h.Read.GetSignalHistory(pair: " GOLD ", since: since);

        var query = h.Pipeline.LastHistoryQuery!.Value;
        Assert.Equal("GOLD", query.Pair);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), query.SinceUtc);
        Assert.Equal(PipelineReadTools.DefaultHistoryLimit, query.Limit);
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        var h = new Harness();
        h.Pipeline.AddOpen(1, "GOLD", "buy", minutesAfterNoon: 0);
        h.Pipeline.AddOpen(2, "GOLD", "sell", minutesAfterNoon: 9);

        var history = await h.Read.GetSignalHistory();

        Assert.Equal(new[] { 2, 1 }, history.Signals.Select(s => s.Id));
    }

    [Fact]
    public async Task Outbox_status_reports_pending_published_and_failed()
    {
        var h = new Harness();
        h.Pipeline.Outbox = new OutboxStatus(2, 40, 1, new DateTime(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc));

        var status = await h.Read.GetOutboxStatus(CancellationToken.None);

        Assert.Equal((2, 40, 1), (status.Pending, status.Published, status.Failed));
    }
}
