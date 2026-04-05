namespace WebhookFx.Middleware;

public class IpWhitelistMiddleware
{
    private static readonly HashSet<string> AllowedIps = new()
    {
        "52.89.214.238",
        "34.212.75.30",
        "54.218.53.128",
        "52.32.178.7"
    };

    private readonly RequestDelegate _next;

    public IpWhitelistMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();

        if (remoteIp is null || !AllowedIps.Contains(remoteIp))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
