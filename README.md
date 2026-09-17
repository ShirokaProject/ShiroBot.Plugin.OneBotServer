# ShiroBot OneBot Server

`ShiroBot.Plugin.OneBotServer` exposes ShiroBot events and actions through the OneBot v11 protocol.

## Requirements

- .NET 10 host
- ShiroBot API 0.9
- `ShiroBot.Model.QQ` 0.9.1 or newer

## Configuration

The plugin creates `OneBotServerConfig` on first load. It covers the HTTP listener (`Host`, `Port`, `Http`), access token, forward and reverse WebSocket settings, HTTP post targets, heartbeat, event serialization, message-ID storage, and request/connection/queue limits.

Set `Enabled` to `false` to keep the plugin installed without opening a listener. A representative configuration is:

```toml
enabled = true
host = "127.0.0.1"
port = 5700
self_id = "0" # 0 means: resolve the active adapter account automatically
access_token = "change-me"

[http]
enabled = true
path = "/"

[forward_web_socket]
enabled = true
path = "/"

[reverse_web_socket]
enabled = false
universal_url = "ws://127.0.0.1:2536/OneBotv11"
# Or use split connections instead:
# api_url = "ws://127.0.0.1:2536/api"
# event_url = "ws://127.0.0.1:2536/event"
reconnect_delay_seconds = 5

[[http_targets]]
url = "https://example/onebot/events"
access_token = "target-token"
secret = "hmac-secret"
timeout_seconds = 15

[heartbeat]
enabled = true
interval_seconds = 15

[event_format]
use_array_message = true
include_raw_message = true
include_raw_payload = false
timezone = "UTC"

[storage]
message_id_registry_path = "data/message-ids.json"
registry_max_entries = 100000
# Optional. If omitted, the access token is used to derive a stable signing key.
# request_flag_secret = "separate-secret"
retention_days = 7

[limits]
max_request_body_bytes = 1048576
max_web_socket_connections = 32
max_web_socket_message_bytes = 1048576
event_queue_capacity = 1024
```

The HTTP action endpoint is `<http.path>/{action}`. Forward WebSocket clients connect to `forward_web_socket.path`, with `/api` and `/event` role-specific endpoints below it. Reverse WebSocket and HTTP targets receive converted events. Tokens are sent and accepted as bearer tokens.

The dispatcher exposes the same 123 action names as the reference Fraq implementation: 38 OneBot 11 standard actions plus LLOneBot/go-cqhttp extensions. Actions unsupported by the active QQ adapter return a normal OneBot failed response with `retcode = 1404`; they are never reported as successful.

## Packaging

Build with `dotnet publish -c Release`. Tags matching `v*` run the release workflow and produce `ShiroBot.Plugin.OneBotServer.zip`, containing only the plugin DLL and its PDB when produced. ShiroBot SDK and QQ model assemblies are host-shared and are not included in the package.

## Development

The plugin entry subscribes to `BotEvent`, so messages, common lifecycle events, and QQ-specific `QEventPayload` platform events all pass through `OneBotEventMapper`. The message-ID registry and OneBot action/transport implementations are intentionally separate integration points.
