# ShiroBot OneBot Server

当前发布：`v0.2.0`。本版本使用 SDK `0.9.8`，需要宿主 `0.9.8` 的新 ABI；旧宿主不兼容。

`ShiroBot.Plugin.OneBotServer` 将 ShiroBot 已加载适配器的通用消息能力转换为 OneBot 11 服务，并在可用时提供 QQ 扩展能力。

插件支持：

- OneBot 11 HTTP API
- 正向 WebSocket：Universal、API、Event 三种角色
- 反向 WebSocket：Universal 或 API/Event 分离连接
- HTTP POST 事件上报与 HMAC-SHA1 签名
- 生命周期事件和心跳
- CQ 码与 OneBot 消息段
- 持久化 OneBot `message_id` 映射
- HTTP 回调快速操作
- OneBot 11 标准 Action 与 LLOneBot/go-cqhttp 扩展 Action

## 运行要求

- .NET 10
- ShiroBot 0.9.8 或更高版本
- ShiroBot API 0.9.2
- `ShiroBot.Model.QQ` 0.9.8 或更高版本
- `ShiroBot.SDK` NuGet 0.9.8
- 至少一个已加载的适配器实例

插件本身不连接平台。普通消息 API 使用 SDK 的平台无关模型；群管理、好友、文件和其他 QQ 扩展 action 需要适配器实现相应的可选 SDK 接口，否则会返回明确的 unsupported 错误。

如果当前 Adapter 不支持某项能力，插件会返回正常的 OneBot 失败响应：

```json
{
  "status": "failed",
  "retcode": 1404,
  "data": null,
  "message": "The active adapter does not provide this OneBot capability.",
  "wording": "The active adapter does not provide this OneBot capability."
}
```

插件不会把未实现的能力伪装成成功。

## 安装

从 GitHub Release 下载 `ShiroBot.Plugin.OneBotServer.zip`，然后通过 ShiroBot 插件市场或插件上传界面安装。

也可以解压后手动部署：

```text
plugins/
└── OneBotServer/
    └── ShiroBot.Plugin.OneBotServer.dll
```

首次加载后，插件会在自己的配置目录生成 `config.toml`。

## 基础配置

下面是可直接使用的完整 TOML 示例：

```toml
enabled = true
host = "127.0.0.1"
port = 5700

# 单实例部署建议显式绑定实例 ID；未填写时仅在宿主恰有一个适配器实例时自动选择。
# instance_id = "qq-main"

# 设为 "0" 时，插件会从当前 QQ Adapter 自动获取登录账号。
self_id = "0"

# 建议对非本机监听设置高强度随机令牌。
access_token = "change-me"

[http]
enabled = true
path = "/"

[forward_web_socket]
enabled = true
path = "/"

[reverse_web_socket]
enabled = false

# Universal 反向 WebSocket。url 是兼容旧配置的别名。
universal_url = "ws://127.0.0.1:2536/OneBotv11"
# url = "ws://127.0.0.1:2536/OneBotv11"

# Universal 与分离模式二选一。分离模式示例：
# api_url = "ws://127.0.0.1:2536/api"
# event_url = "ws://127.0.0.1:2536/event"

reconnect_delay_seconds = 5

[[http_targets]]
url = "https://example.com/onebot/events"
access_token = "target-token"

# 设置后会发送：X-Signature: sha1=<HMAC-SHA1>
secret = "hmac-secret"
timeout_seconds = 15

[heartbeat]
enabled = true
interval_seconds = 15

[event_format]

# true：message 使用 OneBot 消息段数组。
# false：message 使用 CQ 码字符串。
use_array_message = true
include_raw_message = true
include_raw_payload = false
timezone = "UTC"

[storage]
message_id_registry_path = "data/message-ids.json"
registry_max_entries = 100000

# 可选。用于签名好友请求和群请求 flag。
# 留空时会使用 access_token 和固定盐派生稳定密钥。
# request_flag_secret = "separate-secret"

retention_days = 7

[limits]
max_web_socket_connections = 32
event_queue_capacity = 1024
```

### 多个适配器实例

每个 `[[instances]]` 会启动独立 OneBot 监听端点。多实例时必须显式指定 `instance_id`，且各监听地址/端口不能冲突；标准 OneBot 请求没有实例选择字段，因此每个适配器实例需要自己的端口。`host`、`port` 和 `access_token` 可按实例覆盖顶层默认值。请求 flag 密钥、消息 ID 映射和插件存储目录均按实例隔离。实例 ID 是宿主提供的不透明字符串，不需要是 QQ 数字账号。

```toml
[[instances]]
instance_id = "qq-main"
host = "127.0.0.1"
port = 5700
access_token = "main-token"

[[instances]]
instance_id = "qq-alt"
host = "127.0.0.1"
port = 5701
access_token = "alt-token"
```

其他 HTTP、WebSocket、回调和事件格式选项仍可使用顶层配置。配置引用未加载/已移除的适配器实例时会明确报错并尝试恢复上一份配置，不会切换到默认实例。

如果只使用反向 WebSocket，可以关闭本地 HTTP 和正向 WebSocket：

```toml
[http]
enabled = false

[forward_web_socket]
enabled = false

[reverse_web_socket]
enabled = true
universal_url = "ws://127.0.0.1:2536/OneBotv11"
reconnect_delay_seconds = 3
```

## 服务端点

默认监听地址为 `127.0.0.1:5700`。

### HTTP API

```text
GET  /{action}
POST /{action}
```

示例：

```bash
curl "http://127.0.0.1:5700/get_login_info"
```

带令牌：

```bash
curl \
  -H "Authorization: Bearer change-me" \
  "http://127.0.0.1:5700/get_friend_list"
```

也支持查询参数令牌：

```text
?access_token=change-me
```

GET 查询参数、JSON 请求体和 `application/x-www-form-urlencoded` 均可作为 Action 参数。POST 请求体参数会覆盖同名查询参数。

### 正向 WebSocket

| 地址 | 角色 | 接收 Action | 接收事件 |
| --- | --- | --- | --- |
| `/` | Universal | 是 | 是 |
| `/api` | API | 是 | 否 |
| `/event` | Event | 否 | 是 |

如果 OneBot 客户端主动连接 ShiroBot，填写以下地址即可。

Universal 模式，推荐大多数客户端使用：

```text
ws://127.0.0.1:5700/
```

API/Event 分离模式：

```text
API WebSocket:   ws://127.0.0.1:5700/api
Event WebSocket: ws://127.0.0.1:5700/event
```

如果客户端与 ShiroBot 不在同一台机器，把 `127.0.0.1` 替换成 ShiroBot 所在机器的 IP 或域名。例如：

```text
ws://192.168.1.20:5700/
```

此时 ShiroBot 配置中的监听地址需要允许外部连接：

```toml
host = "0.0.0.0"
port = 5700
access_token = "change-me"

[forward_web_socket]
enabled = true
path = "/"
```

`0.0.0.0` 只用于服务端监听，不能作为客户端连接地址。客户端仍应填写 ShiroBot 的实际 IP 或域名。

配置了 `access_token` 后，客户端可使用以下任一种认证方式。

HTTP Header：

```text
Authorization: Bearer change-me
```

查询参数，适合不能设置 WebSocket Header 的客户端或浏览器：

```text
ws://127.0.0.1:5700/?access_token=change-me
ws://127.0.0.1:5700/api?access_token=change-me
ws://127.0.0.1:5700/event?access_token=change-me
```

客户端只支持填写一个 WebSocket 地址时，使用 Universal 地址 `/`。只有客户端明确支持 API/Event 双连接时，才分别填写 `/api` 和 `/event`。

连接成功时响应包含：

```text
X-Self-ID: 当前 QQ 账号
X-Client-Role: Universal | API | Event
```

Event 和 Universal 连接建立后会首先收到 `connect` 生命周期事件。

WebSocket Action 示例：

```json
{
  "action": "get_login_info",
  "params": {},
  "echo": {
    "requestId": 1
  }
}
```

响应会原样保留 `echo`。

### 反向 WebSocket

Universal 模式只建立一个连接，同时收发 Action 和事件。

反向模式用于对方程序监听 WebSocket，而 ShiroBot 主动连接对方。此时不是在对方客户端里填写 ShiroBot 地址，而是在插件配置中填写对方提供的地址：

```toml
[http]
enabled = false

[forward_web_socket]
enabled = false

[reverse_web_socket]
enabled = true
universal_url = "ws://127.0.0.1:2536/OneBotv11"
reconnect_delay_seconds = 3
```

分离模式会分别建立：

- API 连接：接收 Action 并返回结果
- Event 连接：发送 OneBot 事件

对应配置：

```toml
[reverse_web_socket]
enabled = true
api_url = "ws://127.0.0.1:2536/api"
event_url = "ws://127.0.0.1:2536/event"
reconnect_delay_seconds = 3
```

连接断开后会按照 `reconnect_delay_seconds` 固定间隔重连。

### HTTP 事件上报

每个 `[[http_targets]]` 都会收到 JSON POST 请求：

```text
Content-Type: application/json
X-Self-ID: 当前 QQ 账号
X-Signature: sha1=<签名>   # 配置 secret 时
```

如果接收端返回 JSON 对象，插件会将它作为 OneBot 快速操作处理，可用于回复、撤回、踢人、禁言及处理申请。

## Action 支持

插件注册了参考实现中的全部 123 个 Action 名称：

- 38 个 OneBot 11 标准 Action
- 快速操作 Action
- LLOneBot/go-cqhttp 扩展 Action
- 群文件和私聊文件 Action
- 明确注册但底层暂不支持的扩展 Action

当前 QQ Adapter 和 `ShiroBot.Model.QQ` 能表达的操作会执行真实调用，包括：

- 私聊和群消息发送、查询、撤回、历史记录及已读标记
- 好友、群、群成员和登录信息查询
- 群名、群头像、管理员、名片、头衔、禁言、全员禁言、踢人和退群
- 好友申请、入群申请和群邀请处理
- Cookie、CSRF、QQ 实现版本
- 戳一戳、消息表情回应、群公告和精华消息
- 合并转发和单消息转发
- 群文件、私聊文件和文件夹管理
- 事件长轮询 `get_event`
- HTTP 回调快速操作 `.handle_quick_operation`

底层 SDK 暂时没有对应能力的 Action 会返回 `retcode = 1404`，例如部分匿名群功能、群荣誉、AI 语音、群相册、OCR 和二维码登录。

异步后缀兼容：

```text
action_async
action_rate_limited
action_async_rate_limited
```

异步 Action 会立即返回：

```json
{
  "status": "async",
  "retcode": 1,
  "data": null
}
```

## 消息 ID

QQ 原生消息通常由会话类型、会话 ID 和消息序列号共同定位；OneBot 11 使用单个整数 `message_id`。

插件通过 `MessageIdRegistry` 建立有界、持久化映射：

```text
OneBot message_id <-> scene + peer_id + message_seq
```

映射使用临时文件和原子替换写入。重启后仍可继续执行 `get_msg`、`delete_msg`、回复、Reaction 和精华消息操作。

超过 `registry_max_entries` 后，最旧映射会被淘汰。使用已淘汰 ID 时会返回“unknown or expired”错误。

## 安全说明

- 默认只监听 `127.0.0.1`。
- 监听公网或局域网地址时必须设置 `access_token`，并使用防火墙限制来源。
- HTTP 请求体和 WebSocket 消息由中转层原样传递，不设置大小限制；是否接受由 QQ Adapter 或上游服务决定。
- WebSocket 每个连接使用有界发送队列，避免无限积压。
- 下载文件仅写入插件自身的 `storage/files` 目录。
- 本地媒体访问限制在插件允许的目录内。
- HTTP 下载有大小和超时限制。
- ZIP Release 不包含 `ShiroBot.SDK` 或 `ShiroBot.Model.*` 等宿主共享程序集。

## 构建与测试

```bash
dotnet build ShiroBot.Plugin.OneBotServer.csproj -c Release
dotnet test Tests/ShiroBot.Plugin.OneBotServer.Tests.csproj -c Release
dotnet publish ShiroBot.Plugin.OneBotServer.csproj -c Release
```

发布标签使用 `v*`，例如：

```bash
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions 会执行完整测试并生成：

```text
ShiroBot.Plugin.OneBotServer.zip
```

ZIP 中只包含插件 DLL，以及可选 PDB。SDK 与平台 Model 由 ShiroBot 宿主提供。

## 许可证

[MIT](./LICENSE)
