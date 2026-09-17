using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.OneBotServer.Transports;

/// <summary>Self-hosted OneBot 11 HTTP and forward WebSocket transport.</summary>
public sealed class OneBotKestrelServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly IOneBotActionHandler actions;
    private readonly OneBotServerOptions options;
    private readonly ConcurrentDictionary<Guid, OneBotWebSocketConnection> sockets = new();

    private OneBotKestrelServer(WebApplication app, IOneBotActionHandler actions, OneBotServerOptions options)
    {
        this.app = app;
        this.actions = actions;
        this.options = options;
    }

    public static async Task<OneBotKestrelServer> StartAsync(
        OneBotServerOptions options,
        IOneBotActionHandler actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(actions);
        if (options.ListenUrls.Count == 0) throw new ArgumentException("At least one listen URL is required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.SelfId)) throw new ArgumentException("Self ID is required", nameof(options));
        if (options.SocketQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxRequestBodyBytes < 0 || options.MaxWebSocketMessageBytes < 0) throw new ArgumentOutOfRangeException(nameof(options));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        builder.WebHost.UseUrls(options.ListenUrls.ToArray());
        var app = builder.Build();
        var server = new OneBotKestrelServer(app, actions, options);
        app.UseWebSockets();
        server.MapEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return server;
    }

    public static Task<OneBotKestrelServer> StartAsync<TActionHandler>(
        OneBotServerOptions options,
        TActionHandler actions,
        CancellationToken cancellationToken = default)
        where TActionHandler : class =>
        StartAsync(options, OneBotActionHandlerAdapter.Create(actions), cancellationToken);

    public IReadOnlyCollection<string> Addresses =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];

    public async Task PublishEventAsync<T>(T @event, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(@event);
        var recipients = sockets.ToArray().Where(pair => OneBotTransportProtocol.CanReceiveEvents(pair.Value.Role));
        await Task.WhenAll(recipients.Select(async pair =>
        {
            try
            {
                await pair.Value.QueueAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                sockets.TryRemove(pair.Key, out _);
            }
        })).ConfigureAwait(false);
    }

    public Task PublishLifecycleAsync(string subType = "connect", CancellationToken cancellationToken = default) =>
        PublishEventAsync(LifecycleEvent(subType), cancellationToken);

    public Task PublishHeartbeatAsync(long intervalMilliseconds, CancellationToken cancellationToken = default) =>
        PublishEventAsync(new { post_type = "meta_event", meta_event_type = "heartbeat", status = new { online = true, good = true }, interval = intervalMilliseconds, self_id = NumericSelfId(), time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, cancellationToken);

    private void MapEndpoints()
    {
        if (options.WebSocketEnabled)
        {
            app.MapGet(options.WebSocketPath, HandleWebSocketAsync);
            app.MapGet(CombinePath(options.WebSocketPath, "api"), context => HandleWebSocketAsync(context, OneBotWebSocketRole.Api));
            app.MapGet(CombinePath(options.WebSocketPath, "event"), context => HandleWebSocketAsync(context, OneBotWebSocketRole.Event));
        }
        if (options.HttpEnabled)
            app.MapMethods(CombinePath(options.HttpPath, "{action}"), [HttpMethods.Get, HttpMethods.Post], HandleHttpActionAsync);
    }

    private async Task HandleHttpActionAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var authorization = OneBotTransportProtocol.GetAuthorizationStatus(context.Request, options.AccessToken);
        if (authorization != OneBotAuthorizationStatus.Authorized)
        {
            var statusCode = authorization == OneBotAuthorizationStatus.Missing
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            var retCode = authorization == OneBotAuthorizationStatus.Missing ? 1401 : 1403;
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(retCode, "unauthorized"), statusCode).ConfigureAwait(false);
            BotLog.Warning($"[OneBot/HTTP] {remote} 认证失败，HTTP {statusCode}，耗时 {stopwatch.ElapsedMilliseconds}ms。");
            return;
        }

        var action = context.Request.RouteValues["action"]?.ToString() ?? string.Empty;
        if (!IsRegisteredAction(action))
        {
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1404, "action was not found"), StatusCodes.Status404NotFound).ConfigureAwait(false);
            BotLog.Warning($"[OneBot/HTTP] {remote} 调用未知 Action {SafeAction(action)}，HTTP 404，耗时 {stopwatch.ElapsedMilliseconds}ms。");
            return;
        }

        OneBotActionRequest request;
        try
        {
            request = await OneBotTransportProtocol.ParseHttpActionAsync(context.Request, action, context.RequestAborted, options.MaxRequestBodyBytes).ConfigureAwait(false);
        }
        catch (OneBotActionException exception)
        {
            BotLog.Warning($"[OneBot/HTTP] {remote} 解析 Action {SafeAction(action)} 失败 retcode={exception.RetCode}: {exception.Message}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(exception.RetCode, exception.Message), exception.StatusCode).ConfigureAwait(false);
            return;
        }
        catch (OneBotParameterException exception)
        {
            BotLog.Warning($"[OneBot/HTTP] {remote} 解析 Action {SafeAction(action)} 参数失败: {exception.Message}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1400, exception.Message), StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }
        catch (JsonException exception)
        {
            BotLog.Warning($"[OneBot/HTTP] {remote} 解析 Action {SafeAction(action)} JSON 失败: {exception.Message}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1400, "invalid JSON"), StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot/HTTP] {remote} 解析 Action {SafeAction(action)} 异常 ({exception.GetType().Name}): {exception}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1500, "internal server error"), StatusCodes.Status500InternalServerError).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await actions.HandleAsync(request, context.RequestAborted).ConfigureAwait(false);
            await WriteJsonAsync(context, response, StatusCodes.Status200OK).ConfigureAwait(false);
            LogActionResult("HTTP", remote, request.Action, response.RetCode, response.Message, stopwatch.ElapsedMilliseconds);
        }
        catch (OneBotActionException exception)
        {
            BotLog.Warning($"[OneBot/HTTP] {remote} Action {SafeAction(action)} 失败 retcode={exception.RetCode}: {exception.Message}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(exception.RetCode, exception.Message), StatusCodes.Status200OK).ConfigureAwait(false);
        }
        catch (OneBotParameterException exception)
        {
            BotLog.Warning($"[OneBot/HTTP] {remote} Action {SafeAction(action)} 参数失败: {exception.Message}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1400, exception.Message), StatusCodes.Status200OK).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot/HTTP] {remote} Action {SafeAction(action)} 异常 ({exception.GetType().Name}): {exception}");
            await WriteJsonAsync(context, OneBotResponse<object?>.Failed(1500, "internal server error"), StatusCodes.Status200OK).ConfigureAwait(false);
        }
    }

    private Task HandleWebSocketAsync(HttpContext context) => HandleWebSocketAsync(context, OneBotWebSocketRole.Universal);

    private async Task HandleWebSocketAsync(HttpContext context, OneBotWebSocketRole expectedRole)
    {
        if (sockets.Count >= options.MaxWebSocketConnections)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        var authorization = OneBotTransportProtocol.GetAuthorizationStatus(context.Request, options.AccessToken);
        if (authorization != OneBotAuthorizationStatus.Authorized)
        {
            context.Response.StatusCode = authorization == OneBotAuthorizationStatus.Missing
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            return;
        }

        var role = expectedRole;
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        context.Response.Headers["X-Self-ID"] = options.SelfId;
        context.Response.Headers["X-Client-Role"] = OneBotTransportProtocol.RoleName(role);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await using var connection = new OneBotWebSocketConnection(socket, role, options.SocketQueueCapacity, options.MaxWebSocketMessageBytes);
        var id = Guid.NewGuid();
        sockets[id] = connection;
        BotLog.Info($"[OneBot/WS] {remote} 已连接，role={OneBotTransportProtocol.RoleName(role)}。");
        using var writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var writer = connection.RunWriterAsync(writerCancellation.Token);
        try
        {
            if (OneBotTransportProtocol.CanReceiveEvents(role))
                await connection.QueueAsync(JsonSerializer.SerializeToElement(LifecycleEvent("connect")), context.RequestAborted).ConfigureAwait(false);
            while (connection.IsOpen && !context.RequestAborted.IsCancellationRequested)
            {
                JsonElement? frame;
                try
                {
                    frame = await connection.ReceiveAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await connection.QueueAsync(JsonSerializer.SerializeToElement(
                        OneBotResponse<object?>.Failed(1400, "invalid JSON")), context.RequestAborted).ConfigureAwait(false);
                    continue;
                }
                if (frame is null) break;
                await HandleWebSocketActionAsync(connection, frame.Value, role, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            sockets.TryRemove(id, out _);
            writerCancellation.Cancel();
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            BotLog.Info($"[OneBot/WS] {remote} 已断开，role={OneBotTransportProtocol.RoleName(role)}，status={connection.LastCloseStatus?.ToString() ?? "aborted"}，reason={connection.LastCloseDescription ?? "none"}。");
        }
    }

    private static string CombinePath(string root, string child)
    {
        var normalizedRoot = "/" + root.Trim('/');
        return normalizedRoot == "/" ? $"/{child}" : $"{normalizedRoot}/{child}";
    }

    private async Task HandleWebSocketActionAsync(OneBotWebSocketConnection connection, JsonElement frame, OneBotWebSocketRole role, CancellationToken cancellationToken)
    {
        var echo = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("echo", out var echoElement)
            ? echoElement.Clone()
            : (JsonElement?)null;
        OneBotResponse<object?> response;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!OneBotTransportProtocol.CanReceiveActions(role))
                throw new OneBotActionException(StatusCodes.Status403Forbidden, 1403, "this WebSocket connection does not accept API requests");
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
        catch (Exception)
        {
            response = OneBotResponse<object?>.Failed(1500, "internal server error", echo);
        }

        await connection.QueueAsync(JsonSerializer.SerializeToElement(response), cancellationToken).ConfigureAwait(false);
        var action = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("action", out var actionElement)
            ? actionElement.GetString() ?? "unknown"
            : "unknown";
        LogActionResult("WS", OneBotTransportProtocol.RoleName(role), action, response.RetCode, response.Message, stopwatch.ElapsedMilliseconds);
    }

    private static async Task WriteJsonAsync(HttpContext context, object response, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(response, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsRegisteredAction(string action)
    {
        if (!OneBotTransportProtocol.IsActionName(action)) return false;
        if (actions.Actions is null) return true;
        var normalized = action.EndsWith("_rate_limited", StringComparison.Ordinal)
            ? action[..^"_rate_limited".Length]
            : action.EndsWith("_async", StringComparison.Ordinal) ? action[..^"_async".Length] : action;
        return actions.Actions.Contains(normalized, StringComparer.Ordinal);
    }

    private static void LogActionResult(string transport, string source, string action, int retCode, string? message, long elapsedMilliseconds)
    {
        var safeAction = SafeAction(action);
        if (retCode == 0 || retCode == 1)
            BotLog.Log($"[OneBot/{transport}] {source} 调用 {safeAction} 完成，retcode={retCode}，耗时 {elapsedMilliseconds}ms。");
        else
            BotLog.Warning($"[OneBot/{transport}] {source} 调用 {safeAction} 失败，retcode={retCode}，耗时 {elapsedMilliseconds}ms，原因: {message ?? "unknown"}。");
    }

    private static string SafeAction(string action) => action.Length <= 100 ? action : action[..100] + "...";

    private object LifecycleEvent(string subType) => new
    {
        post_type = "meta_event",
        meta_event_type = "lifecycle",
        sub_type = subType,
        self_id = NumericSelfId(),
        time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    private long NumericSelfId() => long.TryParse(options.SelfId, out var selfId) ? selfId : 0;

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, connection) in sockets) await connection.DisposeAsync().ConfigureAwait(false);
        sockets.Clear();
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}
