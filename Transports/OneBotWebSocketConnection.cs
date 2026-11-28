using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace ShiroBot.Plugin.OneBotServer.Transports;

internal sealed class OneBotWebSocketConnection : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly Channel<JsonElement> outbound;

    private readonly int maxMessageBytes;

    public OneBotWebSocketConnection(WebSocket socket, OneBotWebSocketRole role, int queueCapacity, int maxMessageBytes)
    {
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (maxMessageBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        this.socket = socket;
        this.maxMessageBytes = maxMessageBytes;
        Role = role;
        outbound = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public OneBotWebSocketRole Role { get; }
    public bool IsOpen => socket.State == WebSocketState.Open;
    public WebSocketCloseStatus? LastCloseStatus { get; private set; }
    public string? LastCloseDescription { get; private set; }

    public ValueTask QueueAsync(JsonElement payload, CancellationToken cancellationToken) =>
        outbound.Writer.WriteAsync(payload.Clone(), cancellationToken);

    // Bound completed responses waiting on a slow peer, independently of the action execution slots.
    internal static async Task TrackActionAsync(HashSet<Task> pending, Task action)
    {
        pending.Add(action);
        if (pending.Count >= 32)
            await Task.WhenAny(pending).ConfigureAwait(false);
        foreach (var completed in pending.Where(task => task.IsCompleted).ToArray())
        {
            pending.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    public async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, writeDeadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            outbound.Writer.TryComplete();
        }
    }

    public async Task<WebSocketMediaFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var reader = new WebSocketMediaFrameReader();
        JsonException? invalidJson = null;
        long receivedBytes = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                LastCloseStatus = result.CloseStatus;
                LastCloseDescription = result.CloseStatusDescription;
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text) throw new OneBotActionException(400, 1400, "WebSocket frames must be text");
            receivedBytes += result.Count;
            if (maxMessageBytes > 0 && receivedBytes > maxMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "WebSocket message is too large", cancellationToken).ConfigureAwait(false);
                LastCloseStatus = WebSocketCloseStatus.MessageTooBig;
                LastCloseDescription = "WebSocket message is too large";
                return null;
            }
            if (invalidJson is null)
            {
                try
                {
                    reader.Write(buffer.AsSpan(0, result.Count));
                }
                catch (JsonException exception)
                {
                    // Drain the invalid message before reading the next action.
                    invalidJson = exception;
                }
            }
        } while (!result.EndOfMessage);

        if (invalidJson is not null) throw invalidJson;
        return reader.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        outbound.Writer.TryComplete();
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }

        socket.Dispose();
    }
}
