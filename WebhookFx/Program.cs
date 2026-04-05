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
LogManager.Configuration = nlogConfig;

var logger = LogManager.GetCurrentClassLogger();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var connectionString = builder.Configuration.GetConnectionString("Default")!;

await DbInitializer.InitializeAsync(connectionString);

app.UseMiddleware<IpWhitelistMiddleware>();

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

    if (!isCloseRequest)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Signals" ("open_time", "action", "pair", "entry_tag", "alert_message", "comment", "open_price", "allow_multiple", "size")
            VALUES (@open_time, @action, @pair, @entry_tag, @alert_message, @comment, @open_price, @allow_multiple, @size)
            """;
        cmd.Parameters.AddWithValue("open_time", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("action", request.Action);
        cmd.Parameters.AddWithValue("pair", request.Pair);
        cmd.Parameters.AddWithValue("entry_tag", request.EntryTag);
        cmd.Parameters.AddWithValue("alert_message", request.AlertMessage);
        cmd.Parameters.AddWithValue("comment", request.Comment);
        cmd.Parameters.AddWithValue("open_price", request.Price);
        cmd.Parameters.AddWithValue("allow_multiple", request.AllowMultiple);
        cmd.Parameters.AddWithValue("size", request.Lot);
        await cmd.ExecuteNonQueryAsync();

        _ = LogRequestAsync(connectionString, rawBody, isValid: true, logger);
        return Results.Ok("Position opened.");
    }
    else
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "Signals"
            SET "close_time" = @close_time, "close_price" = @close_price
            WHERE "Id" = (
                SELECT "Id" FROM "Signals"
                WHERE "pair" = @pair AND "close_time" IS NULL
                ORDER BY "open_time" ASC
                LIMIT 1
            )
            """;
        cmd.Parameters.AddWithValue("close_time", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("close_price", request.Price);
        cmd.Parameters.AddWithValue("pair", request.Pair);

        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0)
        {
            _ = LogRequestAsync(connectionString, rawBody, isValid: false, logger);
            return Results.NotFound("No open position found for this pair.");
        }

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
