using ShiroBot.Model.QQ;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class CqAndSegmentTests
{
    [TestMethod]
    public void CqCode_ParsesAndSerializesContextSensitiveEscapes()
    {
        var parsed = CqCode.Parse("a&amp;b&#91;c&#93;,d[CQ:at,qq=1&#44;2]z");
        Assert.HasCount(3, parsed);
        Assert.AreEqual("a&b[c],d", parsed[0].Data["text"]);
        Assert.AreEqual("1,2", parsed[1].Data["qq"]);
        Assert.AreEqual("a&amp;&#91;b&#93;,c[CQ:image,file=x&amp;&#91;y&#93;&#44;z]", CqCode.Serialize([OneBotSegment.Text("a&[b],c"), new OneBotSegment("image", new Dictionary<string, object?> { ["file"] = "x&[y],z" })]));
    }

    [TestMethod]
    public void CqCode_TreatsMalformedCodeAsText()
    {
        var result = CqCode.Parse("a[CQ:at,qq]b[CQ:face,id=1");
        Assert.HasCount(1, result);
        Assert.AreEqual("a[CQ:at,qq]b[CQ:face,id=1", result[0].Data["text"]);
    }

    [TestMethod]
    public void MessageSegments_MapsQqAndOneBotToNativeAndGenericSdkModels()
    {
        var fromQq = MessageSegments.FromQq([new QIncomingText("hello"), new QIncomingMention("42", "Alice"), new QIncomingImage("image", "https://example.test/image") { Summary = "pic" }]);
        Assert.AreEqual("text", fromQq[0].Type);
        Assert.AreEqual("42", fromQq[1].Data["qq"]);
        var outgoing = MessageSegments.ToQq(CqCode.Parse("hello[CQ:at,qq=42][CQ:image,file=base64://aW1n]"));
        Assert.IsInstanceOfType<QOutgoingText>(outgoing[0]);
        Assert.IsInstanceOfType<QOutgoingMention>(outgoing[1]);
        Assert.IsInstanceOfType<QOutgoingImage>(outgoing[2]);
        var generic = MessageSegments.ToGeneric(fromQq);
        Assert.IsInstanceOfType<TextSegment>(generic[0]);
        Assert.IsInstanceOfType<MentionSegment>(generic[1]);
        Assert.IsInstanceOfType<ImageSegment>(generic[2]);
    }
}
