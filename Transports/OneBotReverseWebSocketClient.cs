using System.Net.WebSockets;
using System.Text.Json;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.OneBotServer.Transports;

/// <summary>Reverse OneBot 11 WebSocket client with a fixed reconnect interval.</summary>
public sealed class OneBotReverseWebSocketClient
{
    private readonly OneBotReverseWebSocketOptions options;
    private readonly IOneBotActionHandler actions;
    private readonly TimeSpan reconnectDelay;
    private OneBotWebSocketConnection? connection;

    public OneBotReverseWebSocketClient(OneBotReverseWebSocketOptions options, IOneBotActionHandler actions)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        if (string.IsNullOrWhiteSpace(options.SelfId)) throw new ArgumentException("Self ID is required", nameof(options));
        if (options.SocketQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxWebSocketMessageBytes < 0) throw new ArgumentOutOfRangeException(nameof(options));
        reconnectDelay = options.ReconnectDelay ?? TimeSpan.FromSeconds(5);
        if (reconnectDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Endpoint.Scheme is not ("ws" or "wss")) throw new ArgumentException("Endpoint must use ws or wss", nameof(options));
    }

    public OneBotReverseWebSocketClient(OneBotReverseWebSocketOptions options, object actions)
        : this(options, OneBotActionHandlerAdapter.Create(actions))
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                BotLog.Error($"[OneBot/ReverseWS] {SafeEndpoint()} 连接异常 ({exception.GetType().Name}): {exception}");
            }

            if (!cancellationToken.IsCancellationRequested)
                BotLog.Warning($"[OneBot/ReverseWS] {SafeEndpoint()} 已断开，{reconnectDelay.TotalSeconds:0.###} 秒后重连。");
            await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishEventAsync<T>(T @event, CancellationToken cancellationToken = default)
    {
        var activeConnection = Volatile.Read(ref connection);
        if (activeConnection is null || !OneBotTransportProtocol.CanReceiveEvents(options.Role)) return;
        await activeConnection.QueueAsync(JsonSerializer.SerializeToElement(@event), cancellationToken).ConfigureAwait(false);
    }

    public Task PublishLifecycleAsync(string subType = "connect", CancellationToken cancellationToken = default) =>
        PublishEventAsync(LifecycleEvent(subType), cancellationToken);

    public Task PublishHeartbeatAsync(long intervalMilliseconds, CancellationToken cancellationToken = default) =>
        PublishEventAsync(new { post_type = "meta_event", meta_event_type = "heartbeat", status = new { online = true, good = true }, interval = intervalMilliseconds, self_id = NumericSelfId(), time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, cancellationToken);

    public async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        if (options.Headers is not null)
        {
            foreach (var (name, value) in options.Headers) socket.Options.SetRequestHeader(name, value);
        }
        socket.Options.SetRequestHeader("X-Self-ID", options.SelfId);
        socket.Options.SetRequestHeader("X-Client-Role", OneBotTransportProtocol.RoleName(options.Role));
        if (!string.IsNullOrEmpty(options.AccessToken)) socket.Options.SetRequestHeader("Authorization", "Bearer " + options.AccessToken);
        await socket.ConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);
        BotLog.Info($"[OneBot/ReverseWS] 已连接 {SafeEndpoint()}，role={OneBotTransportProtocol.RoleName(options.Role)}。");

        await using var connection = new OneBotWebSocketConnection(socket, options.Role, options.SocketQueueCapacity, options.MaxWebSocketMessageBytes);
        Volatile.Write(ref this.connection, connection);
        using var writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = connection.RunWriterAsync(writerCancellation.Token);
        try
        {
            if (OneBotTransportProtocol.CanReceiveEvents(options.Role))
                await connection.QueueAsync(JsonSerializer.SerializeToElement(LifecycleEvent("connect")), cancellationToken).ConfigureAwait(false);
            while (connection.IsOpen && !cancellationToken.IsCancellationRequested)
            {
                JsonElement? frame;
                try
                {
                    frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await connection.QueueAsync(JsonSerializer.SerializeToElement(
                        OneBotResponse<object?>.Failed(1400, "invalid JSON")), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (frame is null)
                {
                    BotLog.Warning($"[OneBot/ReverseWS] 对端关闭 {SafeEndpoint()}，status={connection.LastCloseStatus?.ToString() ?? "unknown"}，reason={connection.LastCloseDescription ?? "none"}。");
                    return;
                }
                if (!OneBotTransportProtocol.CanReceiveActions(options.Role)) continue;
                await HandleActionAsync(connection, frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref this.connection, null, connection);
            writerCancellation.Cancel();
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task HandleActionAsync(OneBotWebSocketConnection connection, JsonElement frame, CancellationToken cancellationToken)
    {
        var echo = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("echo", out var echoElement)
            ? echoElement.Clone()
            : (JsonElement?)null;
        OneBotResponse<object?> response;
        try
        {
            var request = OneBotTransportProtocol.ParseWebSocketAction(frame);
            response = (await actions.HandleAsync(request, cancellationToken).ConfigureAwait(false)) with { Echo = request.Echo };
        }
        catch (OneBotActionException exception)
        {
            response = OneBotResponse<object?>.Failed(exception.RetCode, exception.Message, echo);
        }
        catch (OneBotParameterException exception)
        {
            response = OneBotResponse<object?>.Failed(1400, exception.Message, echo);
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot/ReverseWS] Action 处理异常 ({exception.GetType().Name}): {exception}");
            response = OneBotResponse<object?>.Failed(1500, "internal server error", echo);
        }

        await connection.QueueAsync(JsonSerializer.SerializeToElement(response), cancellationToken).ConfigureAwait(false);
    }

    private object LifecycleEvent(string subType) => new
    {
        post_type = "meta_event",
        meta_event_type = "lifecycle",
        sub_type = subType,
        self_id = NumericSelfId(),
        time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    private long NumericSelfId() => long.TryParse(options.SelfId, out var selfId) ? selfId : 0;

    private string SafeEndpoint() => $"{options.Endpoint.Scheme}://{options.Endpoint.Authority}{options.Endpoint.AbsolutePath}";
}
