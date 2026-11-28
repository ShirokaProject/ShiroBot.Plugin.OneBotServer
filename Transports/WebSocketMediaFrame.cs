using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.OneBotServer.Transports;

internal sealed class WebSocketMediaFrame(JsonDocument json, DirectoryInfo? mediaDirectory) : IDisposable
{
    private int references = 1;

    public JsonElement Root => json.RootElement;

    internal void Retain() => Interlocked.Increment(ref references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        json.Dispose();
        DeleteMediaDirectory(mediaDirectory);
    }

    internal static void DeleteMediaDirectory(DirectoryInfo? directory)
    {
        if (directory is null) return;
        try
        {
            directory.Delete(recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            BotLog.Warning("[OneBot/WS] 临时媒体目录清理失败。");
        }
        catch (UnauthorizedAccessException)
        {
            BotLog.Warning("[OneBot/WS] 临时媒体目录清理失败。");
        }
    }
}

/// <summary>The frame owning the JSON and temporary media of the action currently executing on this async flow.</summary>
internal static class OneBotRequestFrame
{
    private static readonly AsyncLocal<WebSocketMediaFrame?> current = new();

    internal static WebSocketMediaFrame? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    /// <summary>Keeps the current request's parameters and media files alive for work that outlives the action.</summary>
    internal static IDisposable? Hold()
    {
        var frame = current.Value;
        frame?.Retain();
        return frame;
    }
}

// Keep JSON validation in JsonDocument. The scanner only replaces Base64 media
// string values at the paths used by structured OneBot requests. HTTP bodies use the
// same reader: their root object is the parameters object (optionally wrapped in "params").
internal sealed class WebSocketMediaFrameReader(bool rootIsParameters = false, bool extractMedia = true) : IDisposable
{
    private const int BlockSize = 16 * 1024;
    private static ReadOnlySpan<byte> Base64Prefix => "base64://"u8;

    private readonly MemoryStream json = new();
    private readonly List<Scope> scopes = [];
    private readonly byte[] key = new byte[64];
    private readonly byte[] prefix = new byte[9 * 6];
    private DirectoryInfo? mediaDirectory;
    private FileStream? media;
    private byte[]? encoded;
    private byte[]? decoded;
    private int keyLength;
    private int prefixLength;
    private int prefixMatched;
    private int encodedLength;
    private int unicodeRemaining;
    private int unicodeValue;
    private bool escaped;
    private bool padded;
    private StringKind stringKind;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (stringKind == StringKind.None) WriteOutsideString(value);
            else if (stringKind is StringKind.Key or StringKind.Plain) WritePlainString(value);
            else WriteMediaString(value);
        }
    }

    public WebSocketMediaFrame Complete()
    {
        if (stringKind is StringKind.Probe or StringKind.Media)
            throw new JsonException("Incomplete WebSocket media string.");
        var document = JsonDocument.Parse(json.GetBuffer().AsMemory(0, checked((int)json.Length)));
        var frame = new WebSocketMediaFrame(document, mediaDirectory);
        mediaDirectory = null;
        return frame;
    }

    private void WriteOutsideString(byte value)
    {
        json.WriteByte(value);
        var parent = scopes.Count > 0 ? scopes[^1] : null;
        switch (value)
        {
            case (byte)'{' or (byte)'[':
                if (scopes.Count >= 64) throw new JsonException("WebSocket JSON is too deeply nested.");
                scopes.Add(new Scope(ChildPath(parent, value), value == '{'));
                break;
            case (byte)'}' or (byte)']' when parent is not null:
                scopes.RemoveAt(scopes.Count - 1);
                break;
            case (byte)',' when parent is { IsObject: true }:
                parent.Key = null;
                parent.ExpectsKey = true;
                break;
            case (byte)'"':
                if (parent is { ExpectsKey: true })
                {
                    stringKind = StringKind.Key;
                    keyLength = 0;
                }
                else if (IsMediaValue(parent))
                {
                    stringKind = StringKind.Probe;
                    prefixLength = 0;
                    prefixMatched = 0;
                }
                else
                {
                    stringKind = StringKind.Plain;
                }
                break;
        }
    }

    private PathKind ChildPath(Scope? parent, byte value)
    {
        if (parent is null) return value != '{' ? PathKind.Other : rootIsParameters ? PathKind.Parameters : PathKind.Root;
        if (rootIsParameters && scopes.Count == 1 && parent is { Path: PathKind.Parameters, Key: "params" } && value == '{')
            return PathKind.Parameters;
        return (parent.Path, parent.Key, value) switch
        {
            (PathKind.Root, "params", (byte)'{') => PathKind.Parameters,
            (PathKind.Parameters, "message" or "messages", (byte)'[') => PathKind.Segments,
            (PathKind.Parameters, "message", (byte)'{') => PathKind.Segment,
            (PathKind.Segments, _, (byte)'{') => PathKind.Segment,
            (PathKind.Segment, "data", (byte)'{') => PathKind.SegmentData,
            (PathKind.SegmentData, "content", (byte)'[') => PathKind.Segments,
            (PathKind.SegmentData, "content", (byte)'{') => PathKind.Segment,
            _ => PathKind.Other
        };
    }

    private bool IsMediaValue(Scope? scope) => extractMedia && scope switch
    {
        { Path: PathKind.Parameters, Key: "file" or "file_uri" } => true,
        { Path: PathKind.SegmentData, Key: "file" or "url" or "cover" or "thumb" } => true,
        _ => false
    };

    private void WritePlainString(byte value)
    {
        json.WriteByte(value);
        if (!escaped && value == '"')
        {
            if (stringKind == StringKind.Key)
            {
                var scope = scopes[^1];
                scope.Key = DecodeKey();
                scope.ExpectsKey = false;
            }
            stringKind = StringKind.None;
            return;
        }

        if (stringKind == StringKind.Key && keyLength <= key.Length)
        {
            if (keyLength < key.Length) key[keyLength] = value;
            keyLength++;
        }
        escaped = !escaped && value == '\\';
    }

    private string? DecodeKey()
    {
        if (keyLength > key.Length) return null;
        var value = key.AsSpan(0, keyLength);
        if (!value.Contains((byte)'\\')) return Encoding.UTF8.GetString(value);
        Span<byte> quoted = stackalloc byte[keyLength + 2];
        quoted[0] = quoted[^1] = (byte)'"';
        value.CopyTo(quoted[1..]);
        return JsonSerializer.Deserialize<string>(quoted);
    }

    private void WriteMediaString(byte value)
    {
        if (stringKind == StringKind.Probe)
            prefix[prefixLength++] = value;
        if (!TryReadMediaCharacter(value, out int character)) return;

        if (stringKind == StringKind.Probe)
        {
            if (character >= 0 &&
                char.ToLowerInvariant((char)character) == Base64Prefix[prefixMatched])
            {
                if (++prefixMatched == Base64Prefix.Length) StartMedia();
                return;
            }
            json.Write(prefix.AsSpan(0, prefixLength));
            stringKind = character < 0 ? StringKind.None : StringKind.Plain;
            return;
        }

        if (character < 0)
        {
            CompleteMedia();
            stringKind = StringKind.None;
            return;
        }

        if (character is ' ' or '\r' or '\n' or '\t') return;
        if (padded || character > 127) throw new JsonException("Invalid Base64 media.");
        encoded![encodedLength++] = (byte)character;
        if (encodedLength == BlockSize)
            FlushMedia();
    }

    private bool TryReadMediaCharacter(byte value, out int character)
    {
        character = 0;
        if (unicodeRemaining > 0)
        {
            int hex = value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - '0',
                >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
                _ => throw new JsonException("Invalid JSON string escape.")
            };
            unicodeValue = (unicodeValue << 4) | hex;
            if (--unicodeRemaining != 0) return false;
            character = unicodeValue;
            return true;
        }

        if (escaped)
        {
            escaped = false;
            if (value == 'u')
            {
                unicodeRemaining = 4;
                unicodeValue = 0;
                return false;
            }
            character = value switch
            {
                (byte)'"' or (byte)'\\' or (byte)'/' => value,
                (byte)'b' => '\b',
                (byte)'f' => '\f',
                (byte)'n' => '\n',
                (byte)'r' => '\r',
                (byte)'t' => '\t',
                _ => throw new JsonException("Invalid JSON string escape.")
            };
            return true;
        }

        if (value == '\\')
        {
            escaped = true;
            return false;
        }
        if (value < 32) throw new JsonException("Invalid JSON string character.");
        character = value == '"' ? -1 : value;
        return true;
    }

    private void StartMedia()
    {
        mediaDirectory ??= Directory.CreateTempSubdirectory("shirobot-onebot-media-");
        var path = Path.Combine(mediaDirectory.FullName, Path.GetRandomFileName());
        media = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        encoded ??= ArrayPool<byte>.Shared.Rent(BlockSize);
        decoded ??= ArrayPool<byte>.Shared.Rent(BlockSize / 4 * 3);
        encodedLength = 0;
        padded = false;
        stringKind = StringKind.Media;
    }

    private void FlushMedia()
    {
        var input = encoded.AsSpan(0, encodedLength);
        if (Base64.DecodeFromUtf8(input, decoded, out _, out int written) != OperationStatus.Done)
            throw new JsonException("Invalid Base64 media.");
        media!.Write(decoded.AsSpan(0, written));
        padded = input[^1] == '=';
        encodedLength = 0;
    }

    private void CompleteMedia()
    {
        if (encodedLength > 0) FlushMedia();
        var file = media!;
        file.Dispose();
        media = null;
        // The opening JSON quote was already copied by WriteOutsideString.
        json.SetLength(json.Length - 1);
        json.Position = json.Length;
        json.Write(JsonSerializer.SerializeToUtf8Bytes(new Uri(file.Name).AbsoluteUri));
    }

    public void Dispose()
    {
        try
        {
            media?.Dispose();
        }
        finally
        {
            if (encoded is not null) ArrayPool<byte>.Shared.Return(encoded, clearArray: true);
            if (decoded is not null) ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
            json.Dispose();
            WebSocketMediaFrame.DeleteMediaDirectory(mediaDirectory);
        }
    }

    private sealed class Scope(PathKind path, bool isObject)
    {
        internal PathKind Path { get; } = path;
        internal bool IsObject { get; } = isObject;
        internal bool ExpectsKey { get; set; } = isObject;
        internal string? Key { get; set; }
    }

    private enum StringKind { None, Key, Plain, Probe, Media }
    private enum PathKind { Other, Root, Parameters, Segments, Segment, SegmentData }
}
