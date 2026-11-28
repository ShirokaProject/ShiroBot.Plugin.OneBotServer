using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.Plugin.OneBotServer.Transports;
using ShiroBot.Plugin.OneBotServer.Configuration;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class OneBotTransportProtocolTests
{
    [TestMethod]
    public void WebSocketMediaFrame_DecodesFragmentedAttachmentsAndKeepsFilesUntilReleased()
    {
        var original = new byte[24 * 1024 + 1];
        new Random(42).NextBytes(original);
        var base64 = Convert.ToBase64String(original);
        base64 = base64.Insert(base64.Length / 2, @"\r\n\t ").Replace("/", @"\/");
        var message = Encoding.UTF8.GetBytes(
            """{"params":{"message":[{"type":"node","data":{"content":[{"type":"video","data":{"file":"BaSe64:\u002f\/""" +
            base64 +
            "\"" + ""","cover":"base64://AQID"}},{"type":"text","data":{"text":"base64://literal"}}]}}]},"action":"send_group_forward_msg","echo":"base64://echo"}""");

        using var reader = new WebSocketMediaFrameReader();
        for (var offset = 0; offset < message.Length; offset += 3)
            reader.Write(message.AsSpan(offset, Math.Min(3, message.Length - offset)));

        string file;
        string cover;
        using (var frame = reader.Complete())
        {
            var request = OneBotTransportProtocol.ParseWebSocketAction(frame.Root);
            var content = request.Params["message"][0].GetProperty("data").GetProperty("content");
            var videoData = content[0].GetProperty("data");
            file = new Uri(videoData.GetProperty("file").GetString()!).LocalPath;
            cover = new Uri(videoData.GetProperty("cover").GetString()!).LocalPath;
            Assert.AreEqual("base64://literal", content[1].GetProperty("data").GetProperty("text").GetString());
            Assert.AreEqual("base64://echo", request.Echo?.GetString());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(file));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(cover));


            // Action execution can outlive its transport response after a timeout.
            frame.Retain();
            frame.Dispose();
            Assert.IsTrue(File.Exists(file));
            Assert.IsTrue(File.Exists(cover));
        }

        Assert.IsFalse(File.Exists(file));
        Assert.IsFalse(File.Exists(cover));
    }

    [TestMethod]
    public void IsAuthorized_AcceptsBearerAndAccessToken_AndRejectsOtherValues()
    {
        var bearer = new DefaultHttpContext();
        bearer.Request.Headers.Authorization = "Bearer secret";
        Assert.IsTrue(OneBotTransportProtocol.IsAuthorized(bearer.Request, "secret"));

        var query = new DefaultHttpContext();
        query.Request.QueryString = new QueryString("?access_token=secret");
        Assert.IsTrue(OneBotTransportProtocol.IsAuthorized(query.Request, "secret"));

        var invalid = new DefaultHttpContext();
        invalid.Request.Headers.Authorization = "Bearer wrong";
        Assert.IsFalse(OneBotTransportProtocol.IsAuthorized(invalid.Request, "secret"));
    }

    [TestMethod]
    public void GetAuthorizationStatus_DistinguishesMissingAndInvalidCredentials()
    {
        var missing = new DefaultHttpContext();
        var invalid = new DefaultHttpContext();
        invalid.Request.Headers.Authorization = "Bearer wrong";

        Assert.AreEqual(
            OneBotAuthorizationStatus.Missing,
            OneBotTransportProtocol.GetAuthorizationStatus(missing.Request, "secret"));
        Assert.AreEqual(
            OneBotAuthorizationStatus.Invalid,
            OneBotTransportProtocol.GetAuthorizationStatus(invalid.Request, "secret"));
    }

    [TestMethod]
    public async Task ParseHttpAction_MergesQueryAndJsonBody_WithBodyTakingPrecedence()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString("?foo=query&from_query=1&access_token=secret");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"foo\":\"body\",\"params\":{\"nested\":2},\"echo\":42}"));

        var request = await OneBotTransportProtocol.ParseHttpActionAsync(context.Request, "send_msg", CancellationToken.None);

        Assert.AreEqual("send_msg", request.Action);
        Assert.AreEqual("body", request.Params["foo"].GetString());
        Assert.AreEqual("1", request.Params["from_query"].GetString());
        Assert.AreEqual(2, request.Params["nested"].GetInt32());
        Assert.AreEqual(42, request.Echo?.GetInt32());
        Assert.IsFalse(request.Params.ContainsKey("access_token"));
    }

    [TestMethod]
    public async Task ParseHttpAction_MergesFormAndPreservesRepeatedQueryValues()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString("?id=1&id=2&name=query");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("name=form&enabled=true"));

        var request = await OneBotTransportProtocol.ParseHttpActionAsync(context.Request, "send_msg", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "1", "2" }, request.Params["id"].EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.AreEqual("form", request.Params["name"].GetString());
        Assert.AreEqual("true", request.Params["enabled"].GetString());
    }

    [TestMethod]
    public void ParseWebSocketAction_ReadsActionParamsAndEcho()
    {
        using var document = JsonDocument.Parse("{\"action\":\"send_msg\",\"params\":{\"user_id\":123},\"echo\":{\"request\":7}}");

        var request = OneBotTransportProtocol.ParseWebSocketAction(document.RootElement);

        Assert.AreEqual("send_msg", request.Action);
        Assert.AreEqual(123, request.Params["user_id"].GetInt32());
        Assert.AreEqual(7, request.Echo?.GetProperty("request").GetInt32());
    }

    [TestMethod]
    public void ParseWebSocketAction_RejectsInvalidAction()
    {
        using var document = JsonDocument.Parse("{\"action\":\"not valid\"}");

        var exception = Assert.ThrowsException<OneBotActionException>(() => OneBotTransportProtocol.ParseWebSocketAction(document.RootElement));

        Assert.AreEqual(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.AreEqual(1400, exception.RetCode);
    }

    [TestMethod]
    public void ComputeHmacSha1_UsesOneBotSignatureFormat()
    {
        var signature = OneBotTransportProtocol.ComputeHmacSha1("secret", Encoding.UTF8.GetBytes("hello"));

        Assert.AreEqual("sha1=5112055c05f944f85755efc5cd8970e194e9f45b", signature);
    }

    [TestMethod]
    public void ReverseWebSocketConfig_SupportsUniversalAndSplitEndpoints()
    {
        var universal = new OneBotReverseWebSocketConfig { UniversalUrl = "wss://example.test/universal" };
        var split = new OneBotReverseWebSocketConfig
        {
            ApiUrl = "wss://example.test/api",
            EventUrl = "wss://example.test/event"
        };

        Assert.AreEqual("wss://example.test/universal", universal.UniversalUrl);
        Assert.AreEqual("wss://example.test/api", split.ApiUrl);
        Assert.AreEqual("wss://example.test/event", split.EventUrl);
    }
}
