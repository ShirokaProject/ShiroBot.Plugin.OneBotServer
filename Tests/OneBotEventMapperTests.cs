using System.Text.Json;
using ShiroBot.Model.QQ;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Tests;

[TestClass]
public sealed class OneBotEventMapperTests
{
    private static readonly OneBotEventFormatConfig Format = new() { UseArrayMessage = true };

    [TestMethod]
    public async Task MapAsync_MessageUsesProtocolTimeNumericSelfIdMessageSentAndRegisteredId()
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_001);
        var raw = new QFriendMessage
        {
            PeerId = 20002,
            MessageSeq = 30003,
            SenderId = 10001,
            Time = timestamp,
            Friend = new QFriend { UserId = 20002, Nickname = "friend" }
        };
        var message = new MessageEvent
        {
            Platform = "qq",
            SelfId = "10001",
            Raw = raw,
            MessageId = "30003",
            Channel = Channel.Direct("20002"),
            Sender = new User("10001") { Name = "bot" },
            Segments = [new TextSegment("hello")],
            Timestamp = timestamp
        };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ids.json");
        try
        {
            var registry = await MessageIdRegistry.OpenAsync(path);

            var mapped = await OneBotEventMapper.MapAsync(message, Format, registry);

            Assert.AreEqual(1_700_000_001L, mapped.Time);
            Assert.AreEqual(10001L, mapped.SelfId);
            Assert.AreEqual("message_sent", mapped.PostType);
            Assert.AreEqual(1, mapped.Data["message_id"]);
            Assert.IsTrue(registry.TryResolve(1, out var reference));
            Assert.AreEqual(new MessageReference(MessageScene.Friend, 20002, 30003), reference);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(mapped));
            Assert.AreEqual(JsonValueKind.Number, json.RootElement.GetProperty("self_id").ValueKind);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public void Map_InvalidSelfIdFallsBackToZero()
    {
        var mapped = OneBotEventMapper.Map(new FriendRequestEvent
        {
            Platform = "test",
            SelfId = "not-a-number",
            UserId = "42"
        }, Format);

        Assert.AreEqual(0L, mapped.SelfId);
    }

    [TestMethod]
    public void Map_GroupFilePrefersRawQqFileIdAndEmitsFileAlias()
    {
        const string fileId = "/2052811f-933c-4e61-8597-781769c47a0a";
        var raw = new QGroupMessage
        {
            PeerId = 915449089,
            MessageSeq = 123,
            SenderId = 1034028486,
            Group = new QGroup { GroupId = 915449089, GroupName = "test" },
            GroupMember = new QGroupMember { GroupId = 915449089, UserId = 1034028486, Nickname = "user" },
            Segments = [new QIncomingFile(fileId, "283622490.json", 929603)]
        };
        var message = new MessageEvent
        {
            Platform = "qq",
            SelfId = "3900952625",
            Raw = raw,
            MessageId = "123",
            Channel = Channel.Group("915449089"),
            Sender = new User("1034028486"),
            Segments = [new FileSegment(string.Empty) { FileName = "283622490.json", FileSize = 929603 }]
        };

        var mapped = OneBotEventMapper.Map(message, Format);
        Assert.IsInstanceOfType(mapped.Data["message"], typeof(OneBotSegment[]));
        var segment = ((OneBotSegment[])mapped.Data["message"]!).Single();

        Assert.AreEqual("file", segment.Type);
        Assert.AreEqual(fileId, segment.Data["id"]);
        Assert.AreEqual(fileId, segment.Data["file"]);
        Assert.AreEqual("283622490.json", segment.Data["name"]);
        Assert.AreEqual(929603L, segment.Data["size"]);
    }

    [TestMethod]
    public void Map_GenericFileUsesResourceIdWhenUriIsEmpty()
    {
        const string fileId = "/file-id";
        var mapped = OneBotEventMapper.Map(new MessageEvent
        {
            Platform = "test",
            SelfId = "1",
            MessageId = "2",
            Channel = Channel.Group("3"),
            Sender = new User("4"),
            Segments = [new FileSegment(string.Empty) { ResourceId = fileId, FileName = "file.json", FileSize = 42 }]
        }, Format);
        Assert.IsInstanceOfType(mapped.Data["message"], typeof(OneBotSegment[]));
        var segment = ((OneBotSegment[])mapped.Data["message"]!).Single();

        Assert.AreEqual(fileId, segment.Data["id"]);
        Assert.AreEqual(fileId, segment.Data["file"]);
    }

    [TestMethod]
    public void Map_InvalidPlatformSelfIdDoesNotFallBackToRawPayload()
    {
        var source = Platform(new QGroupDisband { SelfId = 10001, GroupId = 10, OperatorId = 20 }) with { SelfId = "invalid" };

        var mapped = OneBotEventMapper.Map(source, Format);

        Assert.AreEqual(0L, mapped.SelfId);
    }

    [TestMethod]
    public async Task MapAsync_QqNoticesUsePayloadTimeAndRegisterEveryMessageReference()
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_002);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ids.json");
        try
        {
            var registry = await MessageIdRegistry.OpenAsync(path);
            var recall = await OneBotEventMapper.MapAsync(Platform(new QMessageRecall
            {
                Time = time, SelfId = 10001, Scene = QMessageScene.Group, PeerId = 10, MessageSeq = 20, SenderId = 30, OperatorId = 40
            }), Format, registry);
            var reaction = await OneBotEventMapper.MapAsync(Platform(new QGroupMessageReaction
            {
                Time = time, SelfId = 10001, GroupId = 11, UserId = 31, MessageSeq = 21, FaceId = "128", IsAdd = false
            }), Format, registry);
            var essence = await OneBotEventMapper.MapAsync(Platform(new QGroupEssenceMessageChange
            {
                Time = time, SelfId = 10001, GroupId = 12, MessageSeq = 22, OperatorId = 42, IsSet = true
            }), Format, registry);

            Assert.AreEqual(1_700_000_002L, recall.Time);
            Assert.AreEqual(1, recall.Data["message_id"]);
            Assert.AreEqual(2, reaction.Data["message_id"]);
            Assert.AreEqual(3, essence.Data["message_id"]);
            Assert.AreEqual(false, reaction.Data["is_add"]);
            var likes = (Dictionary<string, object?>[])reaction.Data["likes"]!;
            Assert.AreEqual("128", likes.Single()["code"]);
            Assert.AreEqual(3, registry.Count);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public void Map_ProtocolSpecificNoticesHaveExpectedShapes()
    {
        var invited = OneBotEventMapper.Map(Platform(new QGroupInvitedJoinRequest
        {
            SelfId = 10001, GroupId = 10, NotificationSeq = 20, InitiatorId = 30, TargetUserId = 40
        }), Format);
        var dismissed = OneBotEventMapper.Map(Platform(new QGroupDisband { SelfId = 10001, GroupId = 10, OperatorId = 30 }), Format);
        var wholeMute = OneBotEventMapper.Map(Platform(new QGroupWholeMute { SelfId = 10001, GroupId = 10, OperatorId = 30, IsMute = true }), Format);
        var nudge = OneBotEventMapper.Map(Platform(new QGroupNudge { SelfId = 10001, GroupId = 10, SenderId = 30, ReceiverId = 40 }), Format);
        var kicked = OneBotEventMapper.Map(Platform(new QGroupMemberDecrease { SelfId = 10001, GroupId = 10, UserId = 10001, OperatorId = 30 }), Format);

        Assert.AreEqual("add", invited.SubType);
        Assert.AreEqual("group_dismiss", dismissed.NoticeType);
        Assert.AreEqual(30L, dismissed.Data["operator_id"]);
        Assert.AreEqual(0L, wholeMute.Data["user_id"]);
        Assert.AreEqual(30L, wholeMute.Data["operator_id"]);
        Assert.AreEqual(0L, wholeMute.Data["duration"]);
        Assert.AreEqual(40L, nudge.Data["target_id"]);
        Assert.AreEqual("kick_me", kicked.SubType);
    }

    [TestMethod]
    public void Map_MessageDeletedDistinguishesPrivateAndGroupRecall()
    {
        var privateRecall = OneBotEventMapper.Map(new MessageDeletedEvent
        {
            Platform = "qq", SelfId = "10001", MessageId = "1", Channel = Channel.Direct("20"), SenderId = "20"
        }, Format);
        var groupRecall = OneBotEventMapper.Map(new MessageDeletedEvent
        {
            Platform = "qq", SelfId = "10001", MessageId = "2", Channel = Channel.Group("30"), SenderId = "20"
        }, Format);

        Assert.AreEqual("friend_recall", privateRecall.NoticeType);
        Assert.IsFalse(privateRecall.Data.ContainsKey("group_id"));
        Assert.AreEqual("group_recall", groupRecall.NoticeType);
        Assert.AreEqual("30", groupRecall.Data["group_id"]);
    }

    [TestMethod]
    public async Task MapAsync_GenericMessageAndRecallRegisterNumericReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ids.json");
        try
        {
            var registry = await MessageIdRegistry.OpenAsync(path);
            var message = new MessageEvent
            {
                Platform = "qq", SelfId = "1", MessageId = "20", Channel = Channel.Group("10"),
                Sender = new User("2"), Segments = [], Timestamp = DateTimeOffset.FromUnixTimeSeconds(1)
            };
            var recall = new MessageDeletedEvent
            {
                Platform = "qq", SelfId = "1", MessageId = "21", Channel = Channel.Direct("2")
            };

            var mappedMessage = await OneBotEventMapper.MapAsync(message, Format, registry);
            var mappedRecall = await OneBotEventMapper.MapAsync(recall, Format, registry);

            Assert.AreEqual(1, mappedMessage.Data["message_id"]);
            Assert.AreEqual(2, mappedRecall.Data["message_id"]);
            Assert.IsTrue(registry.TryResolve(2, out var reference));
            Assert.AreEqual(new MessageReference(MessageScene.Friend, 2, 21), reference);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private static PlatformEvent Platform(QEventPayload payload) => new()
    {
        Platform = "qq",
        SelfId = payload.SelfId.ToString(),
        Kind = payload.GetType().Name,
        Raw = payload
    };
}
