namespace ShiroBot.Plugin.OneBotServer.Configuration;

/// <summary>Runtime configuration for the embedded OneBot v11 server.</summary>
public sealed record OneBotServerConfig
{
    public bool Enabled { get; init; } = true;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5700;
    public string SelfId { get; init; } = "0";
    public string? AccessToken { get; init; }
    public OneBotHttpServerConfig Http { get; init; } = new();
    public OneBotForwardWebSocketConfig ForwardWebSocket { get; init; } = new();
    public OneBotReverseWebSocketConfig ReverseWebSocket { get; init; } = new();
    public IReadOnlyList<OneBotHttpTargetConfig> HttpTargets { get; init; } = [];
    public OneBotHeartbeatConfig Heartbeat { get; init; } = new();
    public OneBotEventFormatConfig EventFormat { get; init; } = new();
    public OneBotStorageConfig Storage { get; init; } = new();
    public OneBotServerLimits Limits { get; init; } = new();
}

public sealed record OneBotHttpServerConfig
{
    public bool Enabled { get; init; } = true;
    public string Path { get; init; } = "/";
}

public sealed record OneBotForwardWebSocketConfig
{
    public bool Enabled { get; init; } = true;
    public string Path { get; init; } = "/";
}

public sealed record OneBotReverseWebSocketConfig
{
    public bool Enabled { get; init; }
    /// <summary>Universal reverse WebSocket endpoint. Retained as the runtime-compatible alias.</summary>
    public string? Url { get; init; }
    public string? UniversalUrl { get; init; }
    public string? ApiUrl { get; init; }
    public string? EventUrl { get; init; }
    public int ReconnectDelaySeconds { get; init; } = 5;
}

public sealed record OneBotHttpTargetConfig
{
    public required string Url { get; init; }
    public string? AccessToken { get; init; }
    public string? Secret { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
}

public sealed record OneBotHeartbeatConfig
{
    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; } = 15;
}

public sealed record OneBotEventFormatConfig
{
    public bool UseArrayMessage { get; init; }
    public bool IncludeRawMessage { get; init; } = true;
    public bool IncludeRawPayload { get; init; }
    public string Timezone { get; init; } = "UTC";
}

public sealed record OneBotStorageConfig
{
    public string MessageIdRegistryPath { get; init; } = "data/message-ids.json";
    public int RegistryMaxEntries { get; init; } = 100_000;
    public string? RequestFlagSecret { get; init; }
    public int RetentionDays { get; init; } = 7;
}

public sealed record OneBotServerLimits
{
    public int MaxRequestBodyBytes { get; init; } = 1_048_576;
    public int MaxWebSocketConnections { get; init; } = 32;
    public int MaxWebSocketMessageBytes { get; init; } = 1_048_576;
    public int EventQueueCapacity { get; init; } = 1_024;
}
