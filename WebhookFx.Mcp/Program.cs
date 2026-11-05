using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using WebhookFx.Mcp;
using WebhookFx.Mcp.Data;
using WebhookFx.Mcp.Safety;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol. Every log line goes to stderr.
var nlogConfig = new LoggingConfiguration();
var stderr = new ConsoleTarget("stderr")
{
    StdErr = true,
    Layout = "${longdate} | ${level:uppercase=true} | ${logger:shortName=true} | ${message} ${exception:format=tostring}",
};
nlogConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, stderr);
builder.Logging.ClearProviders();
builder.Logging.AddNLog(nlogConfig);

var settings = McpSettings.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new ConfirmationTokenService(services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IPipelineReader, NpgsqlPipelineReader>();
builder.Services.AddSingleton<IAuditLog, NpgsqlAuditLog>();
builder.Services.AddSingleton<IWebhookClient>(
    _ => new HttpWebhookClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, settings));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "webhookfx", Version = "1.0.0" };
        options.ServerInstructions =
            "Access to a TradingView-to-broker trade pipeline. The read tools are always safe. " +
            "To open a position: call preview_open_position, show the user what it would do, and only " +
            "with their agreement call open_position with the same arguments and the confirmation_token, " +
            "within 60 seconds. To close a position: call list_open_positions, show the user the position, " +
            "and only with their agreement call close_position with that pair's close_confirmation_token. " +
            "The server starts in dry-run mode, in which the write tools send nothing; get_health reports the mode.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebhookFx.Mcp").LogInformation(
    "Starting. dry_run={DryRun} live_allowed={AllowLive} max_lot={MaxLot} webhook={WebhookUrl} " +
    "internal_key_configured={HasKey} database_configured={HasDatabase}",
    settings.DryRun, settings.AllowLive, settings.MaxLot, settings.WebhookUrl,
    settings.InternalKey is not null, !string.IsNullOrWhiteSpace(settings.ConnectionString));

await host.RunAsync();
