using System.Globalization;
using Microsoft.Extensions.Configuration;
using WebhookFx.Core;

namespace WebhookFx.Mcp;

/// <summary>
/// Everything the server reads from its environment. The two trading switches fail safe:
/// only the word "false" turns dry-run off and only the word "true" allows live (casing and
/// surrounding spaces do not matter). Any other value, a typo included, keeps the safe setting.
/// </summary>
public sealed record McpSettings
{
    public const string DryRunVariable = "MCP_DRY_RUN";
    public const string AllowLiveVariable = "MCP_ALLOW_LIVE";
    public const string MaxLotVariable = "MCP_MAX_LOT";
    public const string WebhookUrlVariable = "WEBHOOKFX_URL";

    /// <summary>When true, write tools validate, log and audit, but never call the webhook.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Second switch. With dry-run off and this false, write tools refuse.</summary>
    public bool AllowLive { get; init; }

    /// <summary>Largest lot a write tool accepts.</summary>
    public decimal MaxLot { get; init; } = 0.1m;

    public Uri WebhookUrl { get; init; } = new("http://localhost:8089");

    /// <summary>Value sent as X-Internal-Key. Null means the server cannot reach the webhook.</summary>
    public string? InternalKey { get; init; }

    public string? ConnectionString { get; init; }

    public static McpSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new McpSettings
        {
            DryRun = !IsExactly(configuration[DryRunVariable], "false"),
            AllowLive = IsExactly(configuration[AllowLiveVariable], "true"),
            InternalKey = Core.InternalKey.Validate(configuration[Core.InternalKey.ConfigurationName]),
            ConnectionString = configuration.GetConnectionString("Default"),
        };

        if (configuration[MaxLotVariable] is { Length: > 0 } maxLot)
        {
            if (!decimal.TryParse(maxLot, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new InvalidOperationException($"{MaxLotVariable} must be a positive number, got '{maxLot}'.");
            settings = settings with { MaxLot = parsed };
        }

        if (configuration[WebhookUrlVariable] is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"{WebhookUrlVariable} must be an absolute http(s) URL, got '{url}'.");
            settings = settings with { WebhookUrl = parsed };
        }

        return settings;
    }

    private static bool IsExactly(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
