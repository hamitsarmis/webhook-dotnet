using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using WebhookFx.Core;
using WebhookFx.Middleware;

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

var signals = new SignalService(connectionString);

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

    // The header wins over the body field; without either the request is handled as before.
    var keyHeader = context.Request.Headers["Idempotency-Key"].ToString();
    var idempotencyKey = !string.IsNullOrWhiteSpace(keyHeader) ? keyHeader.Trim()
        : !string.IsNullOrWhiteSpace(request.IdempotencyKey) ? request.IdempotencyKey.Trim()
        : null;

    if (idempotencyKey is { Length: > 255 })
    {
        _ = LogRequestAsync(connectionString, rawBody, isValid: false, logger);
        return Results.BadRequest("Idempotency key is longer than 255 characters.");
    }

    var source = context.Items.ContainsKey(IpWhitelistMiddleware.InternalCallerItem) ? "mcp" : null;

    var result = await signals.HandleAsync(request, rawBody, idempotencyKey, source);
    var outcome = result.Outcome;

    if (result.Replayed)
        context.Response.Headers["Idempotent-Replayed"] = "true";

    _ = LogRequestAsync(
        connectionString, rawBody,
        isValid: outcome is not (SignalOutcome.NothingToClose or SignalOutcome.IdempotencyKeyConflict),
        logger);

    return outcome switch
    {
        SignalOutcome.Opened => Results.Ok("Position opened."),
        SignalOutcome.Reversed => Results.Ok("Position reversed."),
        SignalOutcome.AlreadyOpen => Results.Ok("Position already open in the same direction."),
        SignalOutcome.Closed => Results.Ok("Position closed."),
        SignalOutcome.NothingToClose => Results.NotFound("No open position found for this pair."),
        SignalOutcome.IdempotencyKeyConflict =>
            Results.UnprocessableEntity("Idempotency key was already used for a different request."),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome}."),
    };
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
