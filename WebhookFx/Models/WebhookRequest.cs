using System.Text.Json.Serialization;

namespace WebhookFx.Models;

public class WebhookRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("pair")]
    public string Pair { get; set; } = string.Empty;

    [JsonPropertyName("entry_tag")]
    public string EntryTag { get; set; } = string.Empty;

    [JsonPropertyName("alert_message")]
    public string AlertMessage { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("allow_multiple")]
    public bool AllowMultiple { get; set; }

    [JsonPropertyName("lot")]
    public decimal Lot { get; set; }
}
