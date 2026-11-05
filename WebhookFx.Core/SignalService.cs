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
    /// <param name="idempotencyKey">
    /// Optional key from the caller. The first request with a key is handled normally and its
    /// outcome recorded in the same transaction. Any later request with that key gets the
    /// recorded outcome and changes nothing, unless its body differs, which is a conflict.
    /// </param>
    /// <param name="source">Stored on a newly opened signal; null for ordinary webhook callers.</param>
    public async Task<SignalResult> HandleAsync(
        WebhookRequest request, string rawBody, string? idempotencyKey = null, string? source = null)
    {
        bool isCloseRequest = PositionRules.IsCloseRequest(request);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var receivedAt = DateTime.UtcNow;
        var rowKey = IdempotencyKeys.ForSignal(request.Pair, request.Action, receivedAt, request.EntryTag);

        await using var tx = await conn.BeginTransactionAsync();

        if (idempotencyKey is not null)
        {
            var earlier = await ClaimKeyAsync(conn, tx, idempotencyKey, IdempotencyKeys.RequestHash(rawBody));
            if (earlier is not null)
            {
                await tx.RollbackAsync();
                return earlier;
            }
        }

        var outcome = isCloseRequest
            ? await CloseAsync(conn, tx, request, rawBody, rowKey)
            : await OpenAsync(conn, tx, request, rawBody, receivedAt, rowKey, source);

        if (idempotencyKey is not null)
        {
            await RecordOutcomeAsync(conn, tx, idempotencyKey, outcome);
            await tx.CommitAsync();
        }
        else if (outcome is SignalOutcome.AlreadyOpen or SignalOutcome.NothingToClose)
        {
            await tx.RollbackAsync();
        }
        else
        {
            await tx.CommitAsync();
        }

        return new SignalResult(outcome);
    }

    /// <summary>
    /// Takes the key for this transaction. Returns null when the key is new, otherwise the
    /// answer to give instead of handling the request. A concurrent request holding the same
    /// key makes the INSERT wait until that request commits, so two requests never both win.
    /// </summary>
    private static async Task<SignalResult?> ClaimKeyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, string requestHash)
    {
        await using var claim = conn.CreateCommand();
        claim.Transaction = tx;
        claim.CommandText = """
            INSERT INTO "IdempotencyKeys" ("Key", "RequestHash")
            VALUES (@key, @request_hash)
            ON CONFLICT ("Key") DO NOTHING
            """;
        claim.Parameters.AddWithValue("key", key);
        claim.Parameters.AddWithValue("request_hash", requestHash);
        if (await claim.ExecuteNonQueryAsync() == 1)
            return null;

        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = """SELECT "RequestHash", "Outcome" FROM "IdempotencyKeys" WHERE "Key" = @key""";
        read.Parameters.AddWithValue("key", key);
        await using var reader = await read.ExecuteReaderAsync();
        await reader.ReadAsync();

        if (reader.GetString(0) != requestHash)
            return new SignalResult(SignalOutcome.IdempotencyKeyConflict);

        return new SignalResult(Enum.Parse<SignalOutcome>(reader.GetString(1)), Replayed: true);
    }

    private static async Task RecordOutcomeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, SignalOutcome outcome)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """UPDATE "IdempotencyKeys" SET "Outcome" = @outcome WHERE "Key" = @key""";
        cmd.Parameters.AddWithValue("outcome", outcome.ToString());
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<SignalOutcome> OpenAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, WebhookRequest request, string rawBody,
        DateTime receivedAt, string rowKey, string? source)
    {
        var existing = await PositionQueries.FindLatestOpenAsync(conn, tx, request.Pair);
        var decision = PositionRules.DecideOpen(existing?.Action, request.Action);

        if (decision == OpenDecision.NoOp)
            return SignalOutcome.AlreadyOpen;

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
            INSERT INTO "Signals" ("open_time", "action", "pair", "entry_tag", "alert_message", "comment", "open_price", "allow_multiple", "size", "IdempotencyKey", "source")
            VALUES (@open_time, @action, @pair, @entry_tag, @alert_message, @comment, @open_price, @allow_multiple, @size, @idempotency_key, @source)
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
        cmd.Parameters.AddWithValue("idempotency_key", rowKey);
        cmd.Parameters.AddWithValue("source", (object?)source ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync();
        if (result is int signalId)
            await InsertOutboxEventAsync(conn, tx, signalId, "signal.created", rawBody, rowKey);

        return decision == OpenDecision.Reversal ? SignalOutcome.Reversed : SignalOutcome.Opened;
    }

    private static async Task<SignalOutcome> CloseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, WebhookRequest request, string rawBody,
        string rowKey)
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
            return SignalOutcome.NothingToClose;

        await InsertOutboxEventAsync(conn, tx, signalId, "signal.closed", rawBody, rowKey);

        return SignalOutcome.Closed;
    }

    private static async Task InsertOutboxEventAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, int signalId, string eventType, string payload,
        string rowKey)
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
        cmd.Parameters.AddWithValue("idempotency_key", rowKey);
        await cmd.ExecuteNonQueryAsync();
    }
}
