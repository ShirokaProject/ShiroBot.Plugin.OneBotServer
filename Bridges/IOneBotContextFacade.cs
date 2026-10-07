using ShiroBot.SDK.Models;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Model.QQ;

namespace ShiroBot.Plugin.OneBotServer.Bridges;

/// <summary>Testable boundary between OneBot actions and host/adapter capabilities.</summary>
public interface IOneBotContextFacade
{
    string StorageDirectory { get; }
    Task<string> SendAsync(Channel channel, IReadOnlyList<MessageSegment> message);
    Task DeleteAsync(string messageId, Channel? channel = null);
    Task<object?> GetAsync(string messageId, Channel? channel = null);
    Task<object?> GetForwardedAsync(string forwardId);
    Task<object?> GetHistoryAsync(Channel channel, string? beforeMessageId, int limit);
    Task MarkAsReadAsync(string messageId, Channel? channel = null);
    Task<object?> GetLoginInfoAsync();
    Task<string> GetSelfIdAsync();
    Task<bool> CanSendImageAsync();
    Task<bool> CanSendRecordAsync();
    Task<object?> GetStatusAsync();
    Task CleanCacheAsync();
    Task<object?> GetStrangerAsync(long userId, bool noCache);
    Task<object?> GetFriendsAsync(bool noCache);
    Task<object?> GetGroupsAsync(bool noCache);
    Task<object?> GetGroupAsync(long groupId, bool noCache);
    Task<object?> GetGroupMembersAsync(long groupId, bool noCache);
    Task<object?> GetGroupMemberAsync(long groupId, long userId, bool noCache);
    Task<string> GetCookiesAsync(string domain);
    Task<string> GetCsrfTokenAsync();
    Task<object?> GetVersionInfoAsync();
    Task<string> GetResourceUrlAsync(string resourceId);
    Task SendLikeAsync(long userId, int count);
    Task DeleteFriendAsync(long userId);
    Task SetGroupNameAsync(long groupId, string name);
    Task SetGroupPortraitAsync(long groupId, string file);
    Task SetGroupCardAsync(long groupId, long userId, string card);
    Task SetGroupAdminAsync(long groupId, long userId, bool enabled);
    Task SetGroupSpecialTitleAsync(long groupId, long userId, string title);
    Task SetGroupBanAsync(long groupId, long userId, TimeSpan duration);
    Task SetGroupWholeBanAsync(long groupId, bool enabled);
    Task SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest);
    Task SetGroupLeaveAsync(long groupId);
    Task SetFriendRequestAsync(RequestFlag request, bool approve, string? remark);
    Task SetGroupRequestAsync(RequestFlag request, bool approve, string? reason);
    Task SendNudgeAsync(long? groupId, long userId);
    Task SetReactionAsync(long groupId, long messageId, string reactionId, bool enabled);
    Task<object?> GetAnnouncementsAsync(long groupId);
    Task SendAnnouncementAsync(long groupId, string content, string? image);
    Task DeleteAnnouncementAsync(long groupId, string noticeId);
    Task<object?> GetEssenceMessagesAsync(long groupId, int pageIndex, int pageSize);
    Task SetEssenceMessageAsync(long groupId, long messageId, bool enabled);
    Task<string> UploadGroupFileAsync(long groupId, string file, string name, string folderId);
    Task<string> UploadPrivateFileAsync(long userId, string file, string name);
    Task<object?> GetGroupFilesAsync(long groupId, string folderId);
    Task<string> GetGroupFileUrlAsync(long groupId, string fileId);
    Task<string> GetPrivateFileUrlAsync(long userId, string fileId, string fileHash, bool isSelfSend);
    Task DeleteGroupFileAsync(long groupId, string fileId);
    Task MoveGroupFileAsync(long groupId, string fileId, string targetFolderId, string parentFolderId);
    Task RenameGroupFileAsync(long groupId, string fileId, string name, string parentFolderId);
    Task PersistGroupFileAsync(long groupId, string fileId);
    Task<string> CreateGroupFolderAsync(long groupId, string name);
    Task RenameGroupFolderAsync(long groupId, string folderId, string name);
    Task DeleteGroupFolderAsync(long groupId, string folderId);
    Task SetAvatarAsync(string file);
    Task<IReadOnlyList<string>> GetCustomFaceUrlsAsync();
    Task<IReadOnlyList<QFriend>> GetFriendEntitiesAsync(bool noCache);
    Task SetNicknameAsync(string nickname);
    Task SetBioAsync(string bio);
    Task<IReadOnlyList<QGroupNotification>> GetGroupNotificationsAsync(bool filtered, int limit);
    Task<IReadOnlyList<QFriendRequest>> GetFriendRequestsAsync(int limit, bool filtered);
    Task AcceptFriendRequestAsync(string initiatorUid, bool filtered);
    Task<IReadOnlyList<QGroupMember>> GetGroupMemberEntitiesAsync(long groupId, bool noCache);
    Task<string> GetUserNicknameAsync(long userId);
    Task<string> GetGroupNameAsync(long groupId);
    Task<string> SendForwardAsync(Channel channel, IReadOnlyList<OneBotForwardNode> nodes, string? title, IReadOnlyList<string>? preview, string? summary, string? prompt);
    Task<string> ForwardSingleAsync(OneBotMessageReference source, Channel destination);
    Task<OneBotForwardNode> GetForwardNodeAsync(OneBotMessageReference source);
    Task<IReadOnlyList<OneBotGroupFileEntry>> GetGroupFileEntriesAsync(long groupId, string folderId);
}

public sealed record OneBotForwardNode(long UserId, string SenderName, IReadOnlyList<OneBotSegment> Content, DateTimeOffset? Time);
public sealed record OneBotGroupFileEntry(string FileId, long FileSize, string ParentFolderId, bool IsFolder);
