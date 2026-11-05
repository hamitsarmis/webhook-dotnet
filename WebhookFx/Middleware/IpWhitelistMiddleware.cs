using WebhookFx.Core;

namespace WebhookFx.Middleware;

/// <summary>
/// Lets a request through when it comes from a TradingView address, or when it carries the
/// configured internal key. The key path is off unless WEBHOOKFX_INTERNAL_KEY is set.
/// </summary>
public class IpWhitelistMiddleware
{
    /// <summary>HttpContext.Items entry set on requests that were admitted by the internal key.</summary>
    public const string InternalCallerItem = "WebhookFx.InternalCaller";

    private static readonly HashSet<string> AllowedIps = new()
    {
        "52.89.214.238",
        "34.212.75.30",
        "54.218.53.128",
        "52.32.178.7"
    };

    private readonly RequestDelegate _next;
    private readonly string? _internalKey;

    public IpWhitelistMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _internalKey = InternalKey.Validate(configuration[InternalKey.ConfigurationName]);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // A request that presents a key is judged on the key alone. A wrong key is refused
        // even from a whitelisted address, so the header can never be used to probe.
        if (context.Request.Headers.TryGetValue(InternalKey.HeaderName, out var presented))
        {
            if (!InternalKey.Matches(presented.ToString(), _internalKey))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[InternalCallerItem] = true;
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();

        if (remoteIp is null || !AllowedIps.Contains(remoteIp))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
