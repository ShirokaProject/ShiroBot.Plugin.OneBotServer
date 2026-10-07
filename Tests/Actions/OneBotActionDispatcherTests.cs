using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShiroBot.Plugin.OneBotServer.Actions;
using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Tests.Actions;

[TestClass]
public sealed class OneBotActionDispatcherTests
{
    [TestMethod]
    public void Actions_RegistersExactlyThe123ReferenceDispatcherActions()
    {
        var dispatcher = new OneBotActionDispatcher(new RecordingFacade());

        Assert.HasCount(38, dispatcher.StandardActionNames);
        Assert.HasCount(123, dispatcher.Actions);
        CollectionAssert.Contains(dispatcher.StandardActionNames.ToArray(), "send_like");
        CollectionAssert.DoesNotContain(dispatcher.StandardActionNames.ToArray(), "set_group_portrait");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "set_group_portrait");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "get_group_msg_history");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "rename_group_file");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "get_group_album_list");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "download_file");
        CollectionAssert.Contains(dispatcher.Actions.ToArray(), "get_guild_list");
    }

    [TestMethod]
    public async Task UnsupportedExtendedActions_AreAllRegisteredAndReturn1404()
    {
        string[] actions =
        [
            "get_recommend_group_face", "get_ai_record", "get_group_ai_record", "send_group_ai_record", "get_ai_characters",
            "voice_msg_to_text", "send_pb", "set_config", "get_config", "llonebot_debug", "scan_qrcode", "get_rkey",
            "get_flash_file_info", "download_flash_file", "upload_flash_file", "reshare_flash_file", "get_group_album_list",
            "upload_group_album", "get_group_album_media_list", "create_group_album", "delete_group_album",
            "get_robot_uin_range", "set_online_status", "set_input_status", "get_profile_like", "get_profile_like_me",
            "set_friend_category", "set_friend_remark", "set_group_msg_mask", "set_group_remark",
            "get_group_at_all_remain", "send_group_sign", "ocr_image",
        ];
        var dispatcher = new OneBotActionDispatcher(new RecordingFacade());

        foreach (var action in actions)
        {
            CollectionAssert.Contains(dispatcher.Actions.ToArray(), action);
            Assert.AreEqual(1404, (await dispatcher.DispatchAsync(Request(action, "{}"))).RetCode, action);
        }
    }

    [TestMethod]
    public async Task ExtendedProfileFriendModerationAvatarAndGuildActions_MapToFacade()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        await dispatcher.DispatchAsync(Request("set_qq_avatar", """{"file":"avatar.png"}"""));
        await dispatcher.DispatchAsync(Request("set_qq_profile", """{"nickname":"bot","personal_note":"bio"}"""));
        var faces = await dispatcher.DispatchAsync(Request("fetch_custom_face", """{"count":1}"""));
        var friends = await dispatcher.DispatchAsync(Request("get_friends_with_category", "{}"));
        await dispatcher.DispatchAsync(Request("batch_delete_group_member", """{"group_id":8,"user_ids":[2,"3"]}"""));
        var shut = await dispatcher.DispatchAsync(Request("get_group_shut_list", """{"group_id":8}"""));
        var avatar = await dispatcher.DispatchAsync(Request("get_qq_avatar", """{"user_id":42}"""));
        var guilds = await dispatcher.DispatchAsync(Request("get_guild_list", "{}"));

        Assert.AreEqual("avatar.png", facade.Avatar);
        Assert.AreEqual(("bot", "bio"), facade.Profile);
        CollectionAssert.AreEqual(new[] { "face-1" }, ((IEnumerable<string>)faces.Data!).ToArray());
        Assert.AreEqual(2, JsonSerializer.SerializeToElement(friends.Data)[0].GetProperty("categoryMbCount").GetInt32());
        CollectionAssert.AreEqual(new[] { 2L, 3L }, facade.KickedUsers.ToArray());
        Assert.HasCount(1, JsonSerializer.SerializeToElement(shut.Data).EnumerateArray().ToArray());
        Assert.AreEqual("https://thirdqq.qlogo.cn/g?b=qq&nk=42&s=640", JsonSerializer.SerializeToElement(avatar.Data).GetProperty("url").GetString());
        Assert.IsNull(guilds.Data);
    }

    [TestMethod]
    public async Task ForwardAndReactionActions_UseRegisteredReferencesAndFacade()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade { StoragePath = directory };
        var state = await CreateStateAsync(directory);
        var sourceId = await state.Messages.RegisterAsync(new(MessageScene.Group, 8, 9));
        state.Reactions.Update(8, 9, "475", 2, true);
        var dispatcher = new OneBotActionDispatcher(facade, state);

        var forwarded = await dispatcher.DispatchAsync(Request("send_group_forward_msg", JsonSerializer.Serialize(new
        {
            group_id = 8,
            messages = new[] { new { type = "node", data = new { id = sourceId } } },
        })));
        var single = await dispatcher.DispatchAsync(Request("forward_friend_single_msg", JsonSerializer.Serialize(new { user_id = 5, message_id = sourceId })));
        var reactions = await dispatcher.DispatchAsync(Request("fetch_emoji_like", JsonSerializer.Serialize(new { message_id = sourceId, emoji_id = 475 })));

        Assert.AreEqual(0, forwarded.RetCode);
        Assert.AreEqual(0, single.RetCode);
        Assert.AreEqual(Channel.Group("8"), facade.ForwardChannel);
        Assert.AreEqual(2L, facade.ForwardNodes.Single().UserId);
        Assert.AreEqual((new OneBotMessageReference(MessageScene.Group, 8, 9), Channel.Direct("5")), facade.SingleForward);
        Assert.AreEqual("2", JsonSerializer.SerializeToElement(reactions.Data).GetProperty("emojiLikesList")[0].GetProperty("tinyId").GetString());
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task FileActions_StoreDownloadsInsidePluginStorageAndCalculateRecursiveUsage()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade { StoragePath = directory };
        var dispatcher = new OneBotActionDispatcher(facade);

        var download = await dispatcher.DispatchAsync(Request("download_file", """{"base64":"aGVsbG8=","name":"safe.txt","headers":["X-Test: value"]}"""));
        var stored = JsonSerializer.SerializeToElement(download.Data).GetProperty("file").GetString()!;
        var get = await dispatcher.DispatchAsync(Request("get_file", """{"file":"safe.txt"}"""));
        var usage = await dispatcher.DispatchAsync(Request("get_group_file_system_info", """{"group_id":8}"""));

        Assert.IsTrue(Path.GetFullPath(stored).StartsWith(Path.GetFullPath(Path.Combine(directory, "files")) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.AreEqual("hello", await File.ReadAllTextAsync(stored));
        Assert.AreEqual("5", JsonSerializer.SerializeToElement(get.Data).GetProperty("file_size").GetString());
        Assert.AreEqual(12L, JsonSerializer.SerializeToElement(usage.Data).GetProperty("used_space").GetInt64());
        Assert.AreEqual(2, JsonSerializer.SerializeToElement(usage.Data).GetProperty("file_count").GetInt32());
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task SendGroupMessage_ConvertsStringIdentifiersCqCodeAndSegmentMessages()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        var cqResponse = await dispatcher.DispatchAsync(Request("send_group_msg", """{"group_id":"42","message":"hello[CQ:at,qq=7]"}"""));
        var arrayResponse = await dispatcher.DispatchAsync(Request("send_group_msg", """{"group_id":42,"message":{"type":"text","data":{"text":"again"}}}"""));

        Assert.AreEqual(0, cqResponse.RetCode);
        Assert.AreEqual(0, arrayResponse.RetCode);
        Assert.AreEqual("42", facade.Channel!.Id);
        Assert.AreEqual(ChannelType.Group, facade.Channel.Type);
        Assert.IsInstanceOfType<MentionSegment>(facade.Messages[0][1]);
        Assert.AreEqual("again", ((TextSegment)facade.Messages[1].Single()).Text);
    }

    [TestMethod]
    public async Task AsyncAction_ReturnsImmediatelyAndRunsInBackgroundWhilePreservingEcho()
    {
        var facade = new RecordingFacade { LoginGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var dispatcher = new OneBotActionDispatcher(facade);
        using var echo = JsonDocument.Parse("{\"trace\":7}");

        var response = await dispatcher.DispatchAsync(new OneBotActionRequest("get_login_info_async", Empty(), echo.RootElement.Clone()));

        Assert.AreEqual("async", response.Status);
        Assert.AreEqual(1, response.RetCode);
        Assert.AreEqual(7, response.Echo!.Value.GetProperty("trace").GetInt32());
        Assert.IsFalse(facade.LoginGate.Task.IsCompleted);
        facade.LoginGate.SetResult(new { user_id = 1L });
    }

    [TestMethod]
    public async Task RateLimitedActions_AreQueuedRatherThanRejected()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade, TimeSpan.FromMilliseconds(30));

        var first = await dispatcher.DispatchAsync(Request("get_login_info_rate_limited", "{}"));
        var second = await dispatcher.DispatchAsync(Request("get_login_info_rate_limited_async", "{}"));
        await facade.WaitForLoginCallsAsync(2);

        Assert.AreEqual("async", first.Status);
        Assert.AreEqual("async", second.Status);
        Assert.AreEqual(2, facade.LoginCalls);
        var callTimes = facade.LoginCallTimes.ToArray();
        Assert.IsTrue(callTimes[1] - callTimes[0] >= TimeSpan.FromMilliseconds(20));
    }

    [TestMethod]
    public async Task ParameterConversion_AcceptsNumericStringsAndCommonBooleanStrings()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        var response = await dispatcher.DispatchAsync(Request("set_group_admin", """{"group_id":"10","user_id":20,"enable":"yes"}"""));

        Assert.AreEqual(0, response.RetCode);
        Assert.AreEqual((10L, 20L, true), facade.GroupAdmin);
    }

    [TestMethod]
    public async Task Retcodes_DistinguishBadParametersUnsupportedCapabilitiesAndFailures()
    {
        var facade = new RecordingFacade { Failure = new InvalidOperationException("boom") };
        var dispatcher = new OneBotActionDispatcher(facade);

        var bad = await dispatcher.DispatchAsync(Request("send_group_msg", """{"group_id":"bad","message":"x"}"""));
        var unknown = await dispatcher.DispatchAsync(Request("invent_action", "{}"));
        var unavailable = await dispatcher.DispatchAsync(Request("get_group_honor_info", "{}"));
        var failed = await dispatcher.DispatchAsync(Request("get_login_info", "{}"));

        Assert.AreEqual(1400, bad.RetCode);
        Assert.AreEqual(1404, unknown.RetCode);
        Assert.AreEqual(1404, unavailable.RetCode);
        Assert.AreEqual(1500, failed.RetCode);
    }

    [TestMethod]
    public async Task GroupManagementRequestsCredentialsAndExtensions_AreForwardedToFacade()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        var dispatcher = new OneBotActionDispatcher(facade, state);
        var messageId = await state.Messages.RegisterAsync(new(MessageScene.Group, 1, 3));
        var friendFlag = RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: "123"), state.RequestFlagKey);

        await dispatcher.DispatchAsync(Request("set_group_ban", """{"group_id":1,"user_id":2,"duration":"60"}"""));
        await dispatcher.DispatchAsync(Request("set_friend_add_request", $$"""{"flag":"{{friendFlag}}","approve":"off","remark":456}"""));
        var credentials = await dispatcher.DispatchAsync(Request("get_credentials", """{"domain":123}"""));
        await dispatcher.DispatchAsync(Request("group_poke", """{"group_id":1,"user_id":2}"""));
        await dispatcher.DispatchAsync(Request("set_msg_emoji_like", $$"""{"message_id":{{messageId}},"emoji_id":4,"set":"on"}"""));
        await dispatcher.DispatchAsync(Request("_send_group_notice", """{"group_id":1,"content":2}"""));
        await dispatcher.DispatchAsync(Request("set_essence_msg", $$"""{"message_id":{{messageId}}}"""));

        Assert.AreEqual(TimeSpan.FromSeconds(60), facade.BanDuration);
        Assert.AreEqual(("123", false, "456"), facade.FriendRequest);
        Assert.AreEqual(0, credentials.RetCode);
        Assert.AreEqual((1L, 2L), facade.Nudge);
        Assert.AreEqual((1L, 3L, "4", true), facade.Reaction);
        Assert.AreEqual((1L, "2"), facade.Announcement);
        Assert.AreEqual((1L, 3L, true), facade.Essence);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Credentials_FailureReturnsFallbackAndCachesIt()
    {
        var facade = new RecordingFacade { CredentialFailure = new InvalidOperationException("domain no permission") };
        var dispatcher = new OneBotActionDispatcher(facade);

        var first = await dispatcher.DispatchAsync(Request("get_credentials", """{"domain":"example.test"}"""));
        var second = await dispatcher.DispatchAsync(Request("get_cookies", """{"domain":"example.test"}"""));
        var csrf = await dispatcher.DispatchAsync(Request("get_csrf_token", "{}"));

        Assert.AreEqual(0, first.RetCode);
        Assert.AreEqual(0, second.RetCode);
        Assert.AreEqual(0, csrf.RetCode);
        Assert.AreEqual(1, facade.CookieCalls);
        Assert.AreEqual(1, facade.CsrfCalls);
        StringAssert.Contains(JsonSerializer.Serialize(first.Data), "\"cookies\":\"\"");
        StringAssert.Contains(JsonSerializer.Serialize(first.Data), "\"csrf_token\":0");
    }

    [TestMethod]
    public async Task FileActions_AcceptLLOneBotAliasesAndForwardOperations()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        var upload = await dispatcher.DispatchAsync(Request("upload_group_file", """{"group_id":"8","file_uri":"base64://AA==","file_name":"a.bin","parent_folder_id":"root"}"""));
        await dispatcher.DispatchAsync(Request("move_group_file", """{"group_id":8,"file_id":"f","target_directory":"to","parent_directory":"from"}"""));
        await dispatcher.DispatchAsync(Request("rename_group_file", """{"group_id":8,"file_id":"f","new_file_name":"b.bin"}"""));
        await dispatcher.DispatchAsync(Request("set_group_file_forever", """{"group_id":8,"file_id":"f"}"""));

        Assert.AreEqual(0, upload.RetCode);
        Assert.AreEqual((8L, "base64://AA==", "a.bin", "root"), facade.Upload);
        Assert.AreEqual((8L, "f", "to", "from"), facade.Move);
        Assert.AreEqual((8L, "f", "b.bin", "/"), facade.Rename);
        Assert.AreEqual((8L, "f"), facade.Persist);
    }

    [TestMethod]
    public async Task FileActions_ResolveRememberedGroupAndPrivateFilesWithoutExtraIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        var dispatcher = new OneBotActionDispatcher(facade, state);
        state.GroupFiles.Remember("/group-file", new GroupFileReference(915449089, "283622490.json", 929603));
        state.Files.Remember("/private-file", new PrivateFileReference(20002, "hash", false));

        var groupUrl = await dispatcher.DispatchAsync(Request("get_group_file_url", """{"file_id":"/group-file"}"""));
        var groupFile = await dispatcher.DispatchAsync(Request("get_file", """{"file_id":"/group-file","download":false}"""));
        var privateUrl = await dispatcher.DispatchAsync(Request("get_private_file_url", """{"file_id":"/private-file"}"""));

        Assert.AreEqual(0, groupUrl.RetCode);
        Assert.AreEqual(0, groupFile.RetCode);
        Assert.AreEqual(0, privateUrl.RetCode);
        Assert.AreEqual((915449089L, "/group-file"), facade.GroupFile);
        Assert.AreEqual((20002L, "/private-file", "hash", false), facade.PrivateFile);
        var payload = JsonSerializer.SerializeToElement(groupFile.Data);
        Assert.AreEqual("https://example.test/group", payload.GetProperty("url").GetString());
        Assert.AreEqual("283622490.json", payload.GetProperty("file_name").GetString());
        Assert.AreEqual("929603", payload.GetProperty("file_size").GetString());
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Actions_AcceptLLOneBotParameterAliases()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        var forward = await dispatcher.DispatchAsync(Request("get_forward_msg", """{"message_id":"abc"}"""));
        var poke = await dispatcher.DispatchAsync(Request("send_poke", """{"group_id":5,"user_id":6}"""));
        var groupPoke = facade.Nudge;
        var friendPoke = await dispatcher.DispatchAsync(Request("friend_poke", """{"target_id":7}"""));

        Assert.AreEqual(0, forward.RetCode);
        Assert.AreEqual("abc", facade.ForwardedId);
        Assert.AreEqual(0, poke.RetCode);
        Assert.AreEqual((5L, 6L), groupPoke);
        Assert.AreEqual(0, friendPoke.RetCode);
        Assert.AreEqual((null, 7L), facade.Nudge);
    }

    [TestMethod]
    public async Task SentMessageId_IsPersistedAndResolvedForDeleteAndGet()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        var dispatcher = new OneBotActionDispatcher(facade, state);

        var sent = await dispatcher.DispatchAsync(Request("send_private_msg", """{"user_id":9,"message":"hello"}"""));
        var messageId = JsonSerializer.SerializeToElement(sent.Data).GetProperty("message_id").GetInt32();
        var reopened = await MessageIdRegistry.OpenAsync(Path.Combine(directory, "ids.json"));
        var restarted = new OneBotActionDispatcher(facade, new OneBotRuntimeState(reopened, 100, 10, state.RequestFlagKey));
        await restarted.DispatchAsync(Request("delete_msg", $$"""{"message_id":{{messageId}}}"""));
        await restarted.DispatchAsync(Request("get_msg", $$"""{"message_id":"{{messageId}}"}"""));

        Assert.AreEqual(Channel.Direct("9"), facade.DeleteChannel);
        Assert.AreEqual(Channel.Direct("9"), facade.GetChannel);
        Assert.AreEqual("99", facade.DeletedMessageId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task UnsupportedConversionsRestartAndTemporarySpecialTitle_Return1404()
    {
        var dispatcher = new OneBotActionDispatcher(new RecordingFacade());

        var record = await dispatcher.DispatchAsync(Request("get_record", """{"file":"x","out_format":"mp3"}"""));
        var restart = await dispatcher.DispatchAsync(Request("set_restart", "{}"));
        var title = await dispatcher.DispatchAsync(Request("set_group_special_title", """{"group_id":1,"user_id":2,"special_title":"x","duration":60}"""));

        Assert.AreEqual(1404, record.RetCode);
        Assert.AreEqual(1404, restart.RetCode);
        Assert.AreEqual(1404, title.RetCode);
    }

    [TestMethod]
    public async Task CapabilityStatusAndCleanCacheActions_InvokeRealFacadeBehavior()
    {
        var facade = new RecordingFacade();
        var dispatcher = new OneBotActionDispatcher(facade);

        var image = await dispatcher.DispatchAsync(Request("can_send_image", "{}"));
        var record = await dispatcher.DispatchAsync(Request("can_send_record", "{}"));
        var status = await dispatcher.DispatchAsync(Request("get_status", "{}"));
        var clean = await dispatcher.DispatchAsync(Request("clean_cache", "{}"));

        Assert.IsTrue(JsonSerializer.SerializeToElement(image.Data).GetProperty("yes").GetBoolean());
        Assert.IsTrue(JsonSerializer.SerializeToElement(record.Data).GetProperty("yes").GetBoolean());
        Assert.IsTrue(JsonSerializer.SerializeToElement(status.Data).GetProperty("online").GetBoolean());
        Assert.AreEqual(0, clean.RetCode);
        Assert.IsTrue(facade.CacheCleaned);
    }

    [TestMethod]
    public async Task GetEventAndQuickOperation_UseSharedStateAndRegisteredMessageId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        var dispatcher = new OneBotActionDispatcher(facade, state);
        await state.Events.FetchAsync("test", TimeSpan.Zero);
        state.Events.Push(new() { Time = 1, SelfId = 2, PostType = "notice" });
        var messageId = await state.Messages.RegisterAsync(new(MessageScene.Group, 7, 8));

        var events = await dispatcher.DispatchAsync(Request("get_event", """{"consumer":"test"}"""));
        var quick = await dispatcher.DispatchAsync(Request(".handle_quick_operation", JsonSerializer.Serialize(new
        {
            context = new { group_id = 7, user_id = 9, message_id = messageId },
            operation = new { reply = "ok", delete = true },
        })));

        Assert.AreEqual(0, events.RetCode);
        Assert.HasCount(1, (IReadOnlyList<ShiroBot.Plugin.OneBotServer.Events.OneBotEvent>)events.Data!);
        Assert.AreEqual(0, quick.RetCode);
        Assert.AreEqual("8", facade.DeletedMessageId);
        Assert.AreEqual(Channel.Group("7"), facade.DeleteChannel);
        Assert.AreEqual("ok", ((TextSegment)facade.Messages.Single().Single()).Text);
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task RequestFlags_DecodeFriendGroupAndInvitationWithoutGroupId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        var dispatcher = new OneBotActionDispatcher(facade, state);
        var friend = RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: "uid"), state.RequestFlagKey);
        var group = RequestFlagCodec.Encode(new RequestFlag("group", 10, 20, RequestType: "join_request"), state.RequestFlagKey);
        var invitation = RequestFlagCodec.Encode(new RequestFlag("invitation", 11, 21), state.RequestFlagKey);

        var friendResponse = await dispatcher.DispatchAsync(Request("set_friend_add_request", JsonSerializer.Serialize(new { flag = friend })));
        var groupResponse = await dispatcher.DispatchAsync(Request("set_group_add_request", JsonSerializer.Serialize(new { flag = group, sub_type = "add" })));
        var invitationResponse = await dispatcher.DispatchAsync(Request("set_group_add_request", JsonSerializer.Serialize(new { flag = invitation, sub_type = "invite" })));

        Assert.AreEqual(0, friendResponse.RetCode);
        Assert.AreEqual(0, groupResponse.RetCode);
        Assert.AreEqual(0, invitationResponse.RetCode);
        Assert.AreEqual("uid", facade.FriendRequest.Item1);
        Assert.AreEqual(new RequestFlag("invitation", 11, 21), facade.GroupRequest);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task PrivateFileRegistry_SuppliesDownloadMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RecordingFacade();
        var state = await CreateStateAsync(directory);
        state.Files.Remember("file", new PrivateFileReference(12, "hash", true));
        var dispatcher = new OneBotActionDispatcher(facade, state);

        var response = await dispatcher.DispatchAsync(Request("get_private_file_url", """{"file_id":"file"}"""));

        Assert.AreEqual(0, response.RetCode);
        Assert.AreEqual((12L, "file", "hash", true), facade.PrivateFile);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static async Task<OneBotRuntimeState> CreateStateAsync(string directory) =>
        new(await MessageIdRegistry.OpenAsync(Path.Combine(directory, "ids.json")), 100, 10,
            OneBotRuntimeState.DeriveRequestFlagKey("0123456789abcdef", null));

    private static OneBotActionRequest Request(string action, string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        return new OneBotActionRequest(action, OneBotParameters.ObjectOrEmpty(document.RootElement), null);
    }

    private static IReadOnlyDictionary<string, JsonElement> Empty() => Request("x", "{}").Params;

    private sealed class RecordingFacade : OneBotContextFacadeBase
    {
        public Channel? Channel { get; private set; }
        public List<IReadOnlyList<MessageSegment>> Messages { get; } = [];
        public TaskCompletionSource<object?>? LoginGate { get; init; }
        public Exception? Failure { get; init; }
        public Exception? CredentialFailure { get; init; }
        public int CookieCalls { get; private set; }
        public int CsrfCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public ConcurrentQueue<DateTimeOffset> LoginCallTimes { get; } = [];
        public (long, long, bool) GroupAdmin { get; private set; }
        public TimeSpan BanDuration { get; private set; }
        public (string, bool, string?) FriendRequest { get; private set; }
        public RequestFlag? GroupRequest { get; private set; }
        public (long?, long) Nudge { get; private set; }
        public (long, long, string, bool) Reaction { get; private set; }
        public (long, string) Announcement { get; private set; }
        public (long, long, bool) Essence { get; private set; }
        public (long, string, string, string) Upload { get; private set; }
        public (long, string, string, string) Move { get; private set; }
        public (long, string, string, string) Rename { get; private set; }
        public (long, string) Persist { get; private set; }
        public Channel? DeleteChannel { get; private set; }
        public Channel? GetChannel { get; private set; }
        public string? DeletedMessageId { get; private set; }
        public (long, string, string, bool) PrivateFile { get; private set; }
        public (long GroupId, string FileId) GroupFile { get; private set; }
        public bool CacheCleaned { get; private set; }
        public string StoragePath { get; init; } = Path.Combine(Path.GetTempPath(), "onebot-tests");
        public string? Avatar { get; private set; }
        public (string?, string?) Profile { get; private set; }
        public List<long> KickedUsers { get; } = [];
        public Channel? ForwardChannel { get; private set; }
        public IReadOnlyList<OneBotForwardNode> ForwardNodes { get; private set; } = [];
        public (OneBotMessageReference, Channel)? SingleForward { get; private set; }

        public override string StorageDirectory => StoragePath;

        public override Task<string> SendAsync(Channel channel, IReadOnlyList<MessageSegment> message)
        {
            Channel = channel;
            Messages.Add(message);
            return Task.FromResult("99");
        }

        public override Task DeleteAsync(string messageId, Channel? channel = null) { DeletedMessageId = messageId; DeleteChannel = channel; return Task.CompletedTask; }
        public override Task<object?> GetAsync(string messageId, Channel? channel = null) { GetChannel = channel; return Task.FromResult<object?>(new { }); }
        public override Task<object?> GetLoginInfoAsync()
        {
            LoginCalls++;
            LoginCallTimes.Enqueue(DateTimeOffset.UtcNow);
            if (Failure is not null) return Task.FromException<object?>(Failure);
            return LoginGate?.Task ?? Task.FromResult<object?>(new { user_id = 1L });
        }
        public override Task SetGroupAdminAsync(long groupId, long userId, bool enabled) { GroupAdmin = (groupId, userId, enabled); return Task.CompletedTask; }
        public override Task SetGroupBanAsync(long groupId, long userId, TimeSpan duration) { BanDuration = duration; return Task.CompletedTask; }
        public override Task SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest) { KickedUsers.Add(userId); return Task.CompletedTask; }
        public override Task SetFriendRequestAsync(RequestFlag request, bool approve, string? remark) { FriendRequest = (request.InitiatorUid!, approve, remark); return Task.CompletedTask; }
        public override Task SetGroupRequestAsync(RequestFlag request, bool approve, string? reason) { GroupRequest = request; return Task.CompletedTask; }
        public override Task<string> GetCookiesAsync(string domain)
        {
            CookieCalls++;
            return CredentialFailure is null ? Task.FromResult("cookie=" + domain) : Task.FromException<string>(CredentialFailure);
        }
        public override Task<string> GetCsrfTokenAsync()
        {
            CsrfCalls++;
            return CredentialFailure is null ? Task.FromResult("42") : Task.FromException<string>(CredentialFailure);
        }
        public override Task SendNudgeAsync(long? groupId, long userId) { Nudge = (groupId, userId); return Task.CompletedTask; }
        public override Task SetReactionAsync(long groupId, long messageId, string reactionId, bool enabled) { Reaction = (groupId, messageId, reactionId, enabled); return Task.CompletedTask; }
        public override Task SendAnnouncementAsync(long groupId, string content, string? image) { Announcement = (groupId, content); return Task.CompletedTask; }
        public override Task SetEssenceMessageAsync(long groupId, long messageId, bool enabled) { Essence = (groupId, messageId, enabled); return Task.CompletedTask; }
        public override Task<string> UploadGroupFileAsync(long groupId, string file, string name, string folderId) { Upload = (groupId, file, name, folderId); return Task.FromResult("f"); }
        public override Task MoveGroupFileAsync(long groupId, string fileId, string targetFolderId, string parentFolderId) { Move = (groupId, fileId, targetFolderId, parentFolderId); return Task.CompletedTask; }
        public override Task RenameGroupFileAsync(long groupId, string fileId, string name, string parentFolderId) { Rename = (groupId, fileId, name, parentFolderId); return Task.CompletedTask; }
        public override Task PersistGroupFileAsync(long groupId, string fileId) { Persist = (groupId, fileId); return Task.CompletedTask; }
        public override Task<string> GetPrivateFileUrlAsync(long userId, string fileId, string fileHash, bool isSelfSend) { PrivateFile = (userId, fileId, fileHash, isSelfSend); return Task.FromResult("https://example.test/private"); }
        public override Task<string> GetGroupFileUrlAsync(long groupId, string fileId) { GroupFile = (groupId, fileId); return Task.FromResult("https://example.test/group"); }
        public string? ForwardedId { get; private set; }
        public override Task<object?> GetForwardedAsync(string forwardId) { ForwardedId = forwardId; return Task.FromResult<object?>(new { messages = Array.Empty<object>() }); }
        public override Task<bool> CanSendImageAsync() => Task.FromResult(true);
        public override Task<bool> CanSendRecordAsync() => Task.FromResult(true);
        public override Task<object?> GetStatusAsync() => Task.FromResult<object?>(new { online = true, good = true });
        public override Task CleanCacheAsync() { CacheCleaned = true; return Task.CompletedTask; }
        public override Task SetAvatarAsync(string file) { Avatar = file; return Task.CompletedTask; }
        public override Task<IReadOnlyList<string>> GetCustomFaceUrlsAsync() => Task.FromResult<IReadOnlyList<string>>(["face-1", "face-2"]);
        public override Task<IReadOnlyList<QFriend>> GetFriendEntitiesAsync(bool noCache) => Task.FromResult<IReadOnlyList<QFriend>>
        ([
            new QFriend { UserId = "1", Nickname = "one", Category = new(10, "friends") },
            new QFriend { UserId = "2", Nickname = "two", Category = new(10, "friends") },
        ]);
        public override Task SetNicknameAsync(string nickname) { Profile = (nickname, Profile.Item2); return Task.CompletedTask; }
        public override Task SetBioAsync(string bio) { Profile = (Profile.Item1, bio); return Task.CompletedTask; }
        public override Task<IReadOnlyList<QGroupMember>> GetGroupMemberEntitiesAsync(long groupId, bool noCache) => Task.FromResult<IReadOnlyList<QGroupMember>>
        ([
            new QGroupMember { GroupId = groupId.ToString(), UserId = "2", Nickname = "muted", ShutUpEndTime = DateTimeOffset.UtcNow.AddMinutes(1) },
            new QGroupMember { GroupId = groupId.ToString(), UserId = "3", Nickname = "free" },
        ]);
        public override Task<string> GetUserNicknameAsync(long userId) => Task.FromResult("user-" + userId);
        public override Task<string> SendForwardAsync(Channel channel, IReadOnlyList<OneBotForwardNode> nodes, string? title, IReadOnlyList<string>? preview, string? summary, string? prompt)
        { ForwardChannel = channel; ForwardNodes = nodes; return Task.FromResult("101"); }
        public override Task<OneBotForwardNode> GetForwardNodeAsync(OneBotMessageReference source) => Task.FromResult(new OneBotForwardNode(2, "source", [OneBotSegment.Text("message")], null));
        public override Task<string> ForwardSingleAsync(OneBotMessageReference source, Channel destination) { SingleForward = (source, destination); return Task.FromResult("102"); }
        public override Task<IReadOnlyList<OneBotGroupFileEntry>> GetGroupFileEntriesAsync(long groupId, string folderId) =>
            Task.FromResult<IReadOnlyList<OneBotGroupFileEntry>>(folderId == "/"
                ? [new("a", 5, "/", false), new("sub", 0, "/", true)]
                : [new("b", 7, "sub", false)]);

        public async Task WaitForLoginCallsAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (LoginCalls < expected) await Task.Delay(5, timeout.Token);
        }
    }
}
