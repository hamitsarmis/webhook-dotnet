using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebhookFx.Core;

namespace WebhookFx.Mcp.Data;

public sealed class HttpWebhookClient : IWebhookClient
{
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly McpSettings _settings;

    public HttpWebhookClient(HttpClient http, McpSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<WebhookReply> SendAsync(
        WebhookRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (_settings.InternalKey is null)
            throw new InvalidOperationException($"{InternalKey.ConfigurationName} is not set.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_settings.WebhookUrl, "/webhookfx"))
        {
            Content = new StringContent(JsonSerializer.Serialize(request, BodyOptions), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add(InternalKey.HeaderName, _settings.InternalKey);
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new WebhookReply(
            (int)response.StatusCode,
            ReadText(body),
            response.Headers.Contains("Idempotent-Replayed"));
    }

    /// <summary>The endpoint answers with a JSON string; anything else is passed on as is.</summary>
    private static string ReadText(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
