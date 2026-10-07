using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.SDK.Models;
using ShiroBot.Model.QQ;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class GenericMessageConversionTests
{
    [TestMethod]
    public void CommonMediaAndMarkdownPreserveContentAndMetadata()
    {
        MessageSegment[] content = [new TextSegment("hello"), new ImageSegment("https://example.com/image") { Width = 20, Height = 30, Summary = "alt", ResourceId = "image-resource" }, new AudioSegment("audio") { Duration = TimeSpan.FromSeconds(3), Transcript = "transcript" }, new VideoSegment("video") { ThumbnailUri = "cover" }, new MarkdownSegment("**markdown**") { PlainTextFallback = "markdown" }];
        var wire = MessageSegments.FromGeneric(content, "qq");
        var restored = MessageSegments.ToGeneric(wire);
        Assert.AreEqual("hello", ((TextSegment)restored[0]).Text);
        Assert.AreEqual(20, ((ImageSegment)restored[1]).Width);
        Assert.AreEqual("alt", ((ImageSegment)restored[1]).Summary);
        Assert.AreEqual("image-resource", ((ImageSegment)restored[1]).ResourceId);
        Assert.AreEqual(TimeSpan.FromSeconds(3), ((AudioSegment)restored[2]).Duration);
        Assert.AreEqual("cover", ((VideoSegment)restored[3]).ThumbnailUri);
        Assert.AreEqual("**markdown**", ((MarkdownSegment)restored[4]).Content);
        Assert.AreEqual("markdown", ((MarkdownSegment)restored[4]).PlainTextFallback);
    }
    [TestMethod]
    public void CardsPreserveVisibleContentAndRawOneBotSegmentsRoundTrip()
    {
        var card = new CardSegment { Title = "title", Description = "body", Url = "https://example.com/", ImageUrl = "https://example.com/image", Fields = [new("field", "value")] };
        var wire = MessageSegments.FromGeneric([card], "custom").Single();
        Assert.AreEqual("text", wire.Type);
        StringAssert.Contains((string)wire.Data["text"]!, "field: value");
        StringAssert.Contains((string)wire.Data["text"]!, "https://example.com/image");
        var xml = new OneBotSegment("xml", new Dictionary<string, object?> { ["data"] = "<xml/>" });
        var restored = MessageSegments.FromGeneric(MessageSegments.ToGeneric([xml])).Single();
        Assert.AreEqual("<xml/>", restored.Data["data"]);
        Assert.IsFalse(restored.Data.ContainsKey("payload"));
        Assert.IsInstanceOfType<QOutgoingLightApp>(((RawSegment)MessageSegments.ToGeneric([new OneBotSegment("json", new Dictionary<string, object?> { ["data"] = "{}" })]).Single()).Payload);
    }
    [TestMethod]
    public void ForeignEmojiAndUnknownSegmentsAreNotPresentedAsQqTypesOrDebugText()
    {
        Assert.ThrowsException<NotSupportedException>(() => MessageSegments.FromGeneric([new EmojiSegment("123")], "discord"));
        Assert.AreEqual("👍", MessageSegments.FromGeneric([new EmojiSegment("123") { Name = "👍" }], "discord").Single().Data["text"]);
        Assert.ThrowsException<NotSupportedException>(() => MessageSegments.FromGeneric([new UnknownSegment()]));
    }
    [TestMethod]
    public async Task CommonReactionWithoutQqRawPayloadRegistersReferenceAndKeepsUnknownValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "onebot-reaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var registry = await MessageIdRegistry.OpenAsync(Path.Combine(directory, "ids.json"));
            var reaction = new MessageReactionEvent { Platform = "custom", InstanceId = "one", SelfId = "1", Channel = Channel.Group("2"), Kind = "reaction", MessageId = "3", Emoji = new UnicodeReactionEmoji("👍"), IsAdded = true };
            var mapped = await OneBotEventMapper.MapAsync(reaction, new OneBotEventFormatConfig(), registry);
            Assert.AreEqual("group_msg_emoji_like", mapped.NoticeType);
            Assert.IsNull(mapped.Data["user_id"]);
            var likes = (Dictionary<string, object?>[])mapped.Data["likes"]!;
            Assert.IsFalse(likes.Single().ContainsKey("count"));
            Assert.AreEqual("👍", likes.Single()["code"]);
            Assert.AreEqual("emoji", mapped.Data["reaction_type"]);
            Assert.IsTrue(mapped.Data["message_id"] is int);
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed record UnknownSegment : MessageSegment;
}
