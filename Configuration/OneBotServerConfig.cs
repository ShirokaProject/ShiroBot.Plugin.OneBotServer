using ShiroBot.SDK.Config;

namespace ShiroBot.Plugin.OneBotServer.Configuration;

/// <summary>Runtime configuration for the embedded OneBot v11 server.</summary>
[ConfigModel]
public sealed record OneBotServerConfig
{
    internal string? LegacyMessageIdRegistryPath { get; init; }
    [ConfigField("启用 OneBot v11 端点及事件转发。", Label = "启用 OneBot 服务")]
    public bool Enabled { get; init; } = true;
    [ConfigField("默认监听地址，多实例端点可单独覆盖。", Label = "监听主机")]
    public string Host { get; init; } = "127.0.0.1";
    [ConfigField("默认 HTTP / 正向 WebSocket 端口，多实例端点可单独覆盖。", Label = "监听端口")]
    public int Port { get; init; } = 5700;
    /// <summary>Explicit adapter binding for one endpoint. If absent, exactly one loaded adapter instance is required.</summary>
    [ConfigField("单端点绑定的适配器实例；留空要求宿主恰好只有一个已加载实例。", Label = "适配器实例 ID")]
    public string? InstanceId { get; init; }
    /// <summary>Multiple independently addressed endpoints. Each entry may override host, port, and access token.</summary>
    [ConfigField("每个端点显式绑定一个适配器实例；非空时使用此列表建立独立端点。", Label = "多实例端点")]
    public IReadOnlyList<OneBotServerInstanceConfig> Instances { get; init; } = [];
    [ConfigField("优先使用适配器账号 ID；适配器无法查询账号时使用此值。多实例时请保持 0。", Label = "机器人身份 ID")]
    public string SelfId { get; init; } = "0";
    [ConfigField("HTTP 和 WebSocket 访问验证使用的令牌；留空不验证。", Label = "访问令牌", Type = "password")]
    public string? AccessToken { get; init; }
    [ConfigField("OneBot HTTP API 的开关和路径。", Label = "HTTP API")]
    public OneBotHttpServerConfig Http { get; init; } = new();
    [ConfigField("客户端连接插件的 WebSocket 开关和路径。", Label = "正向 WebSocket")]
    public OneBotForwardWebSocketConfig ForwardWebSocket { get; init; } = new();
    [ConfigField("插件主动连接 OneBot 客户端的地址和重连设置。", Label = "反向 WebSocket")]
    public OneBotReverseWebSocketConfig ReverseWebSocket { get; init; } = new();
    [ConfigField("将事件 POST 到这些 HTTP 目标，可分别设置鉴权和签名。", Label = "HTTP 事件上报")]
    public IReadOnlyList<OneBotHttpTargetConfig> HttpTargets { get; init; } = [];
    [ConfigField("OneBot 心跳事件开关和发送周期。", Label = "心跳事件")]
    public OneBotHeartbeatConfig Heartbeat { get; init; } = new();
    [ConfigField("消息结构、原始数据和时间格式设置。", Label = "事件格式")]
    public OneBotEventFormatConfig EventFormat { get; init; } = new();
    [ConfigField("消息 ID 映射、请求审批签名及保留策略。", Label = "数据存储")]
    public OneBotStorageConfig Storage { get; init; } = new();
    [ConfigField("WebSocket 连接数和事件队列容量限制。", Label = "连接与队列限制")]
    public OneBotServerLimits Limits { get; init; } = new();
}

public sealed record OneBotServerInstanceConfig
{
    [ConfigField("此端点绑定的宿主适配器实例 ID，必须填写。", Label = "实例 ID")]
    public required string InstanceId { get; init; }
    [ConfigField("留空继承全局监听主机。", Label = "监听主机")]
    public string? Host { get; init; }
    [ConfigField("留空继承全局端口；同时运行的端点必须避免监听冲突。", Label = "监听端口")]
    public int? Port { get; init; }
    [ConfigField("留空继承全局访问令牌。", Label = "访问令牌", Type = "password")]
    public string? AccessToken { get; init; }
}

public sealed record OneBotHttpServerConfig
{
    [ConfigField("启用此协议端点。", Label = "启用")]
    public bool Enabled { get; init; } = true;
    [ConfigField("此协议的监听路径，默认 /。", Label = "端点路径")]
    public string Path { get; init; } = "/";
}

public sealed record OneBotForwardWebSocketConfig
{
    [ConfigField("启用此协议端点。", Label = "启用")]
    public bool Enabled { get; init; } = true;
    [ConfigField("此协议的监听路径，默认 /。", Label = "端点路径")]
    public string Path { get; init; } = "/";
}

public sealed record OneBotReverseWebSocketConfig
{
    [ConfigField("开启后主动连接配置的 WebSocket 地址。", Label = "启用反向连接")]
    public bool Enabled { get; init; }
    /// <summary>Universal reverse WebSocket endpoint. Retained as the runtime-compatible alias.</summary>
    [ConfigField("未填写 UniversalUrl 时使用此地址。", Label = "通用连接地址")]
    public string? Url { get; init; }
    [ConfigField("同时处理 API 请求和事件的反向连接地址。", Label = "通用 WebSocket 地址")]
    public string? UniversalUrl { get; init; }
    [ConfigField("仅接收 API 调用的反向连接地址。", Label = "API WebSocket 地址")]
    public string? ApiUrl { get; init; }
    [ConfigField("仅发送事件的反向连接地址。", Label = "事件 WebSocket 地址")]
    public string? EventUrl { get; init; }
    [ConfigField("反向 WebSocket 断开后再次连接前的等待时间。", Label = "重连等待（秒）")]
    public int ReconnectDelaySeconds { get; init; } = 5;
}

public sealed record OneBotHttpTargetConfig
{
    [ConfigField("接收 OneBot 事件 HTTP POST 的地址。", Label = "上报地址")]
    public required string Url { get; init; }
    [ConfigField("发送给目标的 Bearer 访问令牌。", Label = "上报令牌", Type = "password")]
    public string? AccessToken { get; init; }
    [ConfigField("用于生成 OneBot X-Signature 的 HMAC-SHA1 密钥；留空不签名。", Label = "事件签名密钥", Type = "password")]
    public string? Secret { get; init; }
    [ConfigField("单次 HTTP 事件上报的请求超时时间。", Label = "上报超时（秒）")]
    public int TimeoutSeconds { get; init; } = 15;
}

public sealed record OneBotHeartbeatConfig
{
    [ConfigField("定期发送 OneBot meta_event 心跳。", Label = "启用心跳")]
    public bool Enabled { get; init; }
    [ConfigField("心跳事件的发送周期。", Label = "心跳间隔（秒）")]
    public int IntervalSeconds { get; init; } = 15;
}

public sealed record OneBotEventFormatConfig
{
    [ConfigField("开启后 message 使用消息段数组，否则使用 CQ 码字符串。", Label = "数组消息格式")]
    public bool UseArrayMessage { get; init; }
    [ConfigField("在事件中附加 raw_message 字段。", Label = "包含原始消息文本")]
    public bool IncludeRawMessage { get; init; } = true;
    [ConfigField("在事件中附加适配器原始数据。", Label = "包含平台原始数据")]
    public bool IncludeRawPayload { get; init; }
    [ConfigField("预留配置，当前事件时间仍使用 Unix 秒，此项尚不影响输出。", Label = "时区")]
    public string Timezone { get; init; } = "UTC";
}

public sealed record OneBotStorageConfig
{
    [ConfigField("保存 OneBot 消息 ID 映射的文件路径，相对路径基于插件目录。", Label = "消息 ID 映射文件")]
    public string MessageIdRegistryPath { get; init; } = "data/message-ids.json";
    [ConfigField("最多保留的消息 ID 映射数量。", Label = "映射条目上限")]
    public int RegistryMaxEntries { get; init; } = 100_000;
    [ConfigField("好友和入群申请 flag 的签名密钥；留空时基于访问令牌派生。", Label = "审批标识签名密钥", Type = "password")]
    public string? RequestFlagSecret { get; init; }
    [ConfigField("预留配置，当前仅按映射条目上限清理，尚未按天数清理。", Label = "映射保留天数")]
    public int RetentionDays { get; init; } = 7;
}

public sealed record OneBotServerLimits
{
    [ConfigField("允许同时保持的 WebSocket 客户端连接数量。", Label = "WebSocket 连接数上限")]
    public int MaxWebSocketConnections { get; init; } = 32;
    [ConfigField("等待转发的事件队列最大容量。", Label = "事件队列容量")]
    public int EventQueueCapacity { get; init; } = 1_024;
}
