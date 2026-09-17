using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Plugin.OneBotServer.Transports;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class OneBotTransportIntegrationTests
{
    [TestMethod]
    public async Task HttpApi_EnforcesAuthenticationAndMergesQueryAndJsonParameters()
    {
        var handler = new RecordingActionHandler();
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001", "secret"), handler);
        using var client = CreateClient(server);

        using var missing = await client.GetAsync("send_msg");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var invalid = await client.GetAsync("send_msg?access_token=wrong");
        Assert.AreEqual(HttpStatusCode.Forbidden, invalid.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "send_msg?message=query&user_id=42&access_token=secret")
        {
            Content = new StringContent("{\"message\":\"body\",\"params\":{\"auto_escape\":true}}", Encoding.UTF8, "application/json"),
        };
        using var success = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        Assert.IsNotNull(handler.LastRequest);
        Assert.AreEqual("send_msg", handler.LastRequest.Action);
        Assert.AreEqual("body", handler.LastRequest.Params["message"].GetString());
        Assert.AreEqual("42", handler.LastRequest.Params["user_id"].GetString());
        Assert.IsTrue(handler.LastRequest.Params["auto_escape"].GetBoolean());
        Assert.IsFalse(handler.LastRequest.Params.ContainsKey("access_token"));
    }

    [TestMethod]
    public async Task HttpApi_MapsActionAndPayloadFailuresToCorrectStatusCodes()
    {
        var handler = new RecordingActionHandler
        {
            Response = _ => throw new OneBotActionException(StatusCodes.Status404NotFound, 1404, "unsupported action"),
        };
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001"), handler);
        using var client = CreateClient(server);

        using var notFound = await client.GetAsync("unknown");
        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);

        using var businessFailure = await client.GetAsync("send_msg");
        Assert.AreEqual(HttpStatusCode.OK, businessFailure.StatusCode);
        using var businessJson = JsonDocument.Parse(await businessFailure.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(1404, businessJson.RootElement.GetProperty("retcode").GetInt32());

        using var malformed = await client.PostAsync(
            "send_msg",
            new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var unsupported = await client.PostAsync(
            "send_msg",
            new StringContent("value", Encoding.UTF8, "text/plain"));
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
    }

    [TestMethod]
    public async Task HttpApi_RejectsChunkedBodyWhenActualBytesExceedLimit()
    {
        var handler = new RecordingActionHandler();
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001", MaxRequestBodyBytes: 16), handler);
        using var client = CreateClient(server);
        using var request = new HttpRequestMessage(HttpMethod.Post, "send_msg")
        {
            Content = new ChunkedJsonContent("{\"message\":\"this is too large\"}")
        };

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    public async Task HttpEventPoster_SignsRequestAndDispatchesQuickOperation()
    {
        var http = new RecordingHttpHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"reply\":\"pong\"}", Encoding.UTF8, "application/json"),
            },
        };
        using var client = new HttpClient(http);
        var quickOperations = new RecordingQuickOperationHandler();
        var poster = new OneBotHttpEventPoster(
            client,
            new OneBotHttpEventPostOptions([new Uri("https://example.test/events")], "token", "secret", "10001"),
            quickOperations);

        await poster.PostAsync(new { post_type = "message", message = "hello" });

        Assert.IsNotNull(http.Request);
        Assert.AreEqual("Bearer", http.Request.AuthorizationScheme);
        Assert.AreEqual("token", http.Request.AuthorizationParameter);
        Assert.AreEqual("10001", http.Request.SelfId);
        Assert.AreEqual(
            OneBotTransportProtocol.ComputeHmacSha1("secret", http.Request.Body),
            http.Request.Signature);
        Assert.AreEqual("message", quickOperations.Event.GetProperty("post_type").GetString());
        Assert.AreEqual("pong", quickOperations.Operation.GetProperty("reply").GetString());
    }

    [TestMethod]
    public async Task ForwardWebSocket_ApiRoleExposesHeadersAndPreservesEcho()
    {
        var handler = new RecordingActionHandler();
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001", "secret"), handler);
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer secret");
        var endpoint = new Uri(server.Addresses.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/api");

        await socket.ConnectAsync(endpoint, CancellationToken.None);
        var request = Encoding.UTF8.GetBytes("{\"action\":\"get_status\",\"params\":{},\"echo\":{\"id\":7}}");
        await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        using var response = JsonDocument.Parse(buffer.AsMemory(0, result.Count));

        var responseHeaders = socket.HttpResponseHeaders;
        Assert.IsNotNull(responseHeaders);
        Assert.AreEqual("10001", responseHeaders["X-Self-ID"].Single());
        Assert.AreEqual("API", responseHeaders["X-Client-Role"].Single());
        Assert.AreEqual("get_status", handler.LastRequest?.Action);
        Assert.AreEqual(7, response.RootElement.GetProperty("echo").GetProperty("id").GetInt32());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [TestMethod]
    public async Task ForwardWebSocket_EventAndUniversalRolesImmediatelyReceiveConnectLifecycleAndHeartbeatStatus()
    {
        var handler = new RecordingActionHandler();
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001"), handler);
        foreach (var path in new[] { string.Empty, "/event" })
        {
            using var socket = new ClientWebSocket();
            var endpoint = new Uri(server.Addresses.Single().Replace("http://", "ws://", StringComparison.Ordinal) + path);

            await socket.ConnectAsync(endpoint, CancellationToken.None);
            using var lifecycle = await ReceiveJsonAsync(socket);
            await server.PublishHeartbeatAsync(15000);
            using var heartbeat = await ReceiveJsonAsync(socket);

            Assert.AreEqual("lifecycle", lifecycle.RootElement.GetProperty("meta_event_type").GetString());
            Assert.AreEqual("connect", lifecycle.RootElement.GetProperty("sub_type").GetString());
            Assert.AreEqual(JsonValueKind.Number, lifecycle.RootElement.GetProperty("self_id").ValueKind);
            Assert.IsTrue(heartbeat.RootElement.GetProperty("status").GetProperty("online").GetBoolean());
            Assert.IsTrue(heartbeat.RootElement.GetProperty("status").GetProperty("good").GetBoolean());
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task ForwardWebSocket_ClosesOversizedMessageWithMessageTooBig()
    {
        var handler = new RecordingActionHandler();
        await using var server = await OneBotKestrelServer.StartAsync(
            new OneBotServerOptions(["http://127.0.0.1:0"], "10001", MaxWebSocketMessageBytes: 16), handler);
        using var socket = new ClientWebSocket();
        var endpoint = new Uri(server.Addresses.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/api");

        await socket.ConnectAsync(endpoint, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"action\":\"get_status\",\"params\":{}}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var result = await socket.ReceiveAsync(new byte[128], CancellationToken.None);

        Assert.AreEqual(WebSocketMessageType.Close, result.MessageType);
        Assert.AreEqual(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
        Assert.IsNull(handler.LastRequest);
    }

    [DataTestMethod]
    [DataRow(OneBotWebSocketRole.Event)]
    [DataRow(OneBotWebSocketRole.Universal)]
    public async Task ReverseWebSocket_EventAndUniversalRolesImmediatelySendConnectLifecycle(OneBotWebSocketRole role)
    {
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/reverse", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            received.TrySetResult(document.RootElement.Clone());
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/reverse");
        var client = new OneBotReverseWebSocketClient(
            new OneBotReverseWebSocketOptions(endpoint, "10001", role),
            new RecordingActionHandler());

        await client.RunConnectionAsync(CancellationToken.None);
        var lifecycle = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("lifecycle", lifecycle.GetProperty("meta_event_type").GetString());
        Assert.AreEqual("connect", lifecycle.GetProperty("sub_type").GetString());
        Assert.AreEqual(10001L, lifecycle.GetProperty("self_id").GetInt64());
    }

    private static HttpClient CreateClient(OneBotKestrelServer server)
    {
        var address = server.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/") };
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    private sealed class RecordingActionHandler : IOneBotActionHandler
    {
        public IReadOnlyCollection<string> Actions { get; } = ["send_msg", "get_status"];
        public OneBotActionRequest? LastRequest { get; private set; }
        public Func<OneBotActionRequest, OneBotResponse<object?>> Response { get; init; } =
            request => OneBotResponse<object?>.Ok(new { action = request.Action }, request.Echo);

        public Task<OneBotResponse<object?>> HandleAsync(OneBotActionRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response(request));
        }
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] body;

        public ChunkedJsonContent(string body)
        {
            this.body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(body);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

    }

    private sealed class RecordingQuickOperationHandler : IOneBotQuickOperationHandler
    {
        public JsonElement Event { get; private set; }
        public JsonElement Operation { get; private set; }

        public Task HandleAsync(JsonElement @event, JsonElement operation, CancellationToken cancellationToken)
        {
            Event = @event.Clone();
            Operation = operation.Clone();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public required HttpResponseMessage Response { get; init; }
        public RecordedRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.GetValues("X-Self-ID").Single(),
                request.Headers.GetValues("X-Signature").Single(),
                await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return Response;
        }
    }

    private sealed record RecordedRequest(
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string SelfId,
        string Signature,
        byte[] Body);
}
