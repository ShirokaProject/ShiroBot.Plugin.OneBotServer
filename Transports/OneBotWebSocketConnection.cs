using System.Net.WebSockets;
using System.Text;
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

    public async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            outbound.Writer.TryComplete();
        }
    }

    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        await using var stream = new MemoryStream();
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
            if (maxMessageBytes > 0 && stream.Length + result.Count > maxMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "WebSocket message is too large", cancellationToken).ConfigureAwait(false);
                LastCloseStatus = WebSocketCloseStatus.MessageTooBig;
                LastCloseDescription = "WebSocket message is too large";
                return null;
            }
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
        } while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
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
