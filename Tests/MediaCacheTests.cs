using System.Net;
using System.Net.Http.Headers;
using ShiroBot.Plugin.OneBotServer.Infrastructure;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class MediaCacheTests
{
    [TestMethod]
    public async Task MediaCache_HandlesBoundedBase64HttpResourcesAndSafeLocalPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var local = Path.Combine(directory, "local.bin");
            await File.WriteAllTextAsync(local, "local");
            var http = new HttpClient(new StaticHandler("http"));
            var cache = new MediaCache(Path.Combine(directory, "cache"), 16, directory, http, (id, _) => Task.FromResult("base64://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id))));
            Assert.AreEqual("base64://aW1n", await cache.OutgoingUriAsync("base64://aW1n"));
            Assert.AreEqual("base64://bG9jYWw=", await cache.OutgoingUriAsync(local));
            Assert.AreEqual("base64://cmVz", await cache.OutgoingUriAsync("resource://res"));
            Assert.AreEqual("https://example.test/media", await cache.OutgoingUriAsync("https://example.test/media"));
            var secureCache = new MediaCache(Path.Combine(directory, "secure"));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => secureCache.OutgoingUriAsync(local));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new MediaCache(Path.Combine(directory, "tiny"), 3).OutgoingUriAsync("base64://dG9vLWxvbmc="));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class StaticHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content)) { Headers = { ContentLength = content.Length } } });
    }
}
