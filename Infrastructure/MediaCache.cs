using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed class MediaCache
{
    private readonly string directory;
    private readonly string? localRoot;
    private readonly long maxBytes;
    private readonly HttpClient client;
    private readonly Func<string, CancellationToken, Task<string>>? resolveResource;
    private readonly TimeSpan downloadTimeout;
    private readonly ConcurrentDictionary<string, Task<string>> pending = new(StringComparer.Ordinal);

    public MediaCache(string directory, long maxBytes = 64 * 1024 * 1024, string? localRoot = null, HttpClient? client = null, Func<string, CancellationToken, Task<string>>? resolveResource = null, TimeSpan? downloadTimeout = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.localRoot = Path.GetFullPath(localRoot ?? AppContext.BaseDirectory);
        this.maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.client = client ?? new HttpClient();
        this.resolveResource = resolveResource;
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromSeconds(30);
        if (this.downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<string> CacheAsync(string source, CancellationToken cancellationToken = default)
    {
        return await pending.GetOrAdd(source, _ => CacheCoreAsync(source, cancellationToken));
    }

    private async Task<string> CacheCoreAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadAsync(source, cancellationToken);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "media-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            if (File.Exists(target)) return target;
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target)) { }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return target;
        }
        finally
        {
            pending.TryRemove(source, out _);
        }
    }

    public async Task<string> OutgoingUriAsync(string source, CancellationToken cancellationToken = default)
    {
        if (source.StartsWith("base64://", StringComparison.Ordinal))
        {
            _ = DecodeBase64(source["base64://".Length..]);
            return source;
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return source;
        return "base64://" + Convert.ToBase64String(await ReadAsync(source, cancellationToken));
    }

    private async Task<byte[]> ReadAsync(string source, CancellationToken cancellationToken)
    {
        if (source.StartsWith("resource://", StringComparison.Ordinal))
        {
            if (resolveResource is null) throw new UnauthorizedAccessException("No resource resolver is configured");
            return await ReadAsync(await resolveResource(source["resource://".Length..], cancellationToken), cancellationToken);
        }
        if (source.StartsWith("base64://", StringComparison.Ordinal)) return DecodeBase64(source["base64://".Length..]);
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(downloadTimeout);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Media exceeds the size limit");
            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadBoundedAsync(responseStream, timeout.Token);
        }
        var path = source.StartsWith("file://", StringComparison.Ordinal) ? new Uri(source).LocalPath : source;
        var fullPath = Path.GetFullPath(path);
        if (localRoot is null || !fullPath.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && fullPath != localRoot) throw new UnauthorizedAccessException("Local media access is disabled outside the configured plugin directory");
        await using var stream = File.OpenRead(fullPath);
        return await ReadBoundedAsync(stream, cancellationToken);
    }

    private byte[] DecodeBase64(string value)
    {
        try { var bytes = Convert.FromBase64String(value); return bytes.Length <= maxBytes ? bytes : throw new InvalidDataException("Media exceeds the size limit"); }
        catch (FormatException exception) { throw new ArgumentException("Invalid base64 media", exception); }
    }

    private async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException("Media exceeds the size limit");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}
