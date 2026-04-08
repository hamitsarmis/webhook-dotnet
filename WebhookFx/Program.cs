using System.Security.Cryptography;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using WebhookFx.Database;
using WebhookFx.Middleware;
using WebhookFx.Models;

var nlogConfig = new LoggingConfiguration();
var fileTarget = new FileTarget("logfile")
{
    FileName = "logs/errors.log",
    Layout = "${longdate} | ${level:uppercase=true} | ${message} ${exception:format=tostring}"
};
nlogConfig.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, fileTarget);

var accessTarget = new FileTarget("accesslog")
{
    FileName = "logs/access.log",
    Layout = "${message}",
    AutoFlush = true
};
nlogConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Info, accessTarget, "AccessLog");

LogManager.Configuration = nlogConfig;

var logger = LogManager.GetCurrentClassLogger();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var connectionString = builder.Configuration.GetConnectionString("Default")!;

await DbInitializer.InitializeAsync(connectionString);

app.UseMiddleware<IpWhitelistMiddleware>();

var accessLogger = LogManager.GetLogger("AccessLog");
app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();
    var ip = context.Connection.RemoteIpAddress;
    var method = context.Request.Method;
    var path = context.Request.Path;
    var status = context.Response.StatusCode;
    accessLogger.Info($"{DateTime.UtcNow:O} | {ip} | {method} {path} | {status} | {sw.ElapsedMilliseconds}ms");
});

app.MapPost("/webhookfx", async (HttpContext context) =>
{
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body);
    var rawBody = await reader.ReadToEndAsync();
    context.Request.Body.Position = 0;

    WebhookRequest? request;
    try
    {
        request = System.Text.Json.JsonSerializer.Deserialize<WebhookRequest>(rawBody);
    }
    catch
    {
        _ = LogRequestAsync(connectionString, rawBody, isValid: false, logger);
        return Results.BadRequest("Invalid JSON body.");
    }

    if (request is null)
    {
        _ = LogRequestAsync(connectionString, rawBody, isValid: false, logger);
        return Results.BadRequest("Empty request body.");
    }

    bool isCloseRequest = request.AlertMessage.Contains("Exit", StringComparison.OrdinalIgnoreCase)
                       || request.AlertMessage.Contains("SL", StringComparison.OrdinalIgnoreCase)
                       || request.AlertMessage.Contains("TP", StringComparison.OrdinalIgnoreCase)
                       || request.Comment.Contains("Exit", StringComparison.OrdinalIgnoreCase)
                       || request.Comment.Contains("SL", StringComparison.OrdinalIgnoreCase)
                       || request.Comment.Contains("TP", StringComparison.OrdinalIgnoreCase);

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    var openTime = DateTime.UtcNow;
    var raw = $"{request.Pair}_{request.Action}_{openTime:O}_{request.EntryTag}";
    var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];

    await using var tx = await conn.BeginTransactionAsync();

    if (!isCloseRequest)
    {
        // Check for existing open position on this pair
        await using var checkCmd = conn.CreateCommand();
        checkCmd.Transaction = tx;
        checkCmd.CommandText = """
            SELECT "Id", "action" FROM "Signals"
            WHERE "pair" = @pair AND "close_time" IS NULL
            ORDER BY "open_time" DESC
            LIMIT 1
            """;
        checkCmd.Parameters.AddWithValue("pair", request.Pair);

        int? existingId = null;
        string? existingAction = null;
        await using (var rdr = await checkCmd.ExecuteReaderAsync())
        {
            if (await rdr.ReadAsync())
            {
                existingId = rdr.GetInt32(0);
                existingAction = rdr.GetString(1);
            }
        }

        if (existingId is not null)
        {
            bool sameDirection =
                (existingAction!.Contains("Long", StringComparison.OrdinalIgnoreCase)
                 && request.Action.Contains("Long", StringComparison.OrdinalIgnoreCase))
                || (existingAction.Contains("Short", StringComparison.OrdinalIgnoreCase)
                    && request.Action.Contains("Short", StringComparison.OrdinalIgnoreCase));

            if (sameDirection)
            {
                await tx.RollbackAsync();
                _ = LogRequestAsync(connectionString, rawBody, isValid: true, logger);
                return Results.Ok("Position already open in the same direction.");
            }

            // Opposite direction: close existing position first
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
            closeCmd.Parameters.AddWithValue("id", existingId.Value);
            var closedId = (int)(await closeCmd.ExecuteScalarAsync())!;

            var closeIdempotencyKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"reversal_close_{closedId}_{openTime:O}")))[..32];

            await using var closeOutboxCmd = conn.CreateCommand();
            closeOutboxCmd.Transaction = tx;
            closeOutboxCmd.CommandText = """
                INSERT INTO "OutboxEvents" ("SignalId", "EventType", "Payload", "IdempotencyKey")
                VALUES (@signal_id, 'signal.closed', @payload::jsonb, @idempotency_key)
                """;
            closeOutboxCmd.Parameters.AddWithValue("signal_id", closedId);
            closeOutboxCmd.Parameters.AddWithValue("payload", rawBody);
            closeOutboxCmd.Parameters.AddWithValue("idempotency_key", closeIdempotencyKey);
            await closeOutboxCmd.ExecuteNonQueryAsync();
        }

        // Open the new position
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO "Signals" ("open_time", "action", "pair", "entry_tag", "alert_message", "comment", "open_price", "allow_multiple", "size", "IdempotencyKey")
            VALUES (@open_time, @action, @pair, @entry_tag, @alert_message, @comment, @open_price, @allow_multiple, @size, @idempotency_key)
            ON CONFLICT ("IdempotencyKey") DO NOTHING
            RETURNING "Id"
            """;
        cmd.Parameters.AddWithValue("open_time", openTime);
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
        {
            await using var outboxCmd = conn.CreateCommand();
            outboxCmd.Transaction = tx;
            outboxCmd.CommandText = """
                INSERT INTO "OutboxEvents" ("SignalId", "EventType", "Payload", "IdempotencyKey")
                VALUES (@signal_id, 'signal.created', @payload::jsonb, @idempotency_key)
                """;
            outboxCmd.Parameters.AddWithValue("signal_id", signalId);
            outboxCmd.Parameters.AddWithValue("payload", rawBody);
            outboxCmd.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await outboxCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        _ = LogRequestAsync(connectionString, rawBody, isValid: true, logger);
        return existingId is not null
            ? Results.Ok("Position reversed.")
            : Results.Ok("Position opened.");
    }
    else
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
            _ = LogRequestAsync(connectionString, rawBody, isValid: false, logger);
            return Results.NotFound("No open position found for this pair.");
        }

        await using var outboxCmd = conn.CreateCommand();
        outboxCmd.Transaction = tx;
        outboxCmd.CommandText = """
            INSERT INTO "OutboxEvents" ("SignalId", "EventType", "Payload", "IdempotencyKey")
            VALUES (@signal_id, 'signal.closed', @payload::jsonb, @idempotency_key)
            """;
        outboxCmd.Parameters.AddWithValue("signal_id", signalId);
        outboxCmd.Parameters.AddWithValue("payload", rawBody);
        outboxCmd.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await outboxCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        _ = LogRequestAsync(connectionString, rawBody, isValid: true, logger);
        return Results.Ok("Position closed.");
    }
});

static async Task LogRequestAsync(string connectionString, string rawJson, bool isValid, NLog.Logger log)
{
    try
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Requests" ("request_date", "json", "is_valid")
            VALUES (@request_date, @json, @is_valid)
            """;
        cmd.Parameters.AddWithValue("request_date", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("json", rawJson);
        cmd.Parameters.AddWithValue("is_valid", isValid ? (short)1 : (short)0);
        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        log.Error(ex, "LogRequestAsync failed");
    }
}

app.MapFallback(() => Results.NotFound());

app.Run("http://0.0.0.0:8089");
