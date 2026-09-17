using System.Text.Json.Serialization;

namespace ShiroBot.Plugin.OneBotServer.Events;

/// <summary>OneBot v11 event envelope delivered to HTTP and WebSocket transports.</summary>
public sealed record OneBotEvent
{
    [JsonPropertyName("time")]
    public required long Time { get; init; }

    [JsonPropertyName("self_id")]
    public required long SelfId { get; init; }

    [JsonPropertyName("post_type")]
    public required string PostType { get; init; }

    [JsonPropertyName("message_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageType { get; init; }

    [JsonPropertyName("notice_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoticeType { get; init; }

    [JsonPropertyName("request_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestType { get; init; }

    [JsonPropertyName("meta_event_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetaEventType { get; init; }

    [JsonPropertyName("sub_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubType { get; init; }

    [JsonExtensionData]
    public IDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
}
