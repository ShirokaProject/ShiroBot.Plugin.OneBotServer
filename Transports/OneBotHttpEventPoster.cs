using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.OneBotServer.Transports;

/// <summary>Posts OneBot events to reverse HTTP endpoints and applies quick operations.</summary>
public sealed class OneBotHttpEventPoster
{
    private readonly HttpClient client;
    private readonly OneBotHttpEventPostOptions options;
    private readonly IOneBotQuickOperationHandler? quickOperations;

    public OneBotHttpEventPoster(HttpClient client, OneBotHttpEventPostOptions options, IOneBotQuickOperationHandler? quickOperations = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.quickOperations = quickOperations;
        if (options.Endpoints.Any(endpoint => endpoint.Scheme is not ("http" or "https")))
            throw new ArgumentException("HTTP event endpoints must use http or https", nameof(options));
    }

    public async Task PostAsync<T>(T @event, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(@event);
        var eventJson = JsonSerializer.SerializeToElement(@event);
        await Task.WhenAll(options.Endpoints.Select(endpoint => PostToEndpointAsync(endpoint, body, eventJson, cancellationToken))).ConfigureAwait(false);
    }

    private async Task PostToEndpointAsync(Uri endpoint, byte[] body, JsonElement @event, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (!string.IsNullOrEmpty(options.AccessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
            if (!string.IsNullOrEmpty(options.Secret)) request.Headers.TryAddWithoutValidation("X-Signature", OneBotTransportProtocol.ComputeHmacSha1(options.Secret, body));
            if (!string.IsNullOrEmpty(options.SelfId)) request.Headers.TryAddWithoutValidation("X-Self-ID", options.SelfId);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                BotLog.Warning($"[OneBot/HTTP-POST] 事件投递失败: {SafeEndpoint(endpoint)} 返回 HTTP {(int)response.StatusCode}。");
                return;
            }
            if (quickOperations is null) return;
            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (responseBody.Length == 0) return;

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                await quickOperations.HandleAsync(@event, document.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed target, callback, or successful non-JSON response must not block other event targets.
            BotLog.Error($"[OneBot/HTTP-POST] 事件投递或快速操作失败: {SafeEndpoint(endpoint)}，{exception.GetType().Name}: {exception}");
        }
    }

    private static string SafeEndpoint(Uri endpoint) => $"{endpoint.Scheme}://{endpoint.Authority}{endpoint.AbsolutePath}";
}
