using Npgsql;

namespace WebhookFx.Core;

/// <summary>
/// Applies one webhook request to Signals and writes the matching OutboxEvents rows in the
/// same transaction. This is the only code that changes a position.
/// </summary>
public sealed class SignalService
{
    private readonly string _connectionString;

    public SignalService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <param name="request">The parsed webhook body.</param>
    /// <param name="rawBody">The body as received; stored unchanged as the outbox payload.</param>
    public async Task<SignalOutcome> HandleAsync(WebhookRequest request, string rawBody)
    {
        bool isCloseRequest = PositionRules.IsCloseRequest(request);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var receivedAt = DateTime.UtcNow;
        var idempotencyKey = IdempotencyKeys.ForSignal(request.Pair, request.Action, receivedAt, request.EntryTag);

        await using var tx = await conn.BeginTransactionAsync();

        return isCloseRequest
            ? await CloseAsync(conn, tx, request, rawBody, idempotencyKey)
            : await OpenAsync(conn, tx, request, rawBody, receivedAt, idempotencyKey);
    }

    private static async Task<SignalOutcome> OpenAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, WebhookRequest request, string rawBody,
        DateTime receivedAt, string idempotencyKey)
    {
        var existing = await PositionQueries.FindLatestOpenAsync(conn, tx, request.Pair);
        var decision = PositionRules.DecideOpen(existing?.Action, request.Action);

        if (decision == OpenDecision.NoOp)
        {
            await tx.RollbackAsync();
            return SignalOutcome.AlreadyOpen;
        }

        if (decision == OpenDecision.Reversal)
        {
            // Opposite direction: close the existing position first.
            await using var closeCmd = conn.CreateCommand();
            closeCmd.Transaction = tx;
            closeCmd.CommandText = """
                UPDATE "Signals"
                SET "close_time" = @close_time, "close_price" = @close_price
                WHERE "Id" = @id
                RETURNING "Id"
                """;
            closeCmd.Parameters.AddWithValue("close_time", DateTime.UtcNow);
            closeCmd.Parameters.AddWithValue("close_price", request.Price);
            closeCmd.Parameters.AddWithValue("id", existing!.Id);
            var closedId = (int)(await closeCmd.ExecuteScalarAsync())!;

            await InsertOutboxEventAsync(
                conn, tx, closedId, "signal.closed", rawBody,
                IdempotencyKeys.ForReversalClose(closedId, receivedAt));
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO "Signals" ("open_time", "action", "pair", "entry_tag", "alert_message", "comment", "open_price", "allow_multiple", "size", "IdempotencyKey")
            VALUES (@open_time, @action, @pair, @entry_tag, @alert_message, @comment, @open_price, @allow_multiple, @size, @idempotency_key)
            ON CONFLICT ("IdempotencyKey") DO NOTHING
            RETURNING "Id"
            """;
        cmd.Parameters.AddWithValue("open_time", receivedAt);
        cmd.Parameters.AddWithValue("action", request.Action);
        cmd.Parameters.AddWithValue("pair", request.Pair);
        cmd.Parameters.AddWithValue("entry_tag", request.EntryTag);
        cmd.Parameters.AddWithValue("alert_message", request.AlertMessage);
        cmd.Parameters.AddWithValue("comment", request.Comment);
        cmd.Parameters.AddWithValue("open_price", request.Price);
        cmd.Parameters.AddWithValue("allow_multiple", request.AllowMultiple);
        cmd.Parameters.AddWithValue("size", request.Lot);
        cmd.Parameters.AddWithValue("idempotency_key", idempotencyKey);

        var result = await cmd.ExecuteScalarAsync();
        if (result is int signalId)
            await InsertOutboxEventAsync(conn, tx, signalId, "signal.created", rawBody, idempotencyKey);

        await tx.CommitAsync();
        return decision == OpenDecision.Reversal ? SignalOutcome.Reversed : SignalOutcome.Opened;
    }

    private static async Task<SignalOutcome> CloseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, WebhookRequest request, string rawBody,
        string idempotencyKey)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE "Signals"
            SET "close_time" = @close_time, "close_price" = @close_price
            WHERE "Id" = (
                SELECT "Id" FROM "Signals"
                WHERE "pair" = @pair AND "close_time" IS NULL
                ORDER BY "open_time" ASC
                LIMIT 1
            )
            RETURNING "Id"
            """;
        cmd.Parameters.AddWithValue("close_time", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("close_price", request.Price);
        cmd.Parameters.AddWithValue("pair", request.Pair);

        var result = await cmd.ExecuteScalarAsync();
        if (result is not int signalId)
        {
            await tx.RollbackAsync();
            return SignalOutcome.NothingToClose;
        }

        await InsertOutboxEventAsync(conn, tx, signalId, "signal.closed", rawBody, idempotencyKey);

        await tx.CommitAsync();
        return SignalOutcome.Closed;
    }

    private static async Task InsertOutboxEventAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, int signalId, string eventType, string payload,
        string idempotencyKey)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO "OutboxEvents" ("SignalId", "EventType", "Payload", "IdempotencyKey")
            VALUES (@signal_id, @event_type, @payload::jsonb, @idempotency_key)
            """;
        cmd.Parameters.AddWithValue("signal_id", signalId);
        cmd.Parameters.AddWithValue("event_type", eventType);
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await cmd.ExecuteNonQueryAsync();
    }
}
