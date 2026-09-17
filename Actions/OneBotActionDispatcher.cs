using System.Collections.Concurrent;
using System.Text.Json;
using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Plugin.OneBotServer.Transports;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Actions;

public sealed class OneBotActionDispatcher : IOneBotActionHandler, IOneBotQuickOperationHandler
{
    private readonly IOneBotContextFacade context;
    private readonly OneBotRuntimeState? state;
    private readonly PluginFileStorage files;
    private static readonly string[] StandardActions =
    [
        "send_private_msg", "send_group_msg", "send_msg", "delete_msg", "get_msg", "get_forward_msg", "send_like",
        "set_group_kick", "set_group_ban", "set_group_anonymous_ban", "set_group_whole_ban", "set_group_admin",
        "set_group_anonymous", "set_group_card", "set_group_name", "set_group_leave", "set_group_special_title",
        "set_friend_add_request", "set_group_add_request", "get_login_info", "get_stranger_info", "get_friend_list",
        "get_group_info", "get_group_list", "get_group_member_info", "get_group_member_list", "get_group_honor_info",
        "get_cookies", "get_csrf_token", "get_credentials", "get_record", "get_image", "can_send_image",
        "can_send_record", "get_status", "get_version_info", "set_restart", "clean_cache",
    ];

    private static readonly string[] ExtensionActions =
    [
        "send_poke", "friend_poke", "group_poke", "mark_msg_as_read", "get_friend_msg_history", "get_group_msg_history",
        "set_essence_msg", "delete_essence_msg", "get_essence_msg_list", "_get_group_notice", "_send_group_notice",
        "_delete_group_notice", "set_qq_avatar", "set_group_portrait", "fetch_custom_face", "delete_friend",
        "get_friends_with_category", "set_qq_profile", "get_qq_avatar", "get_group_system_msg",
        "get_group_ignore_add_request", "get_doubt_friends_add_request", "set_doubt_friends_add_request",
        "batch_delete_group_member", "get_group_shut_list", "send_group_forward_msg", "send_private_forward_msg",
        "send_forward_msg", "forward_friend_single_msg", "forward_group_single_msg", "get_event",
        "set_msg_emoji_like", "unset_msg_emoji_like", "fetch_emoji_like",
    ];

    private static readonly string[] FileActions =
    [
        "upload_private_file", "upload_group_file", "get_private_file_url", "get_group_file_url",
        "get_group_root_files", "get_group_files_by_folder", "create_group_file_folder", "delete_group_folder",
        "rename_group_file_folder", "move_group_file", "rename_group_file", "delete_group_file",
        "set_group_file_forever", "get_group_file_system_info", "download_file", "get_file",
    ];

    private static readonly string[] UnsupportedExtendedActions =
    [
        "get_recommend_group_face", "get_ai_record", "get_group_ai_record", "send_group_ai_record", "get_ai_characters",
        "voice_msg_to_text", "send_pb", "set_config", "get_config", "llonebot_debug", "scan_qrcode", "get_rkey",
        "get_flash_file_info", "download_flash_file", "upload_flash_file", "reshare_flash_file", "get_group_album_list",
        "upload_group_album", "get_group_album_media_list", "create_group_album", "delete_group_album",
        "get_robot_uin_range", "set_online_status", "set_input_status", "get_profile_like", "get_profile_like_me",
        "set_friend_category", "set_friend_remark", "set_group_msg_mask", "set_group_remark",
        "get_group_at_all_remain", "send_group_sign", "ocr_image",
    ];

    private static readonly HashSet<string> RegisteredActions = new(
        StandardActions.Concat([".handle_quick_operation"]).Concat(ExtensionActions).Concat(FileActions)
            .Concat(["get_guild_list"]).Concat(UnsupportedExtendedActions), StringComparer.Ordinal);
    private readonly object _rateLimitLock = new();
    private readonly object _credentialLock = new();
    private readonly Dictionary<string, CachedCredential<string>> _cookieCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _rateLimit;
    private readonly TimeSpan _credentialCacheDuration = TimeSpan.FromMinutes(5);
    private Task _rateLimitedTail = Task.CompletedTask;
    private Task<string>? _csrfFetch;
    private CachedCredential<long>? _csrfCache;
    private DateTimeOffset _lastRateLimitedAt = DateTimeOffset.MinValue;

    public OneBotActionDispatcher(IOneBotContextFacade context, TimeSpan? rateLimit = null) : this(context, null, rateLimit) { }

    public OneBotActionDispatcher(IOneBotContextFacade context, OneBotRuntimeState? state, TimeSpan? rateLimit = null)
    {
        this.context = context;
        this.state = state;
        files = new PluginFileStorage(context.StorageDirectory);
        _rateLimit = rateLimit ?? TimeSpan.Zero;
    }

    public IReadOnlyCollection<string> Actions => RegisteredActions;
    public IReadOnlyCollection<string> StandardActionNames => StandardActions;

    public async Task<OneBotResponse<object?>> DispatchAsync(OneBotActionRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request.Action);
        if (!RegisteredActions.Contains(normalized.Action))
            return OneBotResponse<object?>.Failed(1404, $"Unsupported action: {normalized.Action}", request.Echo);

        if (normalized.Async || normalized.RateLimited)
        {
            var operation = () => InvokeAsync(normalized.Action, request.Params, CancellationToken.None);
            _ = normalized.RateLimited ? EnqueueRateLimited(operation) : RunBackground(operation);
            return new OneBotResponse<object?>("async", 1, null, Echo: request.Echo);
        }

        try
        {
            return OneBotResponse<object?>.Ok(await InvokeAsync(normalized.Action, request.Params, cancellationToken), request.Echo);
        }
        catch (OneBotParameterException exception)
        {
            return OneBotResponse<object?>.Failed(1400, exception.Message, request.Echo);
        }
        catch (ArgumentException exception)
        {
            return OneBotResponse<object?>.Failed(1400, exception.Message, request.Echo);
        }
        catch (NotSupportedException exception)
        {
            return OneBotResponse<object?>.Failed(1404, exception.Message, request.Echo);
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot] Action {normalized.Action} 执行异常 ({exception.GetType().Name}): {exception}");
            return OneBotResponse<object?>.Failed(1500, exception.Message, request.Echo);
        }
    }

    public Task<OneBotResponse<object?>> HandleAsync(OneBotActionRequest request, CancellationToken cancellationToken) =>
        DispatchAsync(request, cancellationToken);

    async Task IOneBotQuickOperationHandler.HandleAsync(JsonElement @event, JsonElement operation, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["context"] = @event.Clone(),
            ["operation"] = operation.Clone(),
        };
        var response = await DispatchAsync(new OneBotActionRequest(".handle_quick_operation", parameters, null), cancellationToken).ConfigureAwait(false);
        if (response.RetCode != 0) throw new OneBotActionException(400, response.RetCode, response.Message ?? "Quick operation failed");
    }

    private static async Task RunBackground(Func<Task<object?>> operation)
    {
        try { await operation(); }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot] 异步 Action 执行异常 ({exception.GetType().Name}): {exception}");
        }
    }

    private Task EnqueueRateLimited(Func<Task<object?>> operation)
    {
        lock (_rateLimitLock)
        {
            _rateLimitedTail = _rateLimitedTail.ContinueWith(async _ =>
            {
                var wait = _rateLimit - (DateTimeOffset.UtcNow - _lastRateLimitedAt);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                try { await operation(); }
                catch (Exception exception)
                {
                    BotLog.Error($"[OneBot] 限速 Action 执行异常 ({exception.GetType().Name}): {exception}");
                }
                finally { _lastRateLimitedAt = DateTimeOffset.UtcNow; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            return _rateLimitedTail;
        }
    }

    private async Task<object?> InvokeAsync(string action, IReadOnlyDictionary<string, JsonElement> p, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case "send_private_msg": return await SendAsync(Channel.Direct(Id(p, "user_id").ToString()), Message(p));
            case "send_group_msg": return await SendAsync(Channel.Group(Id(p, "group_id").ToString()), Message(p));
            case "send_msg": return await SendAsync(Target(p), Message(p));
            case "delete_msg": var delete = MessageReference(p); return await Void(context.DeleteAsync(delete.Sequence.ToString(), ToChannel(delete)));
            case "get_msg":
                var getMessageId = Id(p, "message_id");
                var get = MessageReference(p);
                return RewriteMessageId(await context.GetAsync(get.Sequence.ToString(), ToChannel(get)), getMessageId);
            case "get_forward_msg": return await context.GetForwardedAsync(FirstString(p, ["id", "forward_id"]));
            case "send_like": return await Void(context.SendLikeAsync(Id(p, "user_id"), NonNegativeInt(p, "times", 1)));
            case "get_login_info": return await context.GetLoginInfoAsync();
            case "get_stranger_info": return await context.GetStrangerAsync(Id(p, "user_id"), Bool(p, "no_cache"));
            case "get_friend_list": return await context.GetFriendsAsync(Bool(p, "no_cache"));
            case "get_group_list": return await context.GetGroupsAsync(Bool(p, "no_cache"));
            case "get_group_info": return await context.GetGroupAsync(Id(p, "group_id"), Bool(p, "no_cache"));
            case "get_group_member_list": return await context.GetGroupMembersAsync(Id(p, "group_id"), Bool(p, "no_cache"));
            case "get_group_member_info": return await context.GetGroupMemberAsync(Id(p, "group_id"), Id(p, "user_id"), Bool(p, "no_cache"));
            case "get_friend_msg_history":
                var friendChannel = Channel.Direct(Id(p, "user_id").ToString());
                return await RewriteMessageCollectionAsync(await context.GetHistoryAsync(friendChannel, ResolveOptionalSequence(p), NonNegativeInt(p, "count", 20)), friendChannel);
            case "get_group_msg_history":
                var groupChannel = Channel.Group(Id(p, "group_id").ToString());
                return await RewriteMessageCollectionAsync(await context.GetHistoryAsync(groupChannel, ResolveOptionalSequence(p), NonNegativeInt(p, "count", 20)), groupChannel);
            case "mark_msg_as_read": var read = MessageReference(p); return await Void(context.MarkAsReadAsync(read.Sequence.ToString(), ToChannel(read)));
            case "get_cookies": return new { cookies = await GetCookiesAsync(OneBotParameters.String(p, "domain", "")) };
            case "get_csrf_token": return new { token = await GetCsrfTokenAsync() };
            case "get_credentials":
                var cookies = await GetCookiesAsync(OneBotParameters.String(p, "domain", ""));
                return new { cookies, csrf_token = await GetCsrfTokenAsync() };
            case "get_version_info": return await context.GetVersionInfoAsync();
            case "get_image": return new { file = await context.GetResourceUrlAsync(OneBotParameters.String(p, "file")) };
            case "get_record":
                var format = OptionalString(p, "out_format");
                if (!string.IsNullOrEmpty(format) && !string.Equals(format, "original", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Audio format conversion is not available; out_format must be 'original'.");
                return new { file = await context.GetResourceUrlAsync(OneBotParameters.String(p, "file")) };
            case "can_send_image": return new { yes = await context.CanSendImageAsync() };
            case "can_send_record": return new { yes = await context.CanSendRecordAsync() };
            case "get_status": return await context.GetStatusAsync();
            case "clean_cache": return await Void(context.CleanCacheAsync());
            case "set_restart": throw new NotSupportedException("The host SDK does not expose a restart capability.");
            case "get_event": return await GetEventsAsync(p, cancellationToken);
            case ".handle_quick_operation": return await HandleQuickOperationAsync(p);
            case "get_guild_list": return null;
            case "set_group_name": return await Void(context.SetGroupNameAsync(Id(p, "group_id"), OneBotParameters.String(p, "group_name")));
            case "set_group_portrait": return await Void(context.SetGroupPortraitAsync(Id(p, "group_id"), OneBotParameters.String(p, "file")));
            case "set_group_card": return await Void(context.SetGroupCardAsync(Id(p, "group_id"), Id(p, "user_id"), OneBotParameters.String(p, "card", "")));
            case "set_group_admin": return await Void(context.SetGroupAdminAsync(Id(p, "group_id"), Id(p, "user_id"), Bool(p, "enable", true)));
            case "set_group_special_title":
                if ((OptionalId(p, "duration") ?? -1) != -1) throw new NotSupportedException("Only permanent special titles (duration = -1) are supported.");
                return await Void(context.SetGroupSpecialTitleAsync(Id(p, "group_id"), Id(p, "user_id"), OneBotParameters.String(p, "special_title", "")));
            case "set_group_ban": return await Void(context.SetGroupBanAsync(Id(p, "group_id"), Id(p, "user_id"), TimeSpan.FromSeconds(NonNegativeInt(p, "duration", 1800))));
            case "set_group_whole_ban": return await Void(context.SetGroupWholeBanAsync(Id(p, "group_id"), Bool(p, "enable", true)));
            case "set_group_kick": return await Void(context.SetGroupKickAsync(Id(p, "group_id"), Id(p, "user_id"), Bool(p, "reject_add_request")));
            case "set_group_leave":
                if (Bool(p, "is_dismiss")) throw new NotSupportedException("The SDK cannot dismiss a group.");
                return await Void(context.SetGroupLeaveAsync(Id(p, "group_id")));
            case "set_friend_add_request":
                var friendRequest = DecodeFlag(p);
                if (friendRequest.Kind != "friend") throw new OneBotParameterException("flag is not a friend request");
                return await Void(context.SetFriendRequestAsync(friendRequest, Bool(p, "approve", true), OptionalString(p, "remark")));
            case "set_group_add_request":
                var groupRequest = DecodeFlag(p);
                if (groupRequest.Kind is not ("group" or "invitation")) throw new OneBotParameterException("flag is not a group request");
                return await Void(context.SetGroupRequestAsync(groupRequest, Bool(p, "approve", true), OptionalString(p, "reason")));
            case "send_poke": case "friend_poke": return await Void(context.SendNudgeAsync(null, Id(p, "user_id")));
            case "group_poke": return await Void(context.SendNudgeAsync(Id(p, "group_id"), Id(p, "user_id")));
            case "delete_friend": return await Void(context.DeleteFriendAsync(Id(p, "user_id")));
            case "_send_group_notice": return await Void(context.SendAnnouncementAsync(Id(p, "group_id"), OneBotParameters.String(p, "content"), OptionalString(p, "image")));
            case "_get_group_notice": return await context.GetAnnouncementsAsync(Id(p, "group_id"));
            case "_delete_group_notice": return await Void(context.DeleteAnnouncementAsync(Id(p, "group_id"), OneBotParameters.String(p, "notice_id")));
            case "get_essence_msg_list":
                var essenceGroupId = Id(p, "group_id");
                return await RewriteMessageCollectionAsync(await context.GetEssenceMessagesAsync(essenceGroupId, 0, int.MaxValue), Channel.Group(essenceGroupId.ToString()));
            case "set_essence_msg": var essence = GroupMessageReference(p); return await Void(context.SetEssenceMessageAsync(essence.PeerId, essence.Sequence, true));
            case "delete_essence_msg": var unessence = GroupMessageReference(p); return await Void(context.SetEssenceMessageAsync(unessence.PeerId, unessence.Sequence, false));
            case "set_msg_emoji_like": var reaction = GroupMessageReference(p); return await Void(context.SetReactionAsync(reaction.PeerId, reaction.Sequence, OneBotParameters.String(p, "emoji_id"), Bool(p, "set", true)));
            case "unset_msg_emoji_like": var unreaction = GroupMessageReference(p); return await Void(context.SetReactionAsync(unreaction.PeerId, unreaction.Sequence, OneBotParameters.String(p, "emoji_id"), false));
            case "fetch_emoji_like": return await FetchEmojiLikesAsync(p);
            case "set_qq_avatar": return await Void(context.SetAvatarAsync(OneBotParameters.String(p, "file")));
            case "fetch_custom_face": return (await context.GetCustomFaceUrlsAsync()).Take(NonNegativeInt(p, "count", 48)).ToArray();
            case "get_friends_with_category": return await GetFriendsWithCategoryAsync(Bool(p, "no_cache"));
            case "set_qq_profile": return await SetProfileAsync(p);
            case "get_qq_avatar": return GetQqAvatar(p);
            case "get_group_system_msg": return await GetGroupSystemMessagesAsync();
            case "get_group_ignore_add_request": return await GetIgnoredGroupRequestsAsync(p);
            case "get_doubt_friends_add_request": return await GetDoubtFriendRequestsAsync(p);
            case "set_doubt_friends_add_request": return await SetDoubtFriendRequestAsync(p);
            case "batch_delete_group_member": return await BatchDeleteGroupMembersAsync(p);
            case "get_group_shut_list": return (await context.GetGroupMemberEntitiesAsync(Id(p, "group_id"), Bool(p, "no_cache"))).Where(member => member.ShutUpEndTime > DateTimeOffset.UtcNow).Select(MemberInfo).ToArray();
            case "send_group_forward_msg": return await SendForwardAsync(p, Channel.Group(Id(p, "group_id").ToString()));
            case "send_private_forward_msg": return await SendForwardAsync(p, Channel.Direct(Id(p, "user_id").ToString()));
            case "send_forward_msg": return await SendForwardAsync(p, Target(p));
            case "forward_friend_single_msg": return await ForwardSingleAsync(p, Channel.Direct(Id(p, "user_id").ToString()));
            case "forward_group_single_msg": return await ForwardSingleAsync(p, Channel.Group(Id(p, "group_id").ToString()));
            case "upload_group_file": return new { file_id = await context.UploadGroupFileAsync(Id(p, "group_id"), FirstString(p, ["file", "file_uri"]), FirstString(p, ["name", "file_name"], "file"), FirstString(p, ["folder", "folder_id", "parent_folder_id"], "/")) };
            case "upload_private_file": return new { file_id = await context.UploadPrivateFileAsync(Id(p, "user_id"), FirstString(p, ["file", "file_uri"]), FirstString(p, ["name", "file_name"], "file")) };
            case "get_group_root_files": return await context.GetGroupFilesAsync(Id(p, "group_id"), "/");
            case "get_group_files_by_folder": return await context.GetGroupFilesAsync(Id(p, "group_id"), FirstString(p, ["folder_id", "parent_folder_id"]));
            case "get_group_file_url": return await GetGroupFileUrlAsync(p);
            case "get_private_file_url":
                var fileId = FirstString(p, ["file_id", "file"]);
                var file = ResolvePrivateFile(p, fileId);
                return new { url = await context.GetPrivateFileUrlAsync(file.UserId, fileId, file.FileHash, file.IsSelfSend) };
            case "delete_group_file": return await Void(context.DeleteGroupFileAsync(Id(p, "group_id"), OneBotParameters.String(p, "file_id")));
            case "move_group_file": return await Void(context.MoveGroupFileAsync(Id(p, "group_id"), OneBotParameters.String(p, "file_id"), FirstString(p, ["target_directory", "target_folder_id", "folder_id"], "/"), FirstString(p, ["parent_directory", "parent_folder_id", "current_parent_directory"], "/")));
            case "rename_group_file": return await Void(context.RenameGroupFileAsync(Id(p, "group_id"), OneBotParameters.String(p, "file_id"), FirstString(p, ["new_name", "new_file_name"]), FirstString(p, ["current_parent_directory", "parent_folder_id", "parent_directory"], "/")));
            case "set_group_file_forever": return await Void(context.PersistGroupFileAsync(Id(p, "group_id"), OneBotParameters.String(p, "file_id")));
            case "create_group_file_folder": return new { folder_id = await context.CreateGroupFolderAsync(Id(p, "group_id"), FirstString(p, ["name", "folder_name"])) };
            case "rename_group_file_folder": return await Void(context.RenameGroupFolderAsync(Id(p, "group_id"), OneBotParameters.String(p, "folder_id"), FirstString(p, ["new_folder_name", "new_name"])));
            case "delete_group_folder": return await Void(context.DeleteGroupFolderAsync(Id(p, "group_id"), OneBotParameters.String(p, "folder_id")));
            case "get_group_file_system_info": return await GetGroupFileSystemInfoAsync(Id(p, "group_id"));
            case "download_file": return new { file = await files.DownloadAsync(OptionalString(p, "url"), OptionalString(p, "base64"), OptionalString(p, "name"), DownloadHeaders(p), cancellationToken) };
            case "get_file": return await GetFileAsync(p, cancellationToken);
            default: throw new NotSupportedException($"The host SDK does not expose action '{action}'.");
        }
    }

    private async Task<object?> FetchEmojiLikesAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (state is null) throw new NotSupportedException("Reaction data requires an initialized OneBot runtime.");
        var reference = GroupMessageReference(p);
        var users = state.Reactions.GetUsers(reference.PeerId, reference.Sequence, OneBotParameters.String(p, "emoji_id"), NonNegativeInt(p, "count", 20));
        var likes = await Task.WhenAll(users.Select(async userId => new
        {
            tinyId = userId.ToString(),
            nickName = await TryNameAsync(() => context.GetUserNicknameAsync(userId)),
            headUrl = $"https://q1.qlogo.cn/g?b=qq&nk={userId}&s=640",
        }));
        return new { result = 0, errMsg = string.Empty, emojiLikesList = likes, cookie = string.Empty, isLastPage = true, isFirstPage = true };
    }

    private async Task<object?> GetFriendsWithCategoryAsync(bool noCache)
    {
        var friends = await context.GetFriendEntitiesAsync(noCache);
        return friends.GroupBy(friend => friend.Category ?? new QFriendCategory(0, string.Empty))
            .Select((category, index) => new
            {
                categoryId = category.Key.CategoryId,
                categorySortId = index,
                categoryName = category.Key.CategoryName,
                categoryMbCount = category.Count(),
                buddyList = category.Select(FriendInfo).ToArray(),
            }).ToArray();
    }

    private async Task<object?> SetProfileAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (OptionalString(p, "nickname") is { } nickname) await context.SetNicknameAsync(nickname);
        if (OptionalString(p, "personal_note") is { } bio) await context.SetBioAsync(bio);
        return null;
    }

    private static object GetQqAvatar(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (OptionalId(p, "user_id") is long userId) return new { url = $"https://thirdqq.qlogo.cn/g?b=qq&nk={userId}&s=640" };
        if (OptionalId(p, "group_id") is long groupId) return new { url = $"https://p.qlogo.cn/gh/{groupId}/{groupId}/0" };
        throw new OneBotParameterException("user_id or group_id is required");
    }

    private async Task<object?> GetGroupSystemMessagesAsync()
    {
        var notifications = await context.GetGroupNotificationsAsync(false, 100);
        var invited = new List<object>();
        var joins = new List<object>();
        foreach (var notification in notifications)
        {
            switch (notification)
            {
                case QInvitedJoinRequestNotification request:
                    invited.Add(new
                    {
                        request_id = request.NotificationSeq,
                        invitor_uin = request.InitiatorId,
                        invitor_nick = await TryNameAsync(() => context.GetUserNicknameAsync(request.InitiatorId)),
                        group_id = request.GroupId,
                        group_name = await TryNameAsync(() => context.GetGroupNameAsync(request.GroupId)),
                        @checked = request.State != QRequestState.Pending,
                        actor = request.OperatorId ?? 0,
                    });
                    break;
                case QJoinRequestNotification request:
                    joins.Add(new
                    {
                        request_id = request.NotificationSeq,
                        requester_uin = request.InitiatorId,
                        requester_nick = await TryNameAsync(() => context.GetUserNicknameAsync(request.InitiatorId)),
                        message = request.Comment ?? string.Empty,
                        group_id = request.GroupId,
                        group_name = await TryNameAsync(() => context.GetGroupNameAsync(request.GroupId)),
                        @checked = request.State != QRequestState.Pending,
                        actor = request.OperatorId ?? 0,
                    });
                    break;
            }
        }
        return new { invited_requests = invited, join_requests = joins };
    }

    private async Task<object?> GetIgnoredGroupRequestsAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        var groupId = OptionalId(p, "group_id");
        var requests = await context.GetGroupNotificationsAsync(true, NonNegativeInt(p, "count", 50));
        return requests.OfType<QJoinRequestNotification>()
            .Where(request => groupId is null || request.GroupId == groupId)
            .Select(request => new
            {
                group_id = request.GroupId,
                user_id = request.InitiatorId,
                flag = EncodeFlag(new RequestFlag("group", request.GroupId, request.NotificationSeq, Filtered: true, RequestType: "join_request")),
            }).ToArray();
    }

    private async Task<object?> GetDoubtFriendRequestsAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        var requests = await context.GetFriendRequestsAsync(NonNegativeInt(p, "count", 50), true);
        return await Task.WhenAll(requests.Select(async request => new
        {
            flag = request.InitiatorUid,
            uin = request.InitiatorId.ToString(),
            nick = await TryNameAsync(() => context.GetUserNicknameAsync(request.InitiatorId)),
            source = request.Via ?? string.Empty,
            reason = string.Empty,
            msg = request.Comment ?? string.Empty,
            group_code = string.Empty,
            time = request.Time.ToUnixTimeSeconds().ToString(),
            type = "doubt",
        }));
    }

    private async Task<object?> SetDoubtFriendRequestAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        var flag = OneBotParameters.String(p, "flag");
        if (flag.StartsWith("shiro.", StringComparison.Ordinal))
        {
            var request = DecodeFlag(p);
            if (request.Kind != "friend" || !request.Filtered) throw new OneBotParameterException("flag is not a filtered friend request flag");
            flag = request.InitiatorUid!;
        }
        await context.AcceptFriendRequestAsync(flag, true);
        return null;
    }

    private async Task<object?> BatchDeleteGroupMembersAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (!p.TryGetValue("user_ids", out var users) || users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0)
            throw new OneBotParameterException("user_ids must be a non-empty array");
        var groupId = Id(p, "group_id");
        foreach (var user in users.EnumerateArray())
        {
            var item = new Dictionary<string, JsonElement> { ["user_id"] = user.Clone() };
            await context.SetGroupKickAsync(groupId, Id(item, "user_id"), false);
        }
        return null;
    }

    private async Task<object?> SendForwardAsync(IReadOnlyDictionary<string, JsonElement> p, Channel channel)
    {
        var nodesElement = p.TryGetValue("messages", out var messages) ? messages : p.TryGetValue("message", out var message) ? message : default;
        if (nodesElement.ValueKind != JsonValueKind.Array || nodesElement.GetArrayLength() == 0)
            throw new OneBotParameterException("messages must be a non-empty node array");
        var nodes = new List<OneBotForwardNode>();
        foreach (var node in nodesElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var type) || type.GetString() != "node" ||
                !node.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new OneBotParameterException("messages must contain node segments");
            if (data.TryGetProperty("id", out var id))
            {
                var source = MessageReference(new Dictionary<string, JsonElement> { ["message_id"] = id.Clone() });
                nodes.Add(await context.GetForwardNodeAsync(source));
                continue;
            }
            var userId = PropertyId(data, "user_id", "uin");
            var senderName = PropertyString(data, "nickname", "name");
            if (!data.TryGetProperty("content", out var content)) throw new OneBotParameterException("node content is required");
            nodes.Add(new OneBotForwardNode(userId, senderName, ParseOneBotMessage(content),
                data.TryGetProperty("time", out var time) && time.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null));
        }
        var preview = p.TryGetValue("news", out var news) ? ParseForwardPreview(news) : null;
        var nativeId = await context.SendForwardAsync(channel, nodes, OptionalString(p, "source"), preview, OptionalString(p, "summary"), OptionalString(p, "prompt"));
        return new { message_id = await RegisterMessageAsync(channel, nativeId), forward_id = string.Empty };
    }

    private async Task<object?> ForwardSingleAsync(IReadOnlyDictionary<string, JsonElement> p, Channel destination)
    {
        var nativeId = await context.ForwardSingleAsync(MessageReference(p), destination);
        return new { message_id = await RegisterMessageAsync(destination, nativeId) };
    }

    private async Task<object?> GetGroupFileSystemInfoAsync(long groupId)
    {
        var pending = new Stack<string>();
        pending.Push("/");
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        long usedSpace = 0;
        while (pending.TryPop(out var folder))
        {
            if (!folders.Add(folder)) continue;
            if (folders.Count > 10_000) throw new InvalidOperationException("Group file traversal limit exceeded");
            foreach (var entry in await context.GetGroupFileEntriesAsync(groupId, folder))
            {
                if (entry.IsFolder) pending.Push(entry.FileId);
                else if (fileIds.Add(entry.FileId)) usedSpace = checked(usedSpace + entry.FileSize);
            }
        }
        return new { file_count = fileIds.Count, limit_count = 0, used_space = usedSpace, total_space = 0 };
    }

    private async Task<object> GetGroupFileUrlAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        var fileId = FirstString(p, ["file_id", "file"]);
        var groupId = OptionalId(p, "group_id") ?? ResolveRememberedGroupFile(fileId)?.GroupId
            ?? throw new OneBotParameterException("group_id is required for get_group_file_url when the file id is not remembered.");
        var url = await ResolveGroupFileUrlAsync(groupId, fileId).ConfigureAwait(false);
        return new { url };
    }

    private GroupFileReference? ResolveRememberedGroupFile(string fileId) =>
        state is not null && state.GroupFiles.TryResolve(fileId, out var reference) ? reference : null;

    private async Task<string> ResolveGroupFileUrlAsync(long groupId, string fileId)
    {
        try
        {
            var url = await context.GetGroupFileUrlAsync(groupId, fileId).ConfigureAwait(false);
            BotLog.Log($"[OneBot/File] get_group_file_url 成功，group_id={groupId}，file_id={SafeFileId(fileId)}。");
            return url;
        }
        catch (NotSupportedException exception)
        {
            BotLog.Warning($"[OneBot/File] 当前 Adapter 不支持获取群文件链接，group_id={groupId}，file_id={SafeFileId(fileId)}：{exception.Message}");
            throw;
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot/File] 获取群文件链接失败，group_id={groupId}，file_id={SafeFileId(fileId)}，{exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private async Task<object?> GetFileAsync(IReadOnlyDictionary<string, JsonElement> p, CancellationToken cancellationToken)
    {
        var fileId = FirstString(p, ["file", "file_id"]);
        var requestedName = p.ContainsKey("name") || p.ContainsKey("file_name") ? FirstString(p, ["name", "file_name"]) : null;
        var download = Bool(p, "download", true);
        string url;
        PrivateFileReference? remembered = null;
        var rememberedGroupFile = ResolveRememberedGroupFile(fileId);
        if (OptionalId(p, "group_id") is not null || rememberedGroupFile is not null)
        {
            var group = OptionalId(p, "group_id") ?? rememberedGroupFile!.GroupId;
            url = await ResolveGroupFileUrlAsync(group, fileId).ConfigureAwait(false);
            if (!download)
            {
                var inlineName = requestedName ?? rememberedGroupFile?.FileName ?? Path.GetFileName(url);
                BotLog.Log($"[OneBot/File] get_file(download=false) 返回链接，group_id={group}，file_id={SafeFileId(fileId)}。");
                return new { file = string.Empty, url, file_size = (rememberedGroupFile?.FileSize ?? 0).ToString(), file_name = inlineName };
            }
        }
        else if (state?.Files.TryResolve(fileId, out remembered) == true || p.ContainsKey("user_id") || p.ContainsKey("file_hash") || p.ContainsKey("hash"))
        {
            var privateFile = remembered ?? ResolvePrivateFile(p, fileId);
            url = await context.GetPrivateFileUrlAsync(privateFile.UserId, fileId, privateFile.FileHash, privateFile.IsSelfSend).ConfigureAwait(false);
        }
        else if (files.TryGetStoredFile(fileId, out var stored))
        {
            BotLog.Log($"[OneBot/File] get_file 命中本地缓存，file_id={SafeFileId(fileId)}。");
            return new { file = download ? stored : string.Empty, url = string.Empty, file_size = new FileInfo(stored).Length.ToString(), file_name = Path.GetFileName(stored) };
        }
        else if (Uri.TryCreate(fileId, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https") url = fileId;
        else url = await context.GetResourceUrlAsync(fileId).ConfigureAwait(false);

        var result = await files.StoreFromUrlAsync(url, requestedName, download, cancellationToken).ConfigureAwait(false);
        BotLog.Log($"[OneBot/File] get_file 完成，file_id={SafeFileId(fileId)}，url={(string.IsNullOrWhiteSpace(url) ? "(empty)" : "ok")}，file={result.File}。");
        return new { file = result.File, url, file_size = result.Size.ToString(), file_name = result.Name };
    }

    private static string SafeFileId(string fileId) => fileId.Length <= 160 ? fileId : fileId[..160] + "...";

    private async Task<int> RegisterMessageAsync(Channel channel, string nativeId)
    {
        if (state is null) return int.TryParse(nativeId, out var raw) ? raw : throw new InvalidOperationException("The active adapter returned a non-numeric QQ message reference.");
        if (!long.TryParse(channel.Id, out var peerId) || !long.TryParse(nativeId, out var sequence))
            throw new InvalidOperationException("The active adapter returned a non-numeric QQ message reference.");
        return await state.Messages.RegisterAsync(new MessageReference(Scene(channel), peerId, sequence));
    }

    private string EncodeFlag(RequestFlag flag) => RequestFlagCodec.Encode(flag, state?.RequestFlagKey ?? throw new NotSupportedException("Request flags require an initialized OneBot runtime."));

    private static async Task<string> TryNameAsync(Func<Task<string>> resolve)
    {
        try { return await resolve(); }
        catch { return string.Empty; }
    }

    private static object FriendInfo(QFriend friend) => new
    {
        user_id = friend.UserId, nickname = friend.Nickname, remark = friend.Remark ?? string.Empty,
        sex = friend.Sex switch { QSex.Male => "male", QSex.Female => "female", _ => "unknown" }, age = 0,
        birthday_year = 0, birthday_month = 0, birthday_day = 0, qid = friend.Qid ?? string.Empty, long_nick = string.Empty,
    };

    private static object MemberInfo(QGroupMember member) => new
    {
        group_id = member.GroupId, user_id = member.UserId, nickname = member.Nickname, card = member.Card ?? string.Empty,
        card_or_nickname = member.DisplayName, sex = member.Sex switch { QSex.Male => "male", QSex.Female => "female", _ => "unknown" },
        age = 0, area = string.Empty, join_time = member.JoinTime?.ToUnixTimeSeconds() ?? 0,
        last_sent_time = member.LastSentTime?.ToUnixTimeSeconds() ?? 0, level = member.Level.ToString(), qq_level = 0,
        role = member.Role.ToString().ToLowerInvariant(), unfriendly = false, title = member.Title ?? string.Empty,
        title_expire_time = 0, card_changeable = false, shut_up_timestamp = member.ShutUpEndTime?.ToUnixTimeSeconds() ?? 0, is_robot = false,
    };

    private static IReadOnlyList<OneBotSegment> ParseOneBotMessage(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => CqCode.Parse(value.GetString() ?? string.Empty),
        JsonValueKind.Array => value.EnumerateArray().Select(ParseSegment).ToArray(),
        JsonValueKind.Object => [ParseSegment(value)],
        _ => throw new OneBotParameterException("node content must be a string, segment, or segment array"),
    };

    private static long PropertyId(JsonElement data, string primary, string alias)
    {
        if (!data.TryGetProperty(primary, out var value) && !data.TryGetProperty(alias, out value)) throw new OneBotParameterException($"node {primary} is required");
        return OneBotParameters.RequiredInt(new Dictionary<string, JsonElement> { [primary] = value.Clone() }, primary);
    }

    private static string PropertyString(JsonElement data, string primary, string alias)
    {
        if (!data.TryGetProperty(primary, out var value) && !data.TryGetProperty(alias, out value)) throw new OneBotParameterException($"node {primary} is required");
        return OneBotParameters.String(new Dictionary<string, JsonElement> { [primary] = value.Clone() }, primary);
    }

    private static IReadOnlyList<string> ParseForwardPreview(JsonElement news)
    {
        if (news.ValueKind != JsonValueKind.Array) throw new OneBotParameterException("news must be an array");
        return news.EnumerateArray().Select((item, index) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()!
            : throw new OneBotParameterException($"news[{index}].text must be a string")).ToArray();
    }

    private static IReadOnlyDictionary<string, string>? DownloadHeaders(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (!p.TryGetValue("headers", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var entries = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Split(["[\\r\\n]", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) => value.EnumerateArray().Select(item => item.GetString()!).ToArray(),
            _ => throw new OneBotParameterException("headers must be a string or string array"),
        };
        if (entries.Length > 64) throw new OneBotParameterException("headers contains too many entries");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Length > 8192) throw new OneBotParameterException("header entry is too long");
            var colon = entry.IndexOf(':');
            var equals = entry.IndexOf('=');
            var separator = colon >= 0 ? colon : equals;
            var name = (separator < 0 ? entry : entry[..separator]).Trim();
            var headerValue = separator < 0 ? string.Empty : entry[(separator + 1)..].Trim();
            if (name.Length == 0 || name.Any(character => !char.IsLetterOrDigit(character) && character is not ('!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')) || headerValue.IndexOfAny(['\r', '\n']) >= 0)
                throw new OneBotParameterException("headers contains an invalid entry");
            result[name] = headerValue;
        }
        return result;
    }

    private async Task<object> SendAsync(Channel channel, IReadOnlyList<MessageSegment> message)
    {
        message = ResolveReplies(message);
        var nativeId = await context.SendAsync(channel, message);
        if (state is null) return new { message_id = nativeId };
        if (!long.TryParse(channel.Id, out var peerId) || !long.TryParse(nativeId, out var sequence))
            throw new InvalidOperationException("The active adapter returned a non-numeric QQ message reference.");
        var messageId = await state.Messages.RegisterAsync(new MessageReference(Scene(channel), peerId, sequence));
        return new { message_id = messageId };
    }

    private async Task<string> GetCookiesAsync(string domain)
    {
        Task<string> fetch;
        lock (_credentialLock)
        {
            if (_cookieCache.TryGetValue(domain, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Value;

            if (cached?.Pending is not null) fetch = cached.Pending;
            else
            {
                fetch = FetchCookiesAsync(domain);
                _cookieCache[domain] = new CachedCredential<string>(string.Empty, DateTimeOffset.MinValue, fetch);
            }
        }

        var cookies = await fetch.ConfigureAwait(false);
        lock (_credentialLock)
            _cookieCache[domain] = new CachedCredential<string>(cookies, DateTimeOffset.UtcNow.Add(_credentialCacheDuration));
        return cookies;
    }

    private async Task<string> FetchCookiesAsync(string domain)
    {
        try
        {
            return await context.GetCookiesAsync(domain).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // OneBot clients probe domains that some QQ implementations do not permit.
            BotLog.Warning($"[OneBot] get_cookies 获取失败，domain='{SafeDomain(domain)}'，{exception.GetType().Name}: {exception.Message}；5 分钟内返回空 Cookie。");
            return string.Empty;
        }
    }

    private async Task<long> GetCsrfTokenAsync()
    {
        Task<string> fetch;
        lock (_credentialLock)
        {
            if (_csrfCache is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Value;
            fetch = _csrfFetch ??= context.GetCsrfTokenAsync();
        }

        long token;
        try
        {
            token = Csrf(await fetch.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            BotLog.Warning($"[OneBot] get_csrf_token 获取失败，{exception.GetType().Name}: {exception.Message}；5 分钟内返回 0。");
            token = 0;
        }

        lock (_credentialLock)
        {
            _csrfFetch = null;
            _csrfCache = new CachedCredential<long>(token, DateTimeOffset.UtcNow.Add(_credentialCacheDuration));
        }
        return token;
    }

    private sealed record CachedCredential<T>(T Value, DateTimeOffset ExpiresAt, Task<T>? Pending = null);

    private static string SafeDomain(string domain) => domain.Length <= 120 ? domain : domain[..120] + "...";

    private static (string Action, bool Async, bool RateLimited) Normalize(string action)
    {
        var async = false;
        var limited = false;
        var changed = true;
        while (changed)
        {
            changed = false;
            if (action.EndsWith("_async", StringComparison.Ordinal)) { action = action[..^6]; async = true; changed = true; }
            if (action.EndsWith("_rate_limited", StringComparison.Ordinal)) { action = action[..^13]; limited = true; changed = true; }
        }
        return (action, async, limited);
    }

    private RequestFlag DecodeFlag(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (state is null) throw new NotSupportedException("Request flags require an initialized OneBot runtime.");
        try { return RequestFlagCodec.Decode(OneBotParameters.String(p, "flag"), state.RequestFlagKey); }
        catch (ArgumentException exception) { throw new OneBotParameterException(exception.Message); }
    }

    private MessageReference MessageReference(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (state is null) throw new NotSupportedException("Message references require an initialized OneBot runtime.");
        var messageId = Id(p, "message_id");
        if (messageId is < 1 or > int.MaxValue || !state.Messages.TryResolve((int)messageId, out var reference))
            throw new OneBotParameterException("message_id is unknown or expired");
        return reference!;
    }

    private MessageReference GroupMessageReference(IReadOnlyDictionary<string, JsonElement> p)
    {
        var reference = MessageReference(p);
        return reference.Scene == MessageScene.Group
            ? reference
            : throw new OneBotParameterException("message_id does not identify a group message");
    }

    private IReadOnlyList<MessageSegment> ResolveReplies(IReadOnlyList<MessageSegment> message)
    {
        if (state is null) return message;
        return message.Select(segment => segment is QuoteSegment quote && int.TryParse(quote.MessageId, out var id) && state.Messages.TryResolve(id, out var reference)
            ? new QuoteSegment(reference!.Sequence.ToString())
            : segment).ToArray();
    }

    private PrivateFileReference ResolvePrivateFile(IReadOnlyDictionary<string, JsonElement> p, string fileId)
    {
        if (state?.Files.TryResolve(fileId, out var reference) == true) return reference!;
        if (!p.ContainsKey("user_id")) throw new OneBotParameterException("user_id is required for an unknown private file");
        return new PrivateFileReference(Id(p, "user_id"), FirstString(p, ["file_hash", "hash"], ""), Bool(p, "is_self_send"));
    }

    private async Task<object?> GetEventsAsync(IReadOnlyDictionary<string, JsonElement> p, CancellationToken cancellationToken)
    {
        if (state is null) throw new NotSupportedException("The event queue is not initialized.");
        var cursor = FirstString(p, ["consumer", "cursor"], "default");
        var timeout = NonNegativeInt(p, "timeout", 0);
        return await state.Events.FetchAsync(cursor, TimeSpan.FromMilliseconds(timeout), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> HandleQuickOperationAsync(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (!p.TryGetValue("context", out var eventJson) || eventJson.ValueKind != JsonValueKind.Object ||
            !p.TryGetValue("operation", out var operation) || operation.ValueKind != JsonValueKind.Object)
            throw new OneBotParameterException("context and operation must be objects");

        if (operation.TryGetProperty("reply", out var reply) && reply.ValueKind is not JsonValueKind.Null and not JsonValueKind.False)
        {
            var target = eventJson.TryGetProperty("group_id", out var groupId)
                ? Channel.Group(groupId.ToString())
                : Channel.Direct(eventJson.GetProperty("user_id").ToString());
            var replyParameters = new Dictionary<string, JsonElement>
            {
                ["message"] = reply.Clone(),
            };
            if (operation.TryGetProperty("auto_escape", out var autoEscape)) replyParameters["auto_escape"] = autoEscape.Clone();
            if (operation.TryGetProperty("at_sender", out var atSender) && atSender.ValueKind == JsonValueKind.True && eventJson.TryGetProperty("user_id", out var userId))
            {
                using var message = JsonDocument.Parse($"[{{\"type\":\"at\",\"data\":{{\"qq\":\"{userId}\"}}}},{{\"type\":\"text\",\"data\":{{\"text\":{JsonSerializer.Serialize(reply.ToString())}}}}}]");
                replyParameters["message"] = message.RootElement.Clone();
            }
            await SendAsync(target, Message(replyParameters)).ConfigureAwait(false);
        }

        if (operation.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.True && eventJson.TryGetProperty("message_id", out var messageId))
        {
            var deleteParameters = new Dictionary<string, JsonElement> { ["message_id"] = messageId.Clone() };
            var reference = MessageReference(deleteParameters);
            await context.DeleteAsync(reference.Sequence.ToString(), ToChannel(reference)).ConfigureAwait(false);
        }
        if (eventJson.TryGetProperty("group_id", out var eventGroupId) && eventJson.TryGetProperty("user_id", out var eventUserId))
        {
            var groupId = eventGroupId.GetInt64();
            var userId = eventUserId.GetInt64();
            if (operation.TryGetProperty("kick", out var kick) && kick.ValueKind == JsonValueKind.True)
                await context.SetGroupKickAsync(groupId, userId, operation.TryGetProperty("reject_add_request", out var reject) && reject.ValueKind == JsonValueKind.True).ConfigureAwait(false);
            if (operation.TryGetProperty("ban", out var ban) && ban.ValueKind == JsonValueKind.True)
            {
                var seconds = operation.TryGetProperty("ban_duration", out var duration) && duration.TryGetInt32(out var value) ? value : 1800;
                if (seconds < 0) throw new OneBotParameterException("ban_duration must be a non-negative integer");
                await context.SetGroupBanAsync(groupId, userId, TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            }
        }
        if (operation.TryGetProperty("approve", out var approve) && approve.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            eventJson.TryGetProperty("flag", out var flag))
        {
            var request = DecodeFlag(new Dictionary<string, JsonElement> { ["flag"] = flag.Clone() });
            var accepted = approve.GetBoolean();
            if (request.Kind == "friend")
                await context.SetFriendRequestAsync(request, accepted, operation.TryGetProperty("remark", out var remark) ? remark.ToString() : null).ConfigureAwait(false);
            else
                await context.SetGroupRequestAsync(request, accepted, operation.TryGetProperty("reason", out var reason) ? reason.ToString() : null).ConfigureAwait(false);
        }
        return null;
    }

    private static MessageScene Scene(Channel channel) => channel.Type switch
    {
        ChannelType.Group => MessageScene.Group,
        ChannelType.Direct => MessageScene.Friend,
        _ => MessageScene.Temp,
    };

    private static Channel ToChannel(MessageReference reference) => reference.Scene == MessageScene.Group
        ? Channel.Group(reference.PeerId.ToString())
        : Channel.Direct(reference.PeerId.ToString());

    private static object? RewriteMessageId(object? value, long messageId)
    {
        if (value is null) return null;
        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Object) return value;
        var result = element.EnumerateObject().ToDictionary(property => property.Name, property => JsonValue(property.Value));
        result["message_id"] = messageId;
        result["real_id"] = messageId;
        return result;
    }

    private string? ResolveOptionalSequence(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (OptionalId(p, "message_seq") is long sequence) return sequence.ToString();
        if (OptionalId(p, "message_id") is not long messageId) return null;
        var parameters = new Dictionary<string, JsonElement> { ["message_id"] = JsonSerializer.SerializeToElement(messageId) };
        return MessageReference(parameters).Sequence.ToString();
    }

    private async Task<object?> RewriteMessageCollectionAsync(object? value, Channel channel)
    {
        if (state is null || value is null || !long.TryParse(channel.Id, out var peerId)) return value;
        var root = JsonSerializer.SerializeToElement(value);
        var messages = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var nested) ? nested : default;
        if (messages.ValueKind != JsonValueKind.Array) return value;
        var rewritten = new List<object?>();
        foreach (var message in messages.EnumerateArray())
        {
            var item = message.EnumerateObject().ToDictionary(property => property.Name, property => JsonValue(property.Value));
            if (message.TryGetProperty("message_id", out var nativeId) && long.TryParse(nativeId.ToString(), out var sequence))
            {
                var id = await state.Messages.RegisterAsync(new MessageReference(Scene(channel), peerId, sequence)).ConfigureAwait(false);
                item["message_id"] = id;
                if (item.ContainsKey("real_id")) item["real_id"] = id;
            }
            rewritten.Add(item);
        }
        if (root.ValueKind == JsonValueKind.Array) return rewritten;
        var result = root.EnumerateObject().ToDictionary(property => property.Name, property => JsonValue(property.Value));
        result["messages"] = rewritten;
        return result;
    }

    private static long Id(IReadOnlyDictionary<string, JsonElement> p, string name) => OneBotParameters.RequiredInt(p, name);
    private static long? OptionalId(IReadOnlyDictionary<string, JsonElement> p, string name) => OneBotParameters.OptionalInt(p, name);
    private static int NonNegativeInt(IReadOnlyDictionary<string, JsonElement> p, string name, int fallback)
    {
        var value = OneBotParameters.OptionalInt(p, name) ?? fallback;
        return value is >= 0 and <= int.MaxValue ? (int)value : throw new OneBotParameterException($"{name} must be a non-negative integer");
    }
    private static int Csrf(string value) => int.TryParse(value, out var token) ? token : throw new InvalidOperationException("The adapter returned a non-numeric CSRF token.");
    private static bool Bool(IReadOnlyDictionary<string, JsonElement> p, string name, bool fallback = false)
    {
        if (!p.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || value.ValueKind == JsonValueKind.String && value.GetString() == "") return fallback;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is "yes" or "on") return true;
            if (value.GetString() is "no" or "off") return false;
        }
        return OneBotParameters.Bool(p, name, fallback);
    }
    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? OneBotParameters.String(p, name) : null;
    private static string FirstString(IReadOnlyDictionary<string, JsonElement> p, IReadOnlyList<string> names, string? fallback = null)
    {
        foreach (var name in names)
            if (p.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined && value.ToString().Length > 0)
                return OneBotParameters.String(p, name);
        return fallback ?? throw new OneBotParameterException($"{names[0]} must be a string");
    }
    private static async Task<object?> Void(Task task) { await task; return null; }

    private static IReadOnlyList<MessageSegment> Message(IReadOnlyDictionary<string, JsonElement> p)
    {
        if (!p.TryGetValue("message", out var value)) throw new OneBotParameterException("message must be supplied");
        IReadOnlyList<OneBotSegment> segments = value.ValueKind switch
        {
            JsonValueKind.String when Bool(p, "auto_escape") => [OneBotSegment.Text(value.GetString() ?? string.Empty)],
            JsonValueKind.String => CqCode.Parse(value.GetString() ?? string.Empty),
            JsonValueKind.Array => value.EnumerateArray().Select(ParseSegment).ToArray(),
            JsonValueKind.Object => [ParseSegment(value)],
            _ => throw new OneBotParameterException("message must be a string, segment, or segment array"),
        };
        try { return MessageSegments.ToGeneric(segments); }
        catch (ArgumentException exception) { throw new OneBotParameterException(exception.Message); }
    }

    private static OneBotSegment ParseSegment(JsonElement value)
    {
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new OneBotParameterException("message segment must contain string type and object data");
        return new OneBotSegment(type.GetString()!, data.EnumerateObject().ToDictionary(property => property.Name, property => JsonValue(property.Value)));
    }

    private static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };

    private static Channel Target(IReadOnlyDictionary<string, JsonElement> p)
    {
        var messageType = OptionalString(p, "message_type");
        if (messageType is not null && !string.Equals(messageType, "group", StringComparison.OrdinalIgnoreCase) && !string.Equals(messageType, "private", StringComparison.OrdinalIgnoreCase))
            throw new OneBotParameterException("message_type must be private or group");
        return string.Equals(messageType, "group", StringComparison.OrdinalIgnoreCase) || (messageType is null && p.ContainsKey("group_id"))
            ? Channel.Group(Id(p, "group_id").ToString())
            : Channel.Direct(Id(p, "user_id").ToString());
    }
}
