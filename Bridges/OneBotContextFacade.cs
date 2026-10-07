using ShiroBot.Model.QQ;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using System.Globalization;

namespace ShiroBot.Plugin.OneBotServer.Bridges;

/// <summary>Production bridge backed by generic host APIs and optional QQ adapter extensions.</summary>
public sealed class OneBotContextFacade(IBotContext context) : IOneBotContextFacade
{
    private IQFriendApi Friend => context.GetAdapterExtension<IQFriendApi>() ?? throw Unsupported();
    private IQGroupApi Group => context.GetAdapterExtension<IQGroupApi>() ?? throw Unsupported();
    private IQFileApi File => context.GetAdapterExtension<IQFileApi>() ?? throw Unsupported();
    private IQSystemApi System => context.GetAdapterExtension<IQSystemApi>() ?? throw Unsupported();
    private IQMessageApi Message => context.GetAdapterExtension<IQMessageApi>() ?? throw Unsupported();
    public string StorageDirectory => Path.Combine(context.PluginDirectory, "storage");

    public async Task<string> SendAsync(Channel channel, IReadOnlyList<MessageSegment> message)
    {
        SentMessage sent;
        if (message.Any(segment => segment is MarkdownSegment or CardSegment))
        {
            sent = await context.Message.SendMessageAsync(channel, new OutgoingMessage
            {
                Segments = message,
                AllowedFallbacks = MessageFallbackOptions.MarkdownAsText |
                    MessageFallbackOptions.CardAsMarkdown | MessageFallbackOptions.CardAsText
            }).ConfigureAwait(false);
        }
        else
        {
            sent = await context.Message.SendMessageAsync(channel, message).ConfigureAwait(false);
        }

        if (!sent.IsSuccess)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(sent.ErrorMessage)
                ? "The active adapter failed to send the message."
                : $"The active adapter failed to send the message: {sent.ErrorMessage}");
        return sent.MessageId;
    }

    public Task DeleteAsync(string messageId, Channel? channel = null) => channel is { } target
        ? context.Message.DeleteMessageAsync(target, messageId)
        : throw Unsupported("delete_msg requires a registered message reference or channel context.");

    public async Task<object?> GetAsync(string messageId, Channel? channel = null)
    {
        if (channel is not { } target) throw Unsupported("get_msg requires a registered message reference or channel context.");
        var message = await context.Message.GetMessageAsync(target, messageId);
        return message is null ? null : MessageInfo(message);
    }

    public async Task<object?> GetForwardedAsync(string forwardId) => new
    {
        messages = (await Message.GetForwardedMessagesAsync(forwardId)).Select(message => new
        {
            content = MessageSegments.FromQq(message.Segments),
            sender = new { nickname = message.SenderName ?? string.Empty, user_id = (long?)null },
            time = message.Time.ToUnixTimeSeconds(),
        }).ToArray(),
    };

    public async Task<object?> GetHistoryAsync(Channel channel, string? beforeMessageId, int limit) => new
    {
        messages = (await context.Message.GetHistoryMessagesAsync(channel, beforeMessageId, limit)).Select(MessageInfo).ToArray(),
    };

    public Task MarkAsReadAsync(string messageId, Channel? channel = null) => channel is { } target
        ? Message.MarkAsReadAsync(ToScene(target), target.Id, messageId)
        : throw Unsupported("mark_msg_as_read requires a registered message reference or channel context.");

    public async Task<object?> GetLoginInfoAsync()
    {
        User self;
        try
        {
            self = await context.User.GetSelfAsync().ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            var login = await System.GetLoginInfoAsync().ConfigureAwait(false);
            return new { user_id = WireId(login.Uin, "user_id"), nickname = login.Nickname };
        }
        return new { user_id = WireId(self.Id, "user_id"), nickname = self.Name ?? string.Empty };
    }
    public async Task<string> GetSelfIdAsync()
    {
        User self;
        try { self = await context.User.GetSelfAsync().ConfigureAwait(false); }
        catch (NotSupportedException) { return WireId((await System.GetLoginInfoAsync()).Uin, "self_id").ToString(CultureInfo.InvariantCulture); }
        return WireId(self.Id, "self_id").ToString(CultureInfo.InvariantCulture);
    }
    public Task<bool> CanSendImageAsync() => Task.FromResult(true);
    public Task<bool> CanSendRecordAsync() => Task.FromResult(true);
    public async Task<object?> GetStatusAsync()
    {
        _ = await GetSelfIdAsync();
        return new { online = true, good = true };
    }
    public Task CleanCacheAsync()
    {
        var directory = Path.Combine(StorageDirectory, "cache");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public async Task<object?> GetStrangerAsync(long userId, bool noCache)
    {
        var profile = await System.GetUserProfileAsync(userId.ToString(CultureInfo.InvariantCulture));
        return new
        {
            user_id = WireId(profile.UserId, "user_id"),
            nickname = profile.Nickname,
            sex = Sex(profile.Sex),
            age = profile.Age,
            qid = profile.Qid ?? string.Empty,
            level = profile.Level,
            login_days = 0,
        };
    }

    public async Task<object?> GetFriendsAsync(bool noCache) =>
        (await System.GetFriendListAsync(noCache)).Select(FriendInfo).ToArray();

    public async Task<object?> GetGroupsAsync(bool noCache) =>
        (await Group.GetGroupListAsync(noCache)).Select(GroupInfo).ToArray();

    public async Task<object?> GetGroupAsync(long groupId, bool noCache) => GroupInfo(await Group.GetGroupInfoAsync(groupId.ToString(CultureInfo.InvariantCulture), noCache));
    public async Task<object?> GetGroupMembersAsync(long groupId, bool noCache) =>
        (await Group.GetGroupMemberListAsync(groupId.ToString(CultureInfo.InvariantCulture), noCache)).Select(MemberInfo).ToArray();

    public async Task<object?> GetGroupMemberAsync(long groupId, long userId, bool noCache) =>
        MemberInfo(await Group.GetGroupMemberInfoAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), noCache));
    public Task<string> GetCookiesAsync(string domain) => System.GetCookiesAsync(domain);
    public Task<string> GetCsrfTokenAsync() => System.GetCsrfTokenAsync();
    public async Task<object?> GetVersionInfoAsync()
    {
        var version = await System.GetImplInfoAsync();
        return new
        {
            app_name = version.ImplName,
            app_version = version.ImplVersion,
            protocol_version = version.ProtocolVersion ?? "v11",
            qq_protocol_version = version.QqProtocolVersion,
            qq_protocol_type = version.QqProtocolType,
        };
    }
    public Task<string> GetResourceUrlAsync(string resourceId) => context.Message.GetResourceUrlAsync(resourceId);
    public Task SendLikeAsync(long userId, int count) => Friend.SendProfileLikeAsync(userId.ToString(CultureInfo.InvariantCulture), count);
    public Task DeleteFriendAsync(long userId) => Friend.DeleteFriendAsync(userId.ToString(CultureInfo.InvariantCulture));
    public Task SetGroupNameAsync(long groupId, string name) => Group.SetGroupNameAsync(groupId.ToString(CultureInfo.InvariantCulture), name);
    public Task SetGroupPortraitAsync(long groupId, string file) => Group.SetGroupAvatarAsync(groupId.ToString(CultureInfo.InvariantCulture), file);
    public Task SetGroupCardAsync(long groupId, long userId, string card) => Group.SetMemberCardAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), card);
    public Task SetGroupAdminAsync(long groupId, long userId, bool enabled) => Group.SetMemberAdminAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), enabled);
    public Task SetGroupSpecialTitleAsync(long groupId, long userId, string title) => Group.SetMemberSpecialTitleAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), title);
    public Task SetGroupBanAsync(long groupId, long userId, TimeSpan duration) => Group.MuteMemberAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), duration);
    public Task SetGroupWholeBanAsync(long groupId, bool enabled) => Group.SetWholeMuteAsync(groupId.ToString(CultureInfo.InvariantCulture), enabled);
    public Task SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest) => Group.KickMemberAsync(groupId.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture), rejectAddRequest);
    public Task SetGroupLeaveAsync(long groupId) => Group.QuitGroupAsync(groupId.ToString(CultureInfo.InvariantCulture));

    public Task SetFriendRequestAsync(RequestFlag request, bool approve, string? remark)
    {
        if (!string.IsNullOrWhiteSpace(request.NativeToken))
            return approve ? context.User.AcceptFriendRequestAsync(request.NativeToken) : context.User.RejectFriendRequestAsync(request.NativeToken, remark);
        return approve
            ? Friend.AcceptFriendRequestAsync(request.InitiatorUid!)
            : Friend.RejectFriendRequestAsync(request.InitiatorUid!, reason: remark);
    }

    public Task SetGroupRequestAsync(RequestFlag requestFlag, bool approve, string? reason)
    {
        if (requestFlag.Kind == "invitation")
        {
            var groupId = requestFlag.GroupId?.ToString(CultureInfo.InvariantCulture) ?? throw Unsupported("The request flag has no numeric group ID.");
            var invitationId = requestFlag.NativeToken ?? throw Unsupported("The invitation flag has no native invitation ID.");
            return approve ? Group.AcceptInvitationAsync(groupId, invitationId) : Group.RejectInvitationAsync(groupId, invitationId);
        }

        if (requestFlag.Kind == "group" && requestFlag.EncodedRequest is { } encoded)
        {
            QGroupJoinRequest request;
            try { request = global::System.Text.Json.JsonSerializer.Deserialize<QGroupJoinRequest>(Convert.FromBase64String(encoded)) ?? throw new FormatException(); }
            catch (Exception exception) when (exception is FormatException or global::System.Text.Json.JsonException)
            { throw Unsupported("The request flag does not contain a valid native QQ approval request."); }
            if (request.GroupId != requestFlag.GroupId?.ToString(CultureInfo.InvariantCulture) ||
                request.IsFiltered != requestFlag.Filtered ||
                request.IsInvited != (requestFlag.RequestType == "invited_join_request"))
                throw Unsupported("The request flag does not match its native QQ approval request.");
            return approve ? Group.AcceptJoinRequestAsync(request) : Group.RejectJoinRequestAsync(request, reason);
        }

        throw Unsupported("This request flag does not contain the native QQ approval credentials.");
    }

    public Task SendNudgeAsync(long? groupId, long userId) => groupId is long id ? Group.SendNudgeAsync(id.ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture)) : Friend.SendNudgeAsync(userId.ToString(CultureInfo.InvariantCulture));
    public Task SetReactionAsync(long groupId, long messageId, string reactionId, bool enabled) => Group.SendMessageReactionAsync(groupId.ToString(CultureInfo.InvariantCulture), messageId.ToString(CultureInfo.InvariantCulture), reactionId, enabled);
    public async Task<object?> GetAnnouncementsAsync(long groupId) =>
        (await Group.GetAnnouncementsAsync(groupId.ToString(CultureInfo.InvariantCulture))).Select(notice => new
        {
            notice_id = notice.AnnouncementId,
            sender_id = notice.UserId,
            publish_time = notice.Time.ToUnixTimeSeconds(),
            message = new
            {
                text = notice.Content,
                images = notice.ImageUrl is null ? [] : new[] { new { height = "0", width = "0", id = notice.ImageUrl } },
            },
        }).ToArray();
    public Task SendAnnouncementAsync(long groupId, string content, string? image) => Group.SendAnnouncementAsync(groupId.ToString(CultureInfo.InvariantCulture), content, image);
    public Task DeleteAnnouncementAsync(long groupId, string noticeId) => Group.DeleteAnnouncementAsync(groupId.ToString(CultureInfo.InvariantCulture), noticeId);
    public async Task<object?> GetEssenceMessagesAsync(long groupId, int pageIndex, int pageSize) =>
        (await Group.GetEssenceMessagesAsync(groupId.ToString(CultureInfo.InvariantCulture), pageIndex, pageSize)).Select(message => new
        {
            sender_id = message.SenderId,
            sender_nick = message.SenderName ?? string.Empty,
            sender_time = message.MessageTime.ToUnixTimeSeconds(),
            operator_id = message.OperatorId,
            operator_nick = message.OperatorName ?? string.Empty,
            operator_time = message.OperationTime.ToUnixTimeSeconds(),
            message_id = message.MessageId,
        }).ToArray();
    public Task SetEssenceMessageAsync(long groupId, long messageId, bool enabled) => Group.SetEssenceMessageAsync(groupId.ToString(CultureInfo.InvariantCulture), messageId.ToString(CultureInfo.InvariantCulture), enabled);
    public Task<string> UploadGroupFileAsync(long groupId, string file, string name, string folderId) => File.UploadGroupFileAsync(groupId.ToString(CultureInfo.InvariantCulture), file, name, folderId);
    public Task<string> UploadPrivateFileAsync(long userId, string file, string name) => File.UploadPrivateFileAsync(userId.ToString(CultureInfo.InvariantCulture), file, name);
    public async Task<object?> GetGroupFilesAsync(long groupId, string folderId)
    {
        var (files, folders) = await File.GetGroupFilesAsync(groupId.ToString(CultureInfo.InvariantCulture), folderId);
        return new
        {
            files = files.Select(file => new
            {
                group_id = file.GroupId,
                file_id = file.FileId,
                file_name = file.FileName,
                busid = 102,
                file_size = file.FileSize,
                upload_time = file.UploadedTime?.ToUnixTimeSeconds() ?? 0,
                dead_time = file.ExpireTime?.ToUnixTimeSeconds() ?? 0,
                download_times = file.DownloadedTimes,
                uploader = file.UploaderId,
            }).ToArray(),
            folders = folders.Select(folder => new
            {
                group_id = folder.GroupId,
                folder_id = folder.FolderId,
                folder_name = folder.FolderName,
                create_time = folder.CreatedTime?.ToUnixTimeSeconds() ?? 0,
                creator = folder.CreatorId,
                total_file_count = folder.FileCount,
            }).ToArray(),
        };
    }
    public Task<string> GetGroupFileUrlAsync(long groupId, string fileId) => File.GetGroupFileDownloadUrlAsync(groupId.ToString(CultureInfo.InvariantCulture), fileId);
    public Task<string> GetPrivateFileUrlAsync(long userId, string fileId, string fileHash, bool isSelfSend) => File.GetPrivateFileDownloadUrlAsync(userId.ToString(CultureInfo.InvariantCulture), fileId, fileHash, isSelfSend);
    public Task DeleteGroupFileAsync(long groupId, string fileId) => File.DeleteGroupFileAsync(groupId.ToString(CultureInfo.InvariantCulture), fileId);
    public Task MoveGroupFileAsync(long groupId, string fileId, string targetFolderId, string parentFolderId) => File.MoveGroupFileAsync(groupId.ToString(CultureInfo.InvariantCulture), fileId, targetFolderId, parentFolderId);
    public Task RenameGroupFileAsync(long groupId, string fileId, string name, string parentFolderId) => File.RenameGroupFileAsync(groupId.ToString(CultureInfo.InvariantCulture), fileId, name, parentFolderId);
    public Task PersistGroupFileAsync(long groupId, string fileId) => File.PersistGroupFileAsync(groupId.ToString(CultureInfo.InvariantCulture), fileId);
    public Task<string> CreateGroupFolderAsync(long groupId, string name) => File.CreateGroupFolderAsync(groupId.ToString(CultureInfo.InvariantCulture), name);
    public Task RenameGroupFolderAsync(long groupId, string folderId, string name) => File.RenameGroupFolderAsync(groupId.ToString(CultureInfo.InvariantCulture), folderId, name);
    public Task DeleteGroupFolderAsync(long groupId, string folderId) => File.DeleteGroupFolderAsync(groupId.ToString(CultureInfo.InvariantCulture), folderId);
    public Task SetAvatarAsync(string file) => System.SetAvatarAsync(file);
    public Task<IReadOnlyList<string>> GetCustomFaceUrlsAsync() => System.GetCustomFaceUrlListAsync();
    public Task<IReadOnlyList<QFriend>> GetFriendEntitiesAsync(bool noCache) => System.GetFriendListAsync(noCache);
    public Task SetNicknameAsync(string nickname) => System.SetNicknameAsync(nickname);
    public Task SetBioAsync(string bio) => System.SetBioAsync(bio);
    public async Task<IReadOnlyList<QGroupNotification>> GetGroupNotificationsAsync(bool filtered, int limit) =>
        (await Group.GetNotificationsAsync(isFiltered: filtered, limit: limit)).Notifications;
    public Task<IReadOnlyList<QFriendRequest>> GetFriendRequestsAsync(int limit, bool filtered) => Friend.GetFriendRequestsAsync(limit, filtered);
    public Task AcceptFriendRequestAsync(string initiatorUid, bool filtered) => Friend.AcceptFriendRequestAsync(initiatorUid, filtered);
    public Task<IReadOnlyList<QGroupMember>> GetGroupMemberEntitiesAsync(long groupId, bool noCache) => Group.GetGroupMemberListAsync(groupId.ToString(CultureInfo.InvariantCulture), noCache);
    public async Task<string> GetUserNicknameAsync(long userId) => (await System.GetUserProfileAsync(userId.ToString(CultureInfo.InvariantCulture))).Nickname ?? string.Empty;
    public async Task<string> GetGroupNameAsync(long groupId) => (await Group.GetGroupInfoAsync(groupId.ToString(CultureInfo.InvariantCulture))).GroupName ?? string.Empty;

    public async Task<string> SendForwardAsync(Channel channel, IReadOnlyList<OneBotForwardNode> nodes, string? title, IReadOnlyList<string>? preview, string? summary, string? prompt)
    {
        var forwarded = nodes.Select(node => new QForwardedMessage(node.UserId.ToString(CultureInfo.InvariantCulture), node.SenderName, MessageSegments.ToQq(node.Content))
        {
            Time = node.Time,
        }).ToArray();
        var segment = new QOutgoingForward(forwarded) { Title = title, Preview = preview, Summary = summary, Prompt = prompt };
        return (await Message.SendMessageAsync(ToScene(channel), channel.Id, [segment])).ToString();
    }

    public async Task<string> ForwardSingleAsync(OneBotMessageReference source, Channel destination)
    {
        var message = await Message.GetMessageAsync(ToScene(source.Scene), source.PeerId.ToString(CultureInfo.InvariantCulture), source.Sequence.ToString(CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("The source message no longer exists.");
        return (await Message.SendMessageAsync(ToScene(destination), destination.Id, ToOutgoing(message.Segments))).ToString();
    }

    public async Task<OneBotForwardNode> GetForwardNodeAsync(OneBotMessageReference source)
    {
        var message = await Message.GetMessageAsync(ToScene(source.Scene), source.PeerId.ToString(CultureInfo.InvariantCulture), source.Sequence.ToString(CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("The source message no longer exists.");
        var senderName = message switch
        {
            QGroupMessage group => group.GroupMember.DisplayName,
            QFriendMessage friend => friend.Friend.Nickname ?? string.Empty,
            _ => string.Empty,
        };
        return new OneBotForwardNode(ParseId(message.SenderId), senderName,
            MessageSegments.FromQq(message.Segments), message.Time);
    }

    public async Task<IReadOnlyList<OneBotGroupFileEntry>> GetGroupFileEntriesAsync(long groupId, string folderId)
    {
        var (files, folders) = await File.GetGroupFilesAsync(groupId.ToString(CultureInfo.InvariantCulture), folderId);
        return files.Select(file => new OneBotGroupFileEntry(file.FileId, file.FileSize, file.ParentFolderId ?? "/", false))
            .Concat(folders.Select(folder => new OneBotGroupFileEntry(folder.FolderId, 0, folder.ParentFolderId ?? "/", true)))
            .ToArray();
    }

    private static object FriendInfo(QFriend friend) => new
    {
        user_id = WireId(friend.UserId, "user_id"),
        nickname = friend.Nickname,
        remark = friend.Remark ?? string.Empty,
    };

    private static object GroupInfo(QGroup group) => new
    {
        group_id = WireId(group.GroupId, "group_id"),
        group_name = group.GroupName,
        member_count = group.MemberCount,
        max_member_count = group.MaxMemberCount,
    };

    private static object MemberInfo(QGroupMember member) => new
    {
        group_id = WireId(member.GroupId, "group_id"),
        user_id = WireId(member.UserId, "user_id"),
        nickname = member.Nickname,
        card = member.Card ?? string.Empty,
        sex = Sex(member.Sex),
        age = 0,
        area = string.Empty,
        join_time = member.JoinTime?.ToUnixTimeSeconds() ?? 0,
        last_sent_time = member.LastSentTime?.ToUnixTimeSeconds() ?? 0,
        level = member.Level.ToString(),
        role = member.Role switch
        {
            QGroupRole.Member => "member",
            QGroupRole.Admin => "admin",
            QGroupRole.Owner => "owner",
            _ => null,
        },
        unfriendly = false,
        title = member.Title ?? string.Empty,
        title_expire_time = 0,
        card_changeable = false,
        shut_up_timestamp = member.ShutUpEndTime?.ToUnixTimeSeconds() ?? 0,
    };

    private static object MessageInfo(MessageEvent message)
    {
        var segments = MessageSegments.FromGeneric(message.Segments, message.Platform).Select(ToWireSegment).ToArray();
        return new
        {
            time = message.Timestamp.ToUnixTimeSeconds(),
            message_type = message.IsDirect ? "private" : "group",
            message_id = message.MessageId,
            real_id = message.MessageId,
            sender = new { user_id = WireId(message.Sender.Id, "sender.user_id"), nickname = message.Sender.Name },
            message = segments,
            raw_message = CqCode.Serialize(segments),
            font = 0,
            group_id = message.IsDirect ? (long?)null : WireId(message.Channel.Id, "group_id"),
        };
    }

    private static OneBotSegment ToWireSegment(OneBotSegment segment)
    {
        if (segment.Type is not ("at" or "reply") || !segment.Data.TryGetValue(segment.Type == "at" ? "qq" : "id", out var rawId) || rawId is null)
            return segment;
        var key = segment.Type == "at" ? "qq" : "id";
        if (segment.Type == "at" && string.Equals(rawId.ToString(), "all", StringComparison.Ordinal)) return segment;
        var data = new Dictionary<string, object?>(segment.Data) { [key] = WireId(rawId.ToString()!, segment.Type == "at" ? "at.qq" : "reply.id") };
        return segment with { Data = data };
    }

    private static string Sex(QSex sex) => sex switch
    {
        QSex.Male => "male",
        QSex.Female => "female",
        _ => "unknown",
    };

    private static NotSupportedException Unsupported(string? message = null) => new(message ?? "The active adapter does not provide this OneBot capability.");
    private static long WireId(string value, string field) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0
        ? id
        : throw new NotSupportedException($"OneBot 11 numeric {field} cannot represent this adapter's opaque identifier.");
    private static long ParseId(string value) => long.TryParse(value, out var result) ? result : throw new ArgumentException("OneBot identifier must be an integer.", nameof(value));
    private static QMessageScene ToScene(Channel channel) => channel.Type == ChannelType.Group ? QMessageScene.Group : QMessageScene.Friend;
    private static QMessageScene ToScene(MessageScene scene) => scene switch
    {
        MessageScene.Group => QMessageScene.Group,
        MessageScene.Temp => QMessageScene.Temp,
        _ => QMessageScene.Friend,
    };

    private static IReadOnlyList<QOutgoingSegment> ToOutgoing(IEnumerable<QIncomingSegment> segments) => segments.Select(segment => segment switch
    {
        QIncomingText value => (QOutgoingSegment)new QOutgoingText(value.Text),
        QIncomingMention value => new QOutgoingMention(value.UserId),
        QIncomingMentionAll => new QOutgoingMentionAll(),
        QIncomingFace value => new QOutgoingFace(value.FaceId, value.IsLarge),
        QIncomingImage value => new QOutgoingImage(value.TempUrl),
        QIncomingRecord value => new QOutgoingRecord(value.TempUrl),
        QIncomingVideo value => new QOutgoingVideo(value.TempUrl),
        QIncomingLightApp value => new QOutgoingLightApp(value.JsonPayload),
        _ => throw Unsupported($"Cannot forward QQ segment {segment.GetType().Name} as a standalone message."),
    }).ToArray();
}
