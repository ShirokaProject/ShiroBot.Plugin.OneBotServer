using System.Security.Cryptography;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed class PluginFileStorage
{
    private const long DefaultMaxBytes = 64 * 1024 * 1024;
    private readonly string directory;
    private readonly HttpClient client;
    private readonly long maxBytes;

    public PluginFileStorage(string storageDirectory, HttpClient? client = null, long maxBytes = DefaultMaxBytes)
    {
        directory = Path.GetFullPath(Path.Combine(storageDirectory, "files"));
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        this.maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    public async Task<string> DownloadAsync(string? url, string? base64, string? requestedName, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        byte[] data;
        if (!string.IsNullOrEmpty(base64))
        {
            try { data = Convert.FromBase64String(base64.StartsWith("base64://", StringComparison.Ordinal) ? base64[9..] : base64); }
            catch (FormatException exception) { throw new ArgumentException("base64 must contain valid base64 data", exception); }
            EnsureSize(data.LongLength);
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            data = await DownloadHttpAsync(uri, headers, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("url or base64 is required");
        }

        return await StoreAsync(data, requestedName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string File, long Size, string Name)> StoreFromUrlAsync(string url, string? requestedName, bool download, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("File URL must use HTTP(S)");
        var data = await DownloadHttpAsync(uri, null, cancellationToken).ConfigureAwait(false);
        var name = SafeName(requestedName ?? SourceName(url));
        return (download ? await StoreAsync(data, name, cancellationToken).ConfigureAwait(false) : string.Empty, data.LongLength, name);
    }

    public bool TryGetStoredFile(string fileName, out string path)
    {
        path = string.Empty;
        if (!IsSafeName(fileName)) return false;
        path = Path.Combine(directory, fileName);
        return File.Exists(path);
    }

    private async Task<string> StoreAsync(byte[] data, string? requestedName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var name = SafeName(requestedName ?? Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant());
        var destination = Path.GetFullPath(Path.Combine(directory, name));
        if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("File destination must remain inside plugin storage");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            EnsureSize(output.Length + read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private async Task<byte[]> DownloadHttpAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (headers is not null)
            foreach (var (name, value) in headers)
                if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length) EnsureSize(length);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSize(long size)
    {
        if (size > maxBytes) throw new InvalidDataException("File exceeds the size limit");
    }

    private static string SourceName(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) return Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) is { Length: > 0 } name ? name : "file";
        return Path.GetFileName(source) is { Length: > 0 } localName ? localName : "file";
    }

    private static string SafeName(string name)
    {
        if (!IsSafeName(name))
            throw new ArgumentException("name must be a plain file name");
        return name;
    }

    private static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0;
}
