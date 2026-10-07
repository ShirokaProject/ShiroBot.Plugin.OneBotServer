using ShiroBot.SDK.Models;
using ShiroBot.Plugin.OneBotServer.Infrastructure;

namespace ShiroBot.Plugin.OneBotServer.Bridges;

/// <summary>Convenient base for dispatcher tests and alternate host bridges.</summary>
public abstract class OneBotContextFacadeBase : IOneBotContextFacade
{
    protected static NotSupportedException Unsupported() => new("The active adapter does not provide this OneBot capability.");
    private static Task Missing() => Task.FromException(Unsupported());
    private static Task<T> Missing<T>() => Task.FromException<T>(Unsupported());

    public virtual string StorageDirectory => Path.Combine(Path.GetTempPath(), "ShiroBot.Plugin.OneBotServer", "storage");

    public virtual Task<string> SendAsync(Channel channel, IReadOnlyList<MessageSegment> message) => Missing<string>();
    public virtual Task DeleteAsync(string messageId, Channel? channel = null) => Missing();
    public virtual Task<object?> GetAsync(string messageId, Channel? channel = null) => Missing<object?>();
    public virtual Task<object?> GetForwardedAsync(string forwardId) => Missing<object?>();
    public virtual Task<object?> GetHistoryAsync(Channel channel, string? beforeMessageId, int limit) => Missing<object?>();
    public virtual Task MarkAsReadAsync(string messageId, Channel? channel = null) => Missing();
    public virtual Task<object?> GetLoginInfoAsync() => Missing<object?>();
    public virtual Task<string> GetSelfIdAsync() => Missing<string>();
    public virtual Task<bool> CanSendImageAsync() => Task.FromResult(true);
    public virtual Task<bool> CanSendRecordAsync() => Task.FromResult(true);
    public virtual Task<object?> GetStatusAsync() => Missing<object?>();
    public virtual Task CleanCacheAsync() => Missing();
    public virtual Task<object?> GetStrangerAsync(long userId, bool noCache) => Missing<object?>();
    public virtual Task<object?> GetFriendsAsync(bool noCache) => Missing<object?>();
    public virtual Task<object?> GetGroupsAsync(bool noCache) => Missing<object?>();
    public virtual Task<object?> GetGroupAsync(long groupId, bool noCache) => Missing<object?>();
    public virtual Task<object?> GetGroupMembersAsync(long groupId, bool noCache) => Missing<object?>();
    public virtual Task<object?> GetGroupMemberAsync(long groupId, long userId, bool noCache) => Missing<object?>();
    public virtual Task<string> GetCookiesAsync(string domain) => Missing<string>();
    public virtual Task<string> GetCsrfTokenAsync() => Missing<string>();
    public virtual Task<object?> GetVersionInfoAsync() => Missing<object?>();
    public virtual Task<string> GetResourceUrlAsync(string resourceId) => Missing<string>();
    public virtual Task SendLikeAsync(long userId, int count) => Missing();
    public virtual Task DeleteFriendAsync(long userId) => Missing();
    public virtual Task SetGroupNameAsync(long groupId, string name) => Missing();
    public virtual Task SetGroupPortraitAsync(long groupId, string file) => Missing();
    public virtual Task SetGroupCardAsync(long groupId, long userId, string card) => Missing();
    public virtual Task SetGroupAdminAsync(long groupId, long userId, bool enabled) => Missing();
    public virtual Task SetGroupSpecialTitleAsync(long groupId, long userId, string title) => Missing();
    public virtual Task SetGroupBanAsync(long groupId, long userId, TimeSpan duration) => Missing();
    public virtual Task SetGroupWholeBanAsync(long groupId, bool enabled) => Missing();
    public virtual Task SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest) => Missing();
    public virtual Task SetGroupLeaveAsync(long groupId) => Missing();
    public virtual Task SetFriendRequestAsync(RequestFlag request, bool approve, string? remark) => Missing();
    public virtual Task SetGroupRequestAsync(RequestFlag request, bool approve, string? reason) => Missing();
    public virtual Task SendNudgeAsync(long? groupId, long userId) => Missing();
    public virtual Task SetReactionAsync(long groupId, long messageId, string reactionId, bool enabled) => Missing();
    public virtual Task<object?> GetAnnouncementsAsync(long groupId) => Missing<object?>();
    public virtual Task SendAnnouncementAsync(long groupId, string content, string? image) => Missing();
    public virtual Task DeleteAnnouncementAsync(long groupId, string noticeId) => Missing();
    public virtual Task<object?> GetEssenceMessagesAsync(long groupId, int pageIndex, int pageSize) => Missing<object?>();
    public virtual Task SetEssenceMessageAsync(long groupId, long messageId, bool enabled) => Missing();
    public virtual Task<string> UploadGroupFileAsync(long groupId, string file, string name, string folderId) => Missing<string>();
    public virtual Task<string> UploadPrivateFileAsync(long userId, string file, string name) => Missing<string>();
    public virtual Task<object?> GetGroupFilesAsync(long groupId, string folderId) => Missing<object?>();
    public virtual Task<string> GetGroupFileUrlAsync(long groupId, string fileId) => Missing<string>();
    public virtual Task<string> GetPrivateFileUrlAsync(long userId, string fileId, string fileHash, bool isSelfSend) => Missing<string>();
    public virtual Task DeleteGroupFileAsync(long groupId, string fileId) => Missing();
    public virtual Task MoveGroupFileAsync(long groupId, string fileId, string targetFolderId, string parentFolderId) => Missing();
    public virtual Task RenameGroupFileAsync(long groupId, string fileId, string name, string parentFolderId) => Missing();
    public virtual Task PersistGroupFileAsync(long groupId, string fileId) => Missing();
    public virtual Task<string> CreateGroupFolderAsync(long groupId, string name) => Missing<string>();
    public virtual Task RenameGroupFolderAsync(long groupId, string folderId, string name) => Missing();
    public virtual Task DeleteGroupFolderAsync(long groupId, string folderId) => Missing();
    public virtual Task SetAvatarAsync(string file) => Missing();
    public virtual Task<IReadOnlyList<string>> GetCustomFaceUrlsAsync() => Missing<IReadOnlyList<string>>();
    public virtual Task<IReadOnlyList<ShiroBot.Model.QQ.QFriend>> GetFriendEntitiesAsync(bool noCache) => Missing<IReadOnlyList<ShiroBot.Model.QQ.QFriend>>();
    public virtual Task SetNicknameAsync(string nickname) => Missing();
    public virtual Task SetBioAsync(string bio) => Missing();
    public virtual Task<IReadOnlyList<ShiroBot.Model.QQ.QGroupNotification>> GetGroupNotificationsAsync(bool filtered, int limit) => Missing<IReadOnlyList<ShiroBot.Model.QQ.QGroupNotification>>();
    public virtual Task<IReadOnlyList<ShiroBot.Model.QQ.QFriendRequest>> GetFriendRequestsAsync(int limit, bool filtered) => Missing<IReadOnlyList<ShiroBot.Model.QQ.QFriendRequest>>();
    public virtual Task AcceptFriendRequestAsync(string initiatorUid, bool filtered) => Missing();
    public virtual Task<IReadOnlyList<ShiroBot.Model.QQ.QGroupMember>> GetGroupMemberEntitiesAsync(long groupId, bool noCache) => Missing<IReadOnlyList<ShiroBot.Model.QQ.QGroupMember>>();
    public virtual Task<string> GetUserNicknameAsync(long userId) => Missing<string>();
    public virtual Task<string> GetGroupNameAsync(long groupId) => Missing<string>();
    public virtual Task<string> SendForwardAsync(Channel channel, IReadOnlyList<OneBotForwardNode> nodes, string? title, IReadOnlyList<string>? preview, string? summary, string? prompt) => Missing<string>();
    public virtual Task<string> ForwardSingleAsync(OneBotMessageReference source, Channel destination) => Missing<string>();
    public virtual Task<OneBotForwardNode> GetForwardNodeAsync(OneBotMessageReference source) => Missing<OneBotForwardNode>();
    public virtual Task<IReadOnlyList<OneBotGroupFileEntry>> GetGroupFileEntriesAsync(long groupId, string folderId) => Missing<IReadOnlyList<OneBotGroupFileEntry>>();
}
