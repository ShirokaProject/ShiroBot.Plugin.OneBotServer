using System.Globalization;
using System.Text.Json;
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
        var reference = OneBotMessageReferenceFor(evt);
        if (reference is not null)
            mapped.Data["message_id"] = await messageIds.RegisterAsync(reference, cancellationToken).ConfigureAwait(false);
        return mapped;
    }

    public static OneBotEvent Map(BotEvent evt, OneBotEventFormatConfig format)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(format);
        ValidateProtocolIds(evt);

        return evt switch
        {
            MessageEvent message => MapMessage(message, format),
            MessageDeletedEvent deleted => MapDeleted(deleted),
            MemberJoinedEvent joined => MapJoined(joined),
            MemberLeftEvent left => MapLeft(left),
            FriendRequestEvent request => Request(evt, "friend", new Dictionary<string, object?>
            {
                ["user_id"] = ProtocolId(request.UserId),
                ["comment"] = request.Comment,
                ["flag"] = request.Token
            }),
            GuildInviteEvent invite => Request(evt, "group", new Dictionary<string, object?>
            {
                ["group_id"] = ProtocolId(invite.GuildId),
                ["user_id"] = ProtocolId(invite.InviterId),
                ["comment"] = null,
                ["flag"] = invite.Token,
                ["sub_type"] = "invite"
            }),
            BotOfflineEvent offline => Meta(evt, "lifecycle", new Dictionary<string, object?> { ["sub_type"] = "disable", ["reason"] = offline.Reason }),
            MessageReactionEvent reaction => MapReaction(reaction),
            InteractionEvent interaction => Notice(interaction, "shirobot_interaction", new Dictionary<string, object?>
            {
                ["interaction_id"] = interaction.InteractionId, ["user_id"] = ProtocolId(interaction.User.Id),
                ["button_id"] = interaction.ButtonId, ["data"] = interaction.Data,
                ["acknowledgement"] = interaction.Acknowledgement.ToString().ToLowerInvariant()
            }),
            PlatformEvent platform => MapPlatform(platform, format),
            _ => Notice(evt, "shirobot_event", new Dictionary<string, object?> { ["event_type"] = evt.GetType().Name })
        };
    }

    private static OneBotEvent MapMessage(MessageEvent message, OneBotEventFormatConfig format)
    {
        var segments = message.Raw is QIncomingMessage { Segments.Count: > 0 } qqMessage
            ? MessageSegments.FromQq(qqMessage.Segments).ToArray()
            : MessageSegments.FromGeneric(message.Segments, message.Platform).ToArray();
        var data = new Dictionary<string, object?>
        {
            ["message_id"] = ProtocolId(message.MessageId),
            ["user_id"] = ProtocolId(message.Sender.Id),
            ["message"] = format.UseArrayMessage ? segments : CqCode.Serialize(segments),
            ["sender"] = new Dictionary<string, object?> { ["user_id"] = ProtocolId(message.Sender.Id), ["nickname"] = message.Sender.Name },
            ["raw_message"] = format.IncludeRawMessage ? CqCode.Serialize(segments) : null
        };

        if (message.IsDirect)
        {
            return Event(message, "message", data, messageType: "private", subType: "friend");
        }

        data["group_id"] = ProtocolId(message.Channel.Id);
        data["sender"] = new Dictionary<string, object?>
        {
            ["user_id"] = ProtocolId(message.Sender.Id),
            ["nickname"] = message.Sender.Name,
            ["card"] = message.Member?.Nick,
            ["role"] = message.Member is { Role: not MemberRole.Unknown } member ? member.Role.ToString().ToLowerInvariant() : null
        };
        return Event(message, "message", data, messageType: "group", subType: "normal");
    }

    private static OneBotEvent MapReaction(MessageReactionEvent reaction)
    {
        var channel = reaction.Channel ?? throw new NotSupportedException("Reaction has no source channel.");
        if (channel.Type != ChannelType.Group) throw new NotSupportedException("OneBot reaction notice requires a group.");
        var code = reaction.Emoji switch
        {
            UnicodeReactionEmoji emoji => emoji.Value,
            PlatformReactionEmoji emoji when reaction.Platform is "qq" or "qq-official" or "milky" && string.Equals(emoji.InstanceId, reaction.InstanceId, StringComparison.OrdinalIgnoreCase) => emoji.Id,
            _ => throw new NotSupportedException("Unknown or foreign reaction emoji mapping.")
        };
        var like = new Dictionary<string, object?> { ["code"] = code };
        if (reaction.Count is { } count) like["count"] = count;
        return Notice(reaction, "group_msg_emoji_like", new Dictionary<string, object?>
        {
            ["group_id"] = ProtocolId(channel.Id), ["message_id"] = ProtocolId(reaction.MessageId),
            ["user_id"] = reaction.User is { } user ? ProtocolId(user.Id) : null,
            ["likes"] = new[] { like }, ["is_add"] = reaction.IsAdded,
            ["reaction_type"] = reaction.Emoji is UnicodeReactionEmoji ? "emoji" : "face"
        });
    }

    private static OneBotEvent MapPlatform(PlatformEvent platform, OneBotEventFormatConfig format)
    {
        var data = new Dictionary<string, object?> { ["shirobot_kind"] = platform.Kind };
        if (platform.Channel is { } channel) data["group_id"] = ProtocolId(channel.Id);

        if (platform.Raw is QEventPayload payload)
        {
            if (format.IncludeRawPayload) data["raw_payload"] = payload;
        }

        return platform.Raw switch
        {
            QFriendNudge nudge => Notice(platform, "notify", Add(Add(data, "user_id", ProtocolId(nudge.UserId)), "target_id", nudge.IsSelfReceive ? ProtocolId(nudge.SelfId ?? nudge.UserId) : ProtocolId(nudge.UserId)), "poke"),
            QFriendFileUpload file => Notice(platform, "offline_file", Add(data, "user_id", ProtocolId(file.UserId))) with { Data = Add(Add(data, "user_id", ProtocolId(file.UserId)), "file", new Dictionary<string, object?> { ["id"] = file.FileId, ["fid"] = file.FileId, ["name"] = file.FileName, ["size"] = file.FileSize }) },
            QGroupNudge nudge => Notice(platform, "notify", data, "poke") with { Data = Add(Add(data, "user_id", ProtocolId(nudge.SenderId)), "target_id", ProtocolId(nudge.ReceiverId)) },
            QGroupAdminChange change => Notice(platform, "group_admin", data, change.IsSet ? "set" : "unset") with { Data = Add(Add(data, "group_id", ProtocolId(change.GroupId)), "user_id", ProtocolId(change.UserId)) },
            QGroupEssenceMessageChange change => Notice(platform, "essence", Add(Add(Add(data, "group_id", ProtocolId(change.GroupId)), "message_id", ProtocolId(change.MessageId)), "operator_id", NumericOrNull(change.OperatorId)), change.IsSet ? "add" : "delete"),
            QGroupNameChange change => Notice(platform, "group_name", Add(Add(Add(data, "group_id", ProtocolId(change.GroupId)), "operator_id", NumericOrNull(change.OperatorId)), "group_name", change.NewGroupName)),
            QGroupMessageReaction reaction => Notice(platform, "group_msg_emoji_like", Add(Add(Add(Add(Add(data, "group_id", ProtocolId(reaction.GroupId)), "user_id", ProtocolId(reaction.UserId)), "message_id", ProtocolId(reaction.MessageId)), "likes", new[] { new Dictionary<string, object?> { ["code"] = reaction.FaceId, ["count"] = 1 } }), "is_add", reaction.IsAdd)),
            QGroupMute mute => Notice(platform, "group_ban", data, mute.IsUnmute ? "lift_ban" : "ban") with { Data = Add(Add(Add(Add(data, "group_id", ProtocolId(mute.GroupId)), "user_id", ProtocolId(mute.UserId)), "operator_id", NumericOrNull(mute.OperatorId)), "duration", (long)mute.Duration.TotalSeconds) },
            QGroupWholeMute mute => Notice(platform, "group_ban", data, mute.IsMute ? "ban" : "lift_ban") with { Data = Add(Add(Add(Add(data, "group_id", ProtocolId(mute.GroupId)), "user_id", 0L), "operator_id", NumericOrNull(mute.OperatorId)), "duration", 0L) },
            QGroupFileUpload file => Notice(platform, "group_upload", data) with { Data = Add(Add(Add(data, "group_id", ProtocolId(file.GroupId)), "user_id", ProtocolId(file.UserId)), "file", new Dictionary<string, object?> { ["id"] = file.FileId, ["fid"] = file.FileId, ["name"] = file.FileName, ["size"] = file.FileSize, ["busid"] = 0 }) },
            QGroupJoinRequest request => Request(platform, "group", Add(Add(Add(Add(data, "group_id", ProtocolId(request.GroupId)), "user_id", ProtocolId(request.UserId)), "comment", request.Comment), "flag", RequestFlagCodec.Encode(new RequestFlag("group", ProtocolId(request.GroupId), Filtered: request.IsFiltered, RequestType: request.IsInvited ? "invited_join_request" : "join_request", EncodedRequest: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request)))))) with { SubType = request.IsInvited ? "invite" : "add" },
            QGroupDisband disband => Notice(platform, "group_dismiss", Add(Add(data, "group_id", ProtocolId(disband.GroupId)), "operator_id", NumericOrNull(disband.OperatorId))),
            QPeerPinChange pin => Notice(platform, "peer_pin", Add(Add(Add(data, "peer_id", ProtocolId(pin.PeerId)), "peer_type", pin.Scene.ToString().ToLowerInvariant()), "is_pinned", pin.IsPinned)),
            QMessageRecall recall when recall.Scene == QMessageScene.Group => Notice(platform, "group_recall", Add(Add(Add(Add(data, "group_id", ProtocolId(recall.PeerId)), "message_id", ProtocolId(recall.MessageId)), "user_id", ProtocolId(recall.SenderId)), "operator_id", ProtocolId(recall.OperatorId))),
            QMessageRecall recall => Notice(platform, "friend_recall", Add(Add(data, "message_id", ProtocolId(recall.MessageId)), "user_id", ProtocolId(recall.SenderId))),
            QGroupMemberIncrease member => Notice(platform, "group_increase", Add(Add(Add(data, "group_id", ProtocolId(member.GroupId)), "user_id", ProtocolId(member.UserId)), "operator_id", NumericOrNull(member.OperatorId)), NumericOrNull(member.InvitorId) is null ? "approve" : "invite"),
            QGroupMemberDecrease member => Notice(platform, "group_decrease", Add(Add(Add(data, "group_id", ProtocolId(member.GroupId)), "user_id", ProtocolId(member.UserId)), "operator_id", NumericOrNull(member.OperatorId)), ProtocolId(member.UserId).ToString(CultureInfo.InvariantCulture) == member.SelfId ? "kick_me" : NumericOrNull(member.OperatorId) is null ? "leave" : "kick"),
            QFriendRequestReceived request => Request(platform, "friend", Add(Add(Add(data, "user_id", ProtocolId(request.InitiatorId)), "comment", request.Comment), "flag", RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: request.InitiatorUid)))),
            QGroupInvitation invitation => Request(platform, "group", Add(Add(Add(data, "group_id", ProtocolId(invitation.GroupId)), "user_id", ProtocolId(invitation.InitiatorId)), "flag", RequestFlagCodec.Encode(new RequestFlag("invitation", ProtocolId(invitation.GroupId), NativeToken: invitation.InvitationId))), "invite"),
            QBotOffline offline => Meta(platform, "lifecycle", Add(data, "reason", offline.Reason)) with { SubType = "disable" },
            _ => Notice(platform, "shirobot_notice", data, platform.Kind)
        };
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
        SelfId = ParseId(evt.SelfId ?? (evt is PlatformEvent { Raw: QEventPayload payload } ? payload.SelfId : null)),
        PostType = evt is MessageEvent message && IsSelfSender(message) ? "message_sent" : postType,
        MessageType = messageType,
        NoticeType = noticeType,
        RequestType = requestType,
        MetaEventType = metaEventType,
        SubType = subType,
        Data = data
    };

    private static OneBotMessageReference? OneBotMessageReferenceFor(BotEvent evt) => evt switch
    {
        MessageEvent { Raw: QIncomingMessage message } => Reference(ToScene(message.Scene), message.PeerId, message.MessageId),
        MessageEvent message => Reference(message.Channel, message.MessageId),
        MessageDeletedEvent deleted => Reference(deleted.Channel, deleted.MessageId),
        MessageReactionEvent { Channel: { } channel } reaction => Reference(channel, reaction.MessageId),
        PlatformEvent { Raw: QMessageRecall recall } => Reference(ToScene(recall.Scene), recall.PeerId, recall.MessageId),
        PlatformEvent { Raw: QGroupMessageReaction reaction } => Reference(MessageScene.Group, reaction.GroupId, reaction.MessageId),
        PlatformEvent { Raw: QGroupEssenceMessageChange essence } => Reference(MessageScene.Group, essence.GroupId, essence.MessageId),
        _ => null
    };

    private static MessageScene ToScene(QMessageScene scene) => scene switch
    {
        QMessageScene.Friend => MessageScene.Friend,
        QMessageScene.Group => MessageScene.Group,
        QMessageScene.Temp => MessageScene.Temp,
        _ => throw new ArgumentOutOfRangeException(nameof(scene))
    };

    private static long ParseId(string? value) => value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id
        : throw new NotSupportedException("OneBot 11 requires a numeric self_id; the active adapter supplied no numeric identifier.");
    private static long ProtocolId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id
        : throw new NotSupportedException("OneBot 11 numeric IDs cannot represent this adapter's opaque identifier.");
    private static object? NumericOrNull(string? value) => value is null ? null : ProtocolId(value);

    private static void ValidateProtocolIds(BotEvent evt)
    {
        switch (evt)
        {
            case MessageEvent { Raw: QIncomingMessage message }:
                if (evt.SelfId is { } qqSelfId) _ = ProtocolId(qqSelfId);
                _ = ProtocolId(message.PeerId);
                _ = ProtocolId(message.MessageId);
                _ = ProtocolId(message.SenderId);
                foreach (var reply in message.Segments.OfType<QIncomingReply>()) _ = ProtocolId(reply.MessageId);
                break;
            case MessageEvent message:
                if (message.SelfId is { } selfId) _ = ProtocolId(selfId);
                _ = ProtocolId(message.Channel.Id);
                _ = ProtocolId(message.MessageId);
                _ = ProtocolId(message.Sender.Id);
                foreach (var quote in message.Segments.OfType<QuoteSegment>()) _ = ProtocolId(quote.MessageId);
                break;
            case MessageReactionEvent reaction:
                if (reaction.Channel is { } reactionChannel) _ = ProtocolId(reactionChannel.Id);
                _ = ProtocolId(reaction.MessageId);
                if (reaction.User is { } reactor) _ = ProtocolId(reactor.Id);
                break;
            case InteractionEvent interaction:
                if (interaction.Channel is { } interactionChannel) _ = ProtocolId(interactionChannel.Id);
                _ = ProtocolId(interaction.User.Id);
                break;
            case MessageDeletedEvent deleted:
                _ = ProtocolId(deleted.Channel.Id);
                _ = ProtocolId(deleted.MessageId);
                if (deleted.SenderId is not null) _ = ProtocolId(deleted.SenderId);
                break;
            case MemberJoinedEvent joined:
                _ = ProtocolId(joined.Channel.Id);
                _ = ProtocolId(joined.UserId);
                if (joined.OperatorId is not null) _ = ProtocolId(joined.OperatorId);
                break;
            case MemberLeftEvent left:
                _ = ProtocolId(left.Channel.Id);
                _ = ProtocolId(left.UserId);
                if (left.OperatorId is not null) _ = ProtocolId(left.OperatorId);
                break;
            case FriendRequestEvent request:
                _ = ProtocolId(request.UserId);
                break;
            case GuildInviteEvent invite:
                _ = ProtocolId(invite.GuildId);
                _ = ProtocolId(invite.InviterId);
                break;
            case PlatformEvent { Raw: QOfficialButtonInteraction or QOfficialLifecycleEvent }:
                throw new NotSupportedException("OneBot 11 cannot represent QQ official openid identifiers.");
            case PlatformEvent { Raw: QEventPayload payload }:
                if (payload.SelfId is { } payloadSelfId) _ = ProtocolId(payloadSelfId);
                foreach (var value in ProtocolIdentifierValues(payload)) _ = ProtocolId(value);
                break;
        }
    }

    private static IEnumerable<string> ProtocolIdentifierValues(QEventPayload payload) => payload switch
    {
        QFriendNudge value => [value.UserId],
        QFriendFileUpload value => [value.UserId],
        QGroupAdminChange value => [value.GroupId, value.UserId, .. Optional(value.OperatorId)],
        QGroupEssenceMessageChange value => [value.GroupId, value.MessageId, .. Optional(value.OperatorId)],
        QGroupNameChange value => [value.GroupId, .. Optional(value.OperatorId)],
        QGroupMessageReaction value => [value.GroupId, value.UserId, value.MessageId],
        QGroupMute value => [value.GroupId, value.UserId, .. Optional(value.OperatorId)],
        QGroupWholeMute value => [value.GroupId, .. Optional(value.OperatorId)],
        QGroupNudge value => [value.GroupId, value.SenderId, value.ReceiverId],
        QGroupFileUpload value => [value.GroupId, value.UserId],
        QGroupJoinRequest value => [value.GroupId, value.UserId],
        QGroupDisband value => [value.GroupId, .. Optional(value.OperatorId)],
        QMessageRecall value => [value.PeerId, value.MessageId, value.SenderId, value.OperatorId],
        QGroupMemberIncrease value => [value.GroupId, value.UserId, .. Optional(value.OperatorId), .. Optional(value.InvitorId)],
        QGroupMemberDecrease value => [value.GroupId, value.UserId, .. Optional(value.OperatorId)],
        QFriendRequestReceived value => [value.InitiatorId],
        QGroupInvitation value => [value.GroupId, value.InitiatorId],
        _ => []
    };

    private static IEnumerable<string> Optional(string? value) => value is null ? [] : [value];

    private static bool IsSelfSender(MessageEvent message) =>
        long.TryParse(message.Sender.Id, out var senderId) &&
        long.TryParse(message.SelfId, out var selfId) &&
        senderId == selfId;

    private static OneBotMessageReference? Reference(Channel channel, string sequence) =>
        Reference(channel.Type == ChannelType.Group ? MessageScene.Group : channel.Type == ChannelType.Direct ? MessageScene.Friend : MessageScene.Temp, channel.Id, sequence);

    private static OneBotMessageReference? Reference(MessageScene scene, string peerId, string sequence) =>
        long.TryParse(peerId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericPeer) &&
        long.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSequence)
            ? new OneBotMessageReference(scene, numericPeer, numericSequence)
            : null;

    private static OneBotEvent MapJoined(MemberJoinedEvent joined) => Notice(joined, "group_increase", new Dictionary<string, object?>
    {
        ["group_id"] = ProtocolId(joined.Channel.Id),
        ["user_id"] = ProtocolId(joined.UserId),
        ["operator_id"] = NumericOrNull(joined.OperatorId)
    }, joined.OperatorId is null ? "approve" : "invite");

    private static OneBotEvent MapLeft(MemberLeftEvent left) => Notice(left, "group_decrease", new Dictionary<string, object?>
    {
        ["group_id"] = ProtocolId(left.Channel.Id),
        ["user_id"] = ProtocolId(left.UserId),
        ["operator_id"] = NumericOrNull(left.OperatorId)
    }, left.UserId == left.SelfId ? "kick_me" : left.OperatorId is null ? "leave" : "kick");

    private static OneBotEvent MapDeleted(MessageDeletedEvent deleted)
    {
        var data = new Dictionary<string, object?>
        {
            ["message_id"] = ProtocolId(deleted.MessageId),
            ["user_id"] = NumericOrNull(deleted.SenderId),
            ["operator_id"] = NumericOrNull(deleted.OperatorId)
        };
        if (deleted.Channel.Type != ChannelType.Direct) data["group_id"] = ProtocolId(deleted.Channel.Id);
        return Notice(deleted, deleted.Channel.Type == ChannelType.Direct ? "friend_recall" : "group_recall", data);
    }

    private static Dictionary<string, object?> Add(IDictionary<string, object?> source, string name, object? value)
    {
        var result = new Dictionary<string, object?>(source) { [name] = value };
        return result;
    }
}
