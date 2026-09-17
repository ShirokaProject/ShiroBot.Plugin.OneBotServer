using System.Text.Json;
using ShiroBot.Plugin.OneBotServer.Protocol;

namespace ShiroBot.Plugin.OneBotServer.Transports;

/// <summary>Boundary between transports and the plugin's action implementation.</summary>
public interface IOneBotActionHandler
{
    IReadOnlyCollection<string>? Actions => null;

    Task<OneBotResponse<object?>> HandleAsync(OneBotActionRequest request, CancellationToken cancellationToken);
}

internal sealed class DelegateOneBotActionHandler(
    Func<OneBotActionRequest, CancellationToken, Task<OneBotResponse<object?>>> handler,
    IReadOnlyCollection<string>? actions = null) : IOneBotActionHandler
{
    public IReadOnlyCollection<string>? Actions { get; } = actions;

    public Task<OneBotResponse<object?>> HandleAsync(OneBotActionRequest request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

internal static class OneBotActionHandlerAdapter
{
    public static IOneBotActionHandler Create<T>(T handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is IOneBotActionHandler typed) return typed;

        var method = typeof(T).GetMethod(
            "DispatchAsync",
            [typeof(OneBotActionRequest), typeof(CancellationToken)]);
        if (method is null || method.ReturnType != typeof(Task<OneBotResponse<object?>>))
            throw new ArgumentException("Action handler must implement IOneBotActionHandler or expose DispatchAsync(OneBotActionRequest, CancellationToken)", nameof(handler));

        var dispatch = method.CreateDelegate<Func<OneBotActionRequest, CancellationToken, Task<OneBotResponse<object?>>>>(handler);
        var actions = typeof(T).GetProperty(nameof(IOneBotActionHandler.Actions))?.GetValue(handler) as IReadOnlyCollection<string>;
        return new DelegateOneBotActionHandler(dispatch, actions);
    }
}

/// <summary>Consumes a quick-operation object returned by an HTTP event receiver.</summary>
public interface IOneBotQuickOperationHandler
{
    Task HandleAsync(JsonElement @event, JsonElement operation, CancellationToken cancellationToken);
}

public sealed class OneBotActionException(int statusCode, int retCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public int RetCode { get; } = retCode;
}

public enum OneBotWebSocketRole
{
    Universal,
    Api,
    Event,
}

public enum OneBotAuthorizationStatus
{
    Authorized,
    Missing,
    Invalid,
}

public sealed record OneBotServerOptions(
    IReadOnlyList<string> ListenUrls,
    string SelfId,
    string? AccessToken = null,
    int SocketQueueCapacity = 128,
    bool HttpEnabled = true,
    string HttpPath = "/",
    bool WebSocketEnabled = true,
    string WebSocketPath = "/",
    int MaxRequestBodyBytes = 1_048_576,
    int MaxWebSocketConnections = 32,
    int MaxWebSocketMessageBytes = 1_048_576);

public sealed record OneBotReverseWebSocketOptions(
    Uri Endpoint,
    string SelfId,
    OneBotWebSocketRole Role = OneBotWebSocketRole.Universal,
    string? AccessToken = null,
    TimeSpan? ReconnectDelay = null,
    int SocketQueueCapacity = 128,
    IReadOnlyDictionary<string, string>? Headers = null,
    int MaxWebSocketMessageBytes = 1_048_576);

public sealed record OneBotHttpEventPostOptions(
    IReadOnlyList<Uri> Endpoints,
    string? AccessToken = null,
    string? Secret = null,
    string? SelfId = null);
