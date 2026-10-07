using ShiroBot.Plugin.OneBotServer.Infrastructure;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class InfrastructureTests
{
    private static readonly byte[] SigningKey = System.Text.Encoding.UTF8.GetBytes("0123456789abcdef-test-key");

    [TestMethod]
    public async Task EventQueue_TracksIndependentConsumerCursors()
    {
        var queue = new EventQueue<string>();
        await queue.FetchAsync("one", TimeSpan.Zero);
        await queue.FetchAsync("two", TimeSpan.Zero);
        queue.Push("event");
        CollectionAssert.AreEqual(new[] { "event" }, (await queue.FetchAsync("one", TimeSpan.Zero)).ToArray());
        Assert.HasCount(0, await queue.FetchAsync("one", TimeSpan.Zero));
        CollectionAssert.AreEqual(new[] { "event" }, (await queue.FetchAsync("two", TimeSpan.Zero)).ToArray());
    }

    [TestMethod]
    public void RequestFlagCodec_RoundTripsAndRejectsTampering()
    {
        var encoded = RequestFlagCodec.Encode(new RequestFlag("group", 2, 3, Filtered: true, RequestType: "join_request"), SigningKey);
        var decoded = RequestFlagCodec.Decode(encoded, SigningKey);
        Assert.AreEqual(2L, decoded.GroupId);
        Assert.ThrowsExactly<ArgumentException>(() => RequestFlagCodec.Decode(encoded[..^1] + "x", SigningKey));
        Assert.ThrowsExactly<ArgumentException>(() => RequestFlagCodec.Decode(encoded, System.Text.Encoding.UTF8.GetBytes("different-key-123")));
    }

    [TestMethod]
    public void RequestFlagCodec_RoundTripsEveryRequestKindWithDerivedStableKey()
    {
        var key = OneBotRuntimeState.DeriveRequestFlagKey(null, "access-token");
        var flags = new[]
        {
            new RequestFlag("friend", InitiatorUid: "uid"),
            new RequestFlag("group", 10, 20, Filtered: true, RequestType: "join_request"),
            new RequestFlag("group", 10, Filtered: true, RequestType: "join_request", EncodedRequest: "eyJSZXF1ZXN0SWQiOiJvcGFxdWUifQ=="),
            new RequestFlag("group", 10, 21, RequestType: "invited_join_request"),
            new RequestFlag("invitation", 10, 22),
            new RequestFlag("invitation", 10, NativeToken: "generic-token"),
        };

        foreach (var expected in flags)
            Assert.AreEqual(expected, RequestFlagCodec.Decode(RequestFlagCodec.Encode(expected, key), key));
        CollectionAssert.AreEqual(key, OneBotRuntimeState.DeriveRequestFlagKey(null, "access-token"));
        CollectionAssert.AreNotEqual(key, OneBotRuntimeState.DeriveRequestFlagKey("configured-secret", "access-token"));
    }

    [TestMethod]
    public async Task EventQueue_LongPollTimeoutReturnsEmptyWithoutAdvancingOtherCursor()
    {
        var queue = new EventQueue<string>();
        await queue.FetchAsync("waiting", TimeSpan.Zero);
        await queue.FetchAsync("other", TimeSpan.Zero);
        Assert.HasCount(0, await queue.FetchAsync("waiting", TimeSpan.FromMilliseconds(5)));
        queue.Push("later");
        CollectionAssert.AreEqual(new[] { "later" }, (await queue.FetchAsync("other", TimeSpan.Zero)).ToArray());
    }

    [TestMethod]
    public void Registries_AreBoundedAndKeepLatestValues()
    {
        var files = new PrivateFileRegistry(1);
        files.Remember("one", new(1, "a", false));
        files.Remember("two", new(2, "b", true));
        Assert.IsFalse(files.TryResolve("one", out _));
        Assert.IsTrue(files.TryResolve("two", out var file));
        Assert.AreEqual(2L, file!.UserId);
        var reactions = new ReactionRegistry(2);
        reactions.Update(1, 2, "smile", 10, true);
        reactions.Update(1, 2, "smile", 11, true);
        Assert.AreEqual(2, reactions.Update(1, 2, "smile", 10, true));
        Assert.AreEqual(1, reactions.Update(1, 2, "smile", 10, false));
    }
}
