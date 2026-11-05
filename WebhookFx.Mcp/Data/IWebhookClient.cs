using WebhookFx.Core;

namespace WebhookFx.Mcp.Data;

/// <param name="StatusCode">HTTP status of POST /webhookfx.</param>
/// <param name="Text">The endpoint's answer, for example "Position opened.".</param>
/// <param name="Replayed">True when the endpoint answered from its idempotency record.</param>
public sealed record WebhookReply(int StatusCode, string Text, bool Replayed);

/// <summary>The only way the MCP server changes a position: the same endpoint TradingView calls.</summary>
public interface IWebhookClient
{
    Task<WebhookReply> SendAsync(WebhookRequest request, string idempotencyKey, CancellationToken cancellationToken);
}
