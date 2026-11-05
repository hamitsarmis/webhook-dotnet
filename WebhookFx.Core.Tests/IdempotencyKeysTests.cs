namespace WebhookFx.Core.Tests;

/// <summary>
/// The expected values were computed outside this code base:
/// <c>hashlib.sha256(raw.encode()).hexdigest().upper()[:32]</c> over the string the inline
/// code in Program.cs used to build.
/// </summary>
public sealed class IdempotencyKeysTests
{
    private static readonly DateTime ReceivedAt =
        new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);

    [Fact]
    public void Signal_key_is_sha256_of_pair_action_time_and_entry_tag()
    {
        // raw: GOLD_buy_2026-01-02T03:04:05.1234567Z_gold_strategy
        Assert.Equal(
            "C6728B6D1B5D06F5B31178F788691E73",
            IdempotencyKeys.ForSignal("GOLD", "buy", ReceivedAt, "gold_strategy"));
    }

    [Fact]
    public void Reversal_close_key_is_sha256_of_closed_signal_id_and_time()
    {
        // raw: reversal_close_42_2026-01-02T03:04:05.1234567Z
        Assert.Equal(
            "CB484A40163122049E86617F0F23CE55",
            IdempotencyKeys.ForReversalClose(42, ReceivedAt));
    }

    [Fact]
    public void Same_request_received_at_another_instant_gets_another_key()
    {
        var first = IdempotencyKeys.ForSignal("GOLD", "buy", ReceivedAt, "gold_strategy");
        var second = IdempotencyKeys.ForSignal("GOLD", "buy", ReceivedAt.AddTicks(1), "gold_strategy");

        Assert.NotEqual(first, second);
    }
}
