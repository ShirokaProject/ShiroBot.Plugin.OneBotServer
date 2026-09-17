using System.Globalization;
using ShiroBot.Model.QQ;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Events;

public static class OneBotEventMapper
{
    public static async Task<OneBotEvent> MapAsync(
        BotEvent evt,
        OneBotEventFormatConfig format,
        MessageIdRegistry messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var mapped = Map(evt, format);
        var reference = MessageReferenceFor(evt);
        if (reference is not null)
            mapped.Data["message_id"] = await messageIds.RegisterAsync(reference, cancellationToken).ConfigureAwait(false);
        return mapped;
    }

    public static OneBotEvent Map(BotEvent evt, OneBotEventFormatConfig format)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(format);

        return evt switch
        {
            MessageEvent message => MapMessage(message, format),
            MessageDeletedEvent deleted => MapDeleted(deleted),
            MemberJoinedEvent joined => MapJoined(joined),
            MemberLeftEvent left => MapLeft(left),
            FriendRequestEvent request => Request(evt, "friend", new Dictionary<string, object?>
            {
                ["user_id"] = request.UserId,
                ["comment"] = request.Comment,
                ["flag"] = request.Token
            }),
            GuildInviteEvent invite => Request(evt, "group", new Dictionary<string, object?>
            {
                ["group_id"] = invite.GuildId,
                ["user_id"] = invite.InviterId,
                ["comment"] = null,
                ["flag"] = invite.Token,
                ["sub_type"] = "invite"
            }),
            BotOfflineEvent offline => Meta(evt, "lifecycle", new Dictionary<string, object?> { ["sub_type"] = "disable", ["reason"] = offline.Reason }),
            PlatformEvent platform => MapPlatform(platform, format),
            _ => Notice(evt, "shirobot_event", new Dictionary<string, object?> { ["event_type"] = evt.GetType().Name })
        };
    }

    private static OneBotEvent MapMessage(MessageEvent message, OneBotEventFormatConfig format)
    {
        var segments = message.Raw is QIncomingMessage { Segments.Count: > 0 } qqMessage
            ? MessageSegments.FromQq(qqMessage.Segments).ToArray()
            : message.Segments.Select(MapSegment).ToArray();
        var data = new Dictionary<string, object?>
        {
            ["message_id"] = message.MessageId,
            ["user_id"] = message.Sender.Id,
            ["message"] = format.UseArrayMessage ? segments : CqCode.Serialize(segments),
            ["sender"] = new Dictionary<string, object?> { ["user_id"] = message.Sender.Id, ["nickname"] = message.Sender.Name },
            ["raw_message"] = format.IncludeRawMessage ? CqCode.Serialize(segments) : null
        };

        if (message.IsDirect)
        {
            return Event(message, "message", data, messageType: "private", subType: "friend");
        }

        data["group_id"] = message.Channel.Id;
        data["sender"] = new Dictionary<string, object?>
        {
            ["user_id"] = message.Sender.Id,
            ["nickname"] = message.Sender.Name,
            ["card"] = message.Member?.Nick,
            ["role"] = message.Member?.Role.ToString().ToLowerInvariant()
        };
        return Event(message, "message", data, messageType: "group", subType: "normal");
    }

    private static OneBotEvent MapPlatform(PlatformEvent platform, OneBotEventFormatConfig format)
    {
        var data = new Dictionary<string, object?> { ["shirobot_kind"] = platform.Kind };
        if (platform.Channel is { } channel) data["group_id"] = channel.Id;

        if (platform.Raw is QEventPayload payload)
        {
            if (format.IncludeRawPayload) data["raw_payload"] = payload;
        }

        return platform.Raw switch
        {
            QFriendNudge nudge => Notice(platform, "notify", Add(Add(data, "user_id", nudge.UserId), "target_id", nudge.IsSelfReceive ? nudge.SelfId : nudge.UserId), "poke"),
            QFriendFileUpload file => Notice(platform, "friend_upload", Add(data, "user_id", file.UserId)) with { Data = Add(Add(data, "user_id", file.UserId), "file", File(file.FileId, file.FileName, file.FileSize, file.FileHash)) },
            QGroupNudge nudge => Notice(platform, "notify", data, "poke") with { Data = Add(Add(data, "user_id", nudge.SenderId), "target_id", nudge.ReceiverId) },
            QGroupAdminChange change => Notice(platform, "group_admin", data, change.IsSet ? "set" : "unset") with { Data = Add(Add(data, "group_id", change.GroupId), "user_id", change.UserId) },
            QGroupEssenceMessageChange change => Notice(platform, "essence", Add(Add(Add(data, "group_id", change.GroupId), "message_id", change.MessageSeq), "operator_id", change.OperatorId), change.IsSet ? "add" : "delete"),
            QGroupNameChange change => Notice(platform, "group_name", Add(Add(Add(data, "group_id", change.GroupId), "operator_id", change.OperatorId), "group_name", change.NewGroupName)),
            QGroupMessageReaction reaction => Notice(platform, "group_msg_emoji_like", Add(Add(Add(Add(Add(data, "group_id", reaction.GroupId), "user_id", reaction.UserId), "message_id", reaction.MessageSeq), "likes", new[] { new Dictionary<string, object?> { ["code"] = reaction.FaceId, ["count"] = 1 } }), "is_add", reaction.IsAdd)),
            QGroupMute mute => Notice(platform, "group_ban", data, mute.IsUnmute ? "lift_ban" : "ban") with { Data = Add(Add(Add(Add(data, "group_id", mute.GroupId), "user_id", mute.UserId), "operator_id", mute.OperatorId), "duration", (long)mute.Duration.TotalSeconds) },
            QGroupWholeMute mute => Notice(platform, "group_ban", data, mute.IsMute ? "ban" : "lift_ban") with { Data = Add(Add(Add(Add(data, "group_id", mute.GroupId), "user_id", 0L), "operator_id", mute.OperatorId), "duration", 0L) },
            QGroupFileUpload file => Notice(platform, "group_upload", data) with { Data = Add(Add(Add(data, "group_id", file.GroupId), "user_id", file.UserId), "file", new Dictionary<string, object?> { ["id"] = file.FileId, ["fid"] = file.FileId, ["name"] = file.FileName, ["size"] = file.FileSize, ["busid"] = 0 }) },
            QGroupJoinRequest request => Request(platform, "group", Add(Add(Add(Add(data, "group_id", request.GroupId), "user_id", request.InitiatorId), "comment", request.Comment), "flag", RequestFlagCodec.Encode(new RequestFlag("group", request.GroupId, request.NotificationSeq, Filtered: request.IsFiltered, RequestType: "join_request")))) with { SubType = "add" },
            QGroupInvitedJoinRequest request => Request(platform, "group", Add(Add(Add(data, "group_id", request.GroupId), "user_id", request.InitiatorId), "flag", RequestFlagCodec.Encode(new RequestFlag("group", request.GroupId, request.NotificationSeq, RequestType: "invited_join_request")))) with { SubType = "add" },
            QGroupDisband disband => Notice(platform, "group_dismiss", Add(Add(data, "group_id", disband.GroupId), "operator_id", disband.OperatorId)),
            QPeerPinChange pin => Notice(platform, "peer_pin", Add(Add(Add(data, "peer_id", pin.PeerId), "peer_type", pin.Scene.ToString().ToLowerInvariant()), "is_pinned", pin.IsPinned)),
            QMessageRecall recall when recall.Scene == QMessageScene.Group => Notice(platform, "group_recall", Add(Add(Add(Add(data, "group_id", recall.PeerId), "message_id", recall.MessageSeq), "user_id", recall.SenderId), "operator_id", recall.OperatorId)),
            QMessageRecall recall => Notice(platform, "friend_recall", Add(Add(data, "message_id", recall.MessageSeq), "user_id", recall.SenderId)),
            QGroupMemberIncrease member => Notice(platform, "group_increase", Add(Add(Add(data, "group_id", member.GroupId), "user_id", member.UserId), "operator_id", member.OperatorId), member.InvitorId is null ? "approve" : "invite"),
            QGroupMemberDecrease member => Notice(platform, "group_decrease", Add(Add(Add(data, "group_id", member.GroupId), "user_id", member.UserId), "operator_id", member.OperatorId), member.UserId == member.SelfId ? "kick_me" : member.OperatorId is null ? "leave" : "kick"),
            QFriendRequestReceived request => Request(platform, "friend", Add(Add(Add(data, "user_id", request.InitiatorId), "comment", request.Comment), "flag", RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: request.InitiatorUid)))),
            QGroupInvitation invitation => Request(platform, "group", Add(Add(Add(data, "group_id", invitation.GroupId), "user_id", invitation.InitiatorId), "flag", RequestFlagCodec.Encode(new RequestFlag("invitation", invitation.GroupId, invitation.InvitationSeq))), "invite"),
            QBotOffline offline => Meta(platform, "lifecycle", Add(data, "reason", offline.Reason)) with { SubType = "disable" },
            _ => Notice(platform, "shirobot_notice", data, platform.Kind)
        };
    }

    private static OneBotSegment MapSegment(MessageSegment segment) => segment switch
    {
        TextSegment text => OneBotSegment.Text(text.Text),
        MentionSegment mention => new OneBotSegment("at", new Dictionary<string, object?> { ["qq"] = mention.UserId }),
        MentionAllSegment => new OneBotSegment("at", new Dictionary<string, object?> { ["qq"] = "all" }),
        QuoteSegment quote => new OneBotSegment("reply", new Dictionary<string, object?> { ["id"] = quote.MessageId }),
        EmojiSegment emoji => new OneBotSegment("face", new Dictionary<string, object?> { ["id"] = emoji.Id, ["name"] = emoji.Name }),
        ImageSegment image => new OneBotSegment("image", new Dictionary<string, object?> { ["file"] = image.Uri }),
        AudioSegment audio => new OneBotSegment("record", new Dictionary<string, object?> { ["file"] = audio.Uri }),
        VideoSegment video => new OneBotSegment("video", new Dictionary<string, object?> { ["file"] = video.Uri }),
        FileSegment file => FileSegment(file),
        RawSegment raw => new OneBotSegment(raw.Kind, new Dictionary<string, object?> { ["payload"] = raw.Payload }),
        _ => OneBotSegment.Text(segment.ToString() ?? string.Empty)
    };

    private static OneBotSegment FileSegment(FileSegment file)
    {
        var id = string.IsNullOrWhiteSpace(file.ResourceId) ? file.Uri : file.ResourceId;
        var name = file.FileName ?? file.Uri;
        return new OneBotSegment("file", new Dictionary<string, object?>
        {
            ["file"] = name,
            ["file_id"] = id,
            ["fid"] = id,
            ["file_size"] = file.FileSize?.ToString(CultureInfo.InvariantCulture),
            ["name"] = file.FileName
        });
    }

    private static OneBotEvent Notice(BotEvent evt, string noticeType, IDictionary<string, object?> data, string? subType = null) => Event(evt, "notice", data, noticeType: noticeType, subType: subType);
    private static OneBotEvent Request(BotEvent evt, string requestType, IDictionary<string, object?> data, string? subType = null) => Event(evt, "request", data, requestType: requestType, subType: subType);
    private static OneBotEvent Meta(BotEvent evt, string metaType, IDictionary<string, object?> data) => Event(evt, "meta_event", data, metaEventType: metaType);
    private static OneBotEvent Event(BotEvent evt, string postType, IDictionary<string, object?> data, string? messageType = null, string? noticeType = null, string? requestType = null, string? metaEventType = null, string? subType = null) => new()
    {
        Time = evt switch
        {
            MessageEvent timestampedMessage => timestampedMessage.Timestamp.ToUnixTimeSeconds(),
            PlatformEvent { Raw: QEventPayload { Time: var time } } => time.ToUnixTimeSeconds(),
            _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        },
        SelfId = ParseId(evt.SelfId ?? (evt is PlatformEvent { Raw: QEventPayload payload } ? payload.SelfId.ToString() : null)),
        PostType = evt is MessageEvent message && IsSelfSender(message) ? "message_sent" : postType,
        MessageType = messageType,
        NoticeType = noticeType,
        RequestType = requestType,
        MetaEventType = metaEventType,
        SubType = subType,
        Data = data
    };

    private static MessageReference? MessageReferenceFor(BotEvent evt) => evt switch
    {
        MessageEvent { Raw: QIncomingMessage message } => new MessageReference(ToScene(message.Scene), message.PeerId, message.MessageSeq),
        MessageEvent message => Reference(message.Channel, message.MessageId),
        MessageDeletedEvent deleted => Reference(deleted.Channel, deleted.MessageId),
        PlatformEvent { Raw: QMessageRecall recall } => new MessageReference(ToScene(recall.Scene), recall.PeerId, recall.MessageSeq),
        PlatformEvent { Raw: QGroupMessageReaction reaction } => new MessageReference(MessageScene.Group, reaction.GroupId, reaction.MessageSeq),
        PlatformEvent { Raw: QGroupEssenceMessageChange essence } => new MessageReference(MessageScene.Group, essence.GroupId, essence.MessageSeq),
        _ => null
    };

    private static MessageScene ToScene(QMessageScene scene) => scene switch
    {
        QMessageScene.Friend => MessageScene.Friend,
        QMessageScene.Group => MessageScene.Group,
        QMessageScene.Temp => MessageScene.Temp,
        _ => throw new ArgumentOutOfRangeException(nameof(scene))
    };

    private static long ParseId(string? value) => long.TryParse(value, out var id) ? id : 0;

    private static bool IsSelfSender(MessageEvent message) =>
        long.TryParse(message.Sender.Id, out var senderId) &&
        long.TryParse(message.SelfId, out var selfId) &&
        senderId == selfId;

    private static MessageReference? Reference(Channel channel, string sequence) =>
        long.TryParse(channel.Id, out var peerId) && long.TryParse(sequence, out var messageSequence)
            ? new MessageReference(channel.Type == ChannelType.Group ? MessageScene.Group : channel.Type == ChannelType.Direct ? MessageScene.Friend : MessageScene.Temp, peerId, messageSequence)
            : null;

    private static OneBotEvent MapJoined(MemberJoinedEvent joined) => Notice(joined, "group_increase", new Dictionary<string, object?>
    {
        ["group_id"] = joined.Channel.Id,
        ["user_id"] = joined.UserId,
        ["operator_id"] = joined.OperatorId
    }, joined.OperatorId is null ? "approve" : "invite");

    private static OneBotEvent MapLeft(MemberLeftEvent left) => Notice(left, "group_decrease", new Dictionary<string, object?>
    {
        ["group_id"] = left.Channel.Id,
        ["user_id"] = left.UserId,
        ["operator_id"] = left.OperatorId
    }, left.UserId == left.SelfId ? "kick_me" : left.OperatorId is null ? "leave" : "kick");

    private static OneBotEvent MapDeleted(MessageDeletedEvent deleted)
    {
        var data = new Dictionary<string, object?>
        {
            ["message_id"] = deleted.MessageId,
            ["user_id"] = deleted.SenderId,
            ["operator_id"] = deleted.OperatorId
        };
        if (deleted.Channel.Type != ChannelType.Direct) data["group_id"] = deleted.Channel.Id;
        return Notice(deleted, deleted.Channel.Type == ChannelType.Direct ? "friend_recall" : "group_recall", data);
    }

    private static Dictionary<string, object?> Add(IDictionary<string, object?> source, string name, object? value)
    {
        var result = new Dictionary<string, object?>(source) { [name] = value };
        return result;
    }

    private static Dictionary<string, object?> File(string id, string name, long size, string? hash) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["size"] = size,
        ["hash"] = hash
    };
}
