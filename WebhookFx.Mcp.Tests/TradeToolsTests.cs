using WebhookFx.Core;
using WebhookFx.Mcp.Tools;

namespace WebhookFx.Mcp.Tests;

/// <summary>preview_open_position, open_position and close_position against an in-memory pipeline.</summary>
public sealed class TradeToolsTests
{
    // ---- preview ------------------------------------------------------------------------

    [Fact]
    public async Task Preview_on_a_pair_with_no_position_would_open()
    {
        var h = new Harness();

        var preview = await h.Trade.PreviewOpenPosition("GOLD", "buy", 0.01m, "manual");

        Assert.Equal("open", preview.Would);
        Assert.Null(preview.ExistingPositionId);
        Assert.NotNull(preview.ConfirmationToken);
        Assert.Equal(60, preview.ExpiresInSeconds);
        Assert.True(preview.DryRun);
        Assert.False(preview.LiveAllowed);
    }

    [Fact]
    public async Task Preview_in_the_direction_already_open_would_be_a_no_op()
    {
        var h = new Harness();
        h.Pipeline.AddOpen(7, "GOLD", "buy");

        var preview = await h.Trade.PreviewOpenPosition("GOLD", "buy", 0.01m, "manual");

        Assert.Equal("no_op", preview.Would);
        Assert.Equal(7, preview.ExistingPositionId);
    }

    [Fact]
    public async Task Preview_against_a_position_in_the_other_direction_would_be_a_reversal()
    {
        var h = new Harness();
        h.Pipeline.AddOpen(7, "GOLD", "sell");

        var preview = await h.Trade.PreviewOpenPosition("GOLD", "buy", 0.01m, "manual");

        Assert.Equal("reversal", preview.Would);
        Assert.Equal(7, preview.ExistingPositionId);
        Assert.Contains("Close it, then open a buy", preview.Explanation);
    }

    [Theory]
    [InlineData("GOLD", "hold", 0.01, "manual", "action must be")]
    [InlineData("GOLD", "buy", 0, "manual", "lot must be greater than 0")]
    [InlineData("GOLD", "buy", -1, "manual", "lot must be greater than 0")]
    [InlineData("GOLD", "buy", 0.11, "manual", "above the configured maximum of 0.1")]
    [InlineData("GOLD", "buy", 0.01, "", "entry_tag must be")]
    [InlineData("", "buy", 0.01, "manual", "pair must be")]
    [InlineData("GOLD; DROP TABLE", "buy", 0.01, "manual", "pair must be")]
    public async Task Preview_rejects_arguments_it_would_not_execute_and_issues_no_token(
        string pair, string action, decimal lot, string entryTag, string expectedProblem)
    {
        var h = new Harness();

        var preview = await h.Trade.PreviewOpenPosition(pair, action, lot, entryTag);

        Assert.Equal("rejected", preview.Would);
        Assert.Contains(expectedProblem, preview.Explanation);
        Assert.Null(preview.ConfirmationToken);
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        var h = new Harness(Harness.Live);

        await h.Trade.PreviewOpenPosition("GOLD", "buy", 0.01m, "manual");

        Assert.Empty(h.Webhook.Calls);
        Assert.Empty(h.Audit.Entries);
    }

    // ---- the two switches ---------------------------------------------------------------

    [Fact]
    public async Task With_default_settings_a_confirmed_open_is_a_dry_run_and_makes_no_http_call()
    {
        var h = new Harness();
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("dry_run", result.Status);
        Assert.True(result.DryRun);
        Assert.Null(result.HttpStatus);
        Assert.Empty(h.Webhook.Calls);

        var entry = Assert.Single(h.Audit.Entries);
        Assert.Equal("open_position", entry.Tool);
        Assert.True(entry.DryRun);
        Assert.Equal("dry_run", entry.Outcome);
    }

    [Fact]
    public async Task With_dry_run_off_but_live_not_allowed_the_open_is_refused_and_makes_no_http_call()
    {
        var h = new Harness(new McpSettings { DryRun = false, AllowLive = false, InternalKey = new string('k', 40) });
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("MCP_ALLOW_LIVE", result.Detail);
        Assert.Empty(h.Webhook.Calls);
        Assert.StartsWith("refused: Live trading is not allowed", Assert.Single(h.Audit.Entries).Outcome);
    }

    [Fact]
    public async Task Allow_live_alone_changes_nothing_while_dry_run_is_on()
    {
        var h = new Harness(new McpSettings { DryRun = true, AllowLive = true, InternalKey = new string('k', 40) });
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("dry_run", result.Status);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Live_without_an_internal_key_is_refused()
    {
        var h = new Harness(new McpSettings { DryRun = false, AllowLive = true, InternalKey = null });
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("WEBHOOKFX_INTERNAL_KEY", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task With_both_switches_set_a_confirmed_open_sends_exactly_one_request_through_the_webhook()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync(lot: 0.02m, entryTag: "manual_gold");

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.02m, "manual_gold", token);

        Assert.Equal("executed", result.Status);
        Assert.False(result.DryRun);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("Position opened.", result.Response);

        var (request, idempotencyKey) = Assert.Single(h.Webhook.Calls);
        Assert.Equal("buy", request.Action);
        Assert.Equal("GOLD", request.Pair);
        Assert.Equal(0.02m, request.Lot);
        Assert.Equal("manual_gold", request.EntryTag);
        Assert.False(PositionRules.IsCloseRequest(request));

        Assert.Equal(2, h.Audit.Entries.Count);
        Assert.Equal("submitted", h.Audit.Entries[0].Outcome);
        Assert.Equal("executed: 200 Position opened.", h.Audit.Entries[1].Outcome);
        Assert.All(h.Audit.Entries, e => Assert.False(e.DryRun));
        Assert.All(h.Audit.Entries, e => Assert.Equal(idempotencyKey, e.CorrelationId));
        Assert.Equal(idempotencyKey, result.CorrelationId);
    }

    // ---- the confirmation token ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("yes, go ahead")]
    public async Task Open_without_a_token_from_this_server_is_refused(string token)
    {
        var h = new Harness(Harness.Live);

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("Preview first", result.Detail);
        Assert.Empty(h.Webhook.Calls);
        Assert.StartsWith("refused:", Assert.Single(h.Audit.Entries).Outcome);
    }

    [Fact]
    public async Task Token_previewed_for_one_lot_is_refused_for_another()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync(lot: 0.01m);

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.05m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("does not match", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Token_is_refused_after_it_has_expired()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("expired", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Token_opens_one_position_however_often_it_is_presented()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync();

        var first = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);
        var second = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("executed", first.Status);
        Assert.Equal("refused", second.Status);
        Assert.Contains("already used", second.Detail);
        Assert.Single(h.Webhook.Calls);
    }

    [Fact]
    public async Task Token_is_refused_when_the_position_changed_between_preview_and_open()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync(action: "buy");
        h.Pipeline.AddOpen(9, "GOLD", "sell");

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("Preview again", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Dry_run_uses_the_token_up_as_well()
    {
        var h = new Harness();
        var token = await h.PreviewTokenAsync();

        await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);
        var second = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("refused", second.Status);
    }

    [Fact]
    public async Task Open_above_the_maximum_lot_is_refused_whatever_the_token()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync(lot: 0.1m);

        var result = await h.Trade.OpenPosition("GOLD", "buy", 5m, "manual", token);

        Assert.Equal("refused", result.Status);
        Assert.Contains("above the configured maximum", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    // ---- audit --------------------------------------------------------------------------

    [Fact]
    public async Task Order_is_not_sent_when_the_audit_log_is_unavailable()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync();
        h.Audit.Unavailable = true;

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("failed", result.Status);
        Assert.Contains("audit log is unavailable", result.Detail);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Audit_rows_never_contain_the_confirmation_token()
    {
        var h = new Harness(Harness.Live);
        var token = await h.PreviewTokenAsync();

        await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.All(h.Audit.Entries, e => Assert.DoesNotContain(token, e.ArgumentsJson + e.Outcome));
        Assert.All(h.Audit.Entries, e => Assert.Contains("\"pair\":\"GOLD\"", e.ArgumentsJson));
    }

    // ---- what the webhook answers -------------------------------------------------------

    [Fact]
    public async Task Rejection_by_the_webhook_is_reported_and_audited_as_failed()
    {
        var h = new Harness(Harness.Live);
        h.Webhook.Reply = new(403, "", false);
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("failed", result.Status);
        Assert.Equal(403, result.HttpStatus);
        Assert.StartsWith("failed: 403", h.Audit.Entries.Last().Outcome);
    }

    [Fact]
    public async Task Unreachable_webhook_is_reported_and_audited_as_failed()
    {
        var h = new Harness(Harness.Live);
        h.Webhook.Failure = new HttpRequestException("Connection refused");
        var token = await h.PreviewTokenAsync();

        var result = await h.Trade.OpenPosition("GOLD", "buy", 0.01m, "manual", token);

        Assert.Equal("failed", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.Contains("Connection refused", result.Detail);
        Assert.Equal(new[] { "submitted", "failed: Connection refused" }, h.Audit.Entries.Select(e => e.Outcome));
    }

    // ---- close --------------------------------------------------------------------------

    [Fact]
    public async Task With_default_settings_a_confirmed_close_is_a_dry_run_and_makes_no_http_call()
    {
        var h = new Harness();
        h.Pipeline.AddOpen(7, "GOLD", "buy");
        var token = await h.CloseTokenAsync();

        var result = await h.Trade.ClosePosition("GOLD", token);

        Assert.Equal("dry_run", result.Status);
        Assert.True(result.DryRun);
        Assert.Empty(h.Webhook.Calls);
        Assert.Equal(("close_position", "dry_run"), (h.Audit.Entries.Single().Tool, h.Audit.Entries.Single().Outcome));
    }

    [Fact]
    public async Task With_both_switches_set_a_confirmed_close_sends_a_close_request()
    {
        var h = new Harness(Harness.Live);
        h.Webhook.Reply = new(200, "Position closed.", false);
        h.Pipeline.AddOpen(7, "GOLD", "buy");
        var token = await h.CloseTokenAsync();

        var result = await h.Trade.ClosePosition("GOLD", token);

        Assert.Equal("executed", result.Status);
        Assert.Equal("Position closed.", result.Response);
        var (request, _) = Assert.Single(h.Webhook.Calls);
        Assert.Equal("GOLD", request.Pair);
        Assert.True(PositionRules.IsCloseRequest(request));
    }

    [Fact]
    public async Task Close_with_nothing_open_on_the_pair_is_refused()
    {
        var h = new Harness(Harness.Live);

        var result = await h.Trade.ClosePosition("GOLD", "v1.0.00000000000000000000000000000000.x");

        Assert.Equal("refused", result.Status);
        Assert.Contains("No position is open on GOLD", result.Detail);
        Assert.Empty(h.Webhook.Calls);
        Assert.Single(h.Audit.Entries);
    }

    [Fact]
    public async Task Close_token_for_one_pair_is_refused_for_another()
    {
        var h = new Harness(Harness.Live);
        h.Pipeline.AddOpen(7, "GOLD", "buy");
        h.Pipeline.AddOpen(8, "EURUSD", "sell");
        var goldToken = await h.CloseTokenAsync("GOLD");

        var result = await h.Trade.ClosePosition("EURUSD", goldToken);

        Assert.Equal("refused", result.Status);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Token_from_an_open_preview_cannot_close_a_position()
    {
        var h = new Harness(Harness.Live);
        h.Pipeline.AddOpen(7, "GOLD", "buy");
        var openToken = await h.PreviewTokenAsync(action: "buy");

        var result = await h.Trade.ClosePosition("GOLD", openToken);

        Assert.Equal("refused", result.Status);
        Assert.Empty(h.Webhook.Calls);
    }

    [Fact]
    public async Task Close_token_is_refused_once_that_position_is_gone_and_another_is_open()
    {
        var h = new Harness(Harness.Live);
        var first = h.Pipeline.AddOpen(7, "GOLD", "buy");
        var token = await h.CloseTokenAsync();
        h.Pipeline.Signals.Remove(first);
        h.Pipeline.AddOpen(8, "GOLD", "sell", minutesAfterNoon: 1);

        var result = await h.Trade.ClosePosition("GOLD", token);

        Assert.Equal("refused", result.Status);
        Assert.Empty(h.Webhook.Calls);
    }

    // ---- wording of what this server sends ----------------------------------------------

    [Fact]
    public void Open_marker_is_never_mistaken_for_a_close_and_close_marker_always_is_one()
    {
        var open = new WebhookRequest { AlertMessage = TradeTools.OpenMarker, Comment = TradeTools.OpenMarker };
        var close = new WebhookRequest { AlertMessage = TradeTools.CloseMarker, Comment = TradeTools.CloseMarker };

        Assert.False(PositionRules.IsCloseRequest(open));
        Assert.True(PositionRules.IsCloseRequest(close));
    }
}
