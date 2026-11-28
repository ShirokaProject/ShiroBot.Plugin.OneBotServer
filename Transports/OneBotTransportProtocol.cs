using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using ShiroBot.Plugin.OneBotServer.Protocol;

namespace ShiroBot.Plugin.OneBotServer.Transports;

public static class OneBotTransportProtocol
{
    public static OneBotAuthorizationStatus GetAuthorizationStatus(HttpRequest request, string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return OneBotAuthorizationStatus.Authorized;

        var bearer = request.Headers.Authorization.ToString();
        if (bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            TokensEqual(bearer[7..], accessToken))
        {
            return OneBotAuthorizationStatus.Authorized;
        }

        if (request.Query.TryGetValue("access_token", out var queryToken) && TokensEqual(queryToken.ToString(), accessToken))
            return OneBotAuthorizationStatus.Authorized;

        return string.IsNullOrEmpty(bearer) && !request.Query.ContainsKey("access_token")
            ? OneBotAuthorizationStatus.Missing
            : OneBotAuthorizationStatus.Invalid;
    }

    public static bool IsAuthorized(HttpRequest request, string? accessToken) =>
        GetAuthorizationStatus(request, accessToken) == OneBotAuthorizationStatus.Authorized;

    public static string ComputeHmacSha1(string secret, ReadOnlySpan<byte> body) =>
        "sha1=" + Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static bool IsActionName(string? action) =>
        !string.IsNullOrWhiteSpace(action) && action.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static OneBotActionRequest ParseWebSocketAction(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("action", out var actionElement) ||
            actionElement.ValueKind != JsonValueKind.String ||
            !IsActionName(actionElement.GetString()))
        {
            throw new OneBotActionException(StatusCodes.Status400BadRequest, 1400, "action must be a valid action name");
        }

        var parameters = root.TryGetProperty("params", out var parameterElement)
            ? OneBotParameters.ObjectOrEmpty(parameterElement)
            : new Dictionary<string, JsonElement>();
        JsonElement? echo = root.TryGetProperty("echo", out var echoElement) ? echoElement.Clone() : null;
        return new OneBotActionRequest(actionElement.GetString()!, parameters, echo);
    }

    public static async Task<OneBotActionRequest> ParseHttpActionAsync(HttpRequest request, string action, CancellationToken cancellationToken, int maxBodyBytes = 0)
    {
        var (parsed, frame) = await ParseHttpActionFrameAsync(request, action, cancellationToken, maxBodyBytes, extractMedia: false).ConfigureAwait(false);
        if (frame is null) return parsed;
        using (frame)
        {
            return new OneBotActionRequest(parsed.Action,
                parsed.Params.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                parsed.Echo?.Clone());
        }
    }

    /// <summary>
    /// Parses an HTTP action without buffering Base64 media in memory: media strings are decoded to temporary
    /// files as the body streams in. The returned frame owns those files and the parameter JSON; keep it alive
    /// (and pass it to the executor) until the action has finished.
    /// </summary>
    internal static async Task<(OneBotActionRequest Request, WebSocketMediaFrame? Frame)> ParseHttpActionFrameAsync(
        HttpRequest request, string action, CancellationToken cancellationToken, int maxBodyBytes = 0, bool extractMedia = true)
    {
        if (!IsActionName(action))
            throw new OneBotActionException(StatusCodes.Status404NotFound, 1404, "action was not found");

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Query)
        {
            if (!string.Equals(key, "access_token", StringComparison.Ordinal)) values[key] = ToJson(value.ToArray());
        }

        if (maxBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        if (maxBodyBytes > 0 && request.ContentLength > maxBodyBytes)
            throw new OneBotActionException(StatusCodes.Status413PayloadTooLarge, 1413, "request body is too large");

        JsonElement? echo = null;
        if (HttpMethods.IsPost(request.Method) && request.ContentLength is not 0)
        {
            if (maxBodyBytes > 0) request.Body = new LimitedReadStream(request.Body, maxBodyBytes);
            if (request.HasFormContentType)
            {
                var formLimit = maxBodyBytes > 0 ? maxBodyBytes : int.MaxValue;
                var form = await request.ReadFormAsync(new FormOptions
                {
                    ValueLengthLimit = formLimit,
                    MultipartBodyLengthLimit = formLimit,
                    BufferBodyLengthLimit = formLimit,
                }, cancellationToken).ConfigureAwait(false);
                foreach (var (key, value) in form) values[key] = ToJson(value.ToArray());
            }
            else if (IsJsonContentType(request.ContentType))
            {
                var frame = await ReadJsonFrameAsync(request.Body, extractMedia, cancellationToken).ConfigureAwait(false);
                if (frame is null) return (new OneBotActionRequest(action, values, null), null);
                try
                {
                    var root = frame.Root;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new OneBotActionException(StatusCodes.Status400BadRequest, 1400, "JSON action parameters must be an object");

                    foreach (var property in root.EnumerateObject())
                    {
                        if (property.NameEquals("echo")) echo = property.Value;
                        else if (!property.NameEquals("action") && !property.NameEquals("params")) values[property.Name] = property.Value;
                        else if (property.NameEquals("params"))
                        {
                            foreach (var parameter in OneBotParameters.ObjectOrEmpty(property.Value)) values[parameter.Key] = parameter.Value;
                        }
                    }
                    return (new OneBotActionRequest(action, values, echo), frame);
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }
            else
            {
                throw new OneBotActionException(StatusCodes.Status415UnsupportedMediaType, 1400, "content type must be JSON or form data");
            }
        }

        return (new OneBotActionRequest(action, values, echo), null);
    }

    private static async Task<WebSocketMediaFrame?> ReadJsonFrameAsync(Stream body, bool extractMedia, CancellationToken cancellationToken)
    {
        using var reader = new WebSocketMediaFrameReader(rootIsParameters: true, extractMedia);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                reader.Write(buffer.AsSpan(0, read));
            }
            return total == 0 ? null : reader.Complete();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string RoleName(OneBotWebSocketRole role) => role switch
    {
        OneBotWebSocketRole.Universal => "Universal",
        OneBotWebSocketRole.Api => "API",
        OneBotWebSocketRole.Event => "Event",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool CanReceiveActions(OneBotWebSocketRole role) => role is OneBotWebSocketRole.Universal or OneBotWebSocketRole.Api;
    public static bool CanReceiveEvents(OneBotWebSocketRole role) => role is OneBotWebSocketRole.Universal or OneBotWebSocketRole.Event;

    private static bool TokensEqual(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ToJson(string?[] values)
    {
        object? value = values.Length == 1 ? values[0] : values;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> ReadArrayAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        private int Count(int count)
        {
            read += count;
            if (read > maxBytes)
                throw new OneBotActionException(StatusCodes.Status413PayloadTooLarge, 1413, "request body is too large");
            return count;
        }
    }
}
