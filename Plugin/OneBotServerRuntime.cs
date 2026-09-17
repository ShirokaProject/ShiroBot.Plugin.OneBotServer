using ShiroBot.Plugin.OneBotServer.Actions;
using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Infrastructure;
using ShiroBot.Plugin.OneBotServer.Protocol;
using ShiroBot.Plugin.OneBotServer.Transports;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

/// <summary>Composes the OneBot action, HTTP, WebSocket, webhook, and heartbeat services.</summary>
public sealed class OneBotServerRuntime(IOneBotContextFacade context, string pluginDirectory) : IOneBotServerRuntime
{
    private OneBotActionDispatcher? _actions;
    private OneBotRuntimeState? _state;
    private readonly List<HttpClient> _httpClients = [];
    private CancellationTokenSource? _runtimeCancellation;
    private OneBotKestrelServer? _server;
    private readonly List<OneBotReverseWebSocketClient> _reverseWebSockets = [];
    private OneBotHttpEventPoster[] _httpPosters = [];
    private readonly List<Task> _reverseWebSocketTasks = [];
    private Task? _heartbeatTask;
    private long _selfId;

    public async Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_runtimeCancellation is not null) throw new InvalidOperationException("The OneBot runtime is already started.");
        if (config.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(config), "Port must be between 1 and 65535.");
        if (config.Heartbeat.Enabled && config.Heartbeat.IntervalSeconds < 1) throw new ArgumentOutOfRangeException(nameof(config), "Heartbeat interval must be positive.");
        if (config.ReverseWebSocket.ReconnectDelaySeconds < 0) throw new ArgumentOutOfRangeException(nameof(config), "Reconnect delay cannot be negative.");
        if (config.HttpTargets.Any(target => target.TimeoutSeconds < 1)) throw new ArgumentOutOfRangeException(nameof(config), "HTTP target timeout must be positive.");

        _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runtimeToken = _runtimeCancellation.Token;
        try
        {
            var selfId = await ResolveSelfIdAsync(config).ConfigureAwait(false);
            if (!long.TryParse(selfId, out _selfId)) throw new InvalidOperationException("The active QQ adapter returned a non-numeric self ID.");
            var registryPath = Path.IsPathRooted(config.Storage.MessageIdRegistryPath)
                ? config.Storage.MessageIdRegistryPath
                : Path.Combine(pluginDirectory, config.Storage.MessageIdRegistryPath);
            var messages = await MessageIdRegistry.OpenAsync(registryPath, config.Storage.RegistryMaxEntries, runtimeToken).ConfigureAwait(false);
            _state = new OneBotRuntimeState(messages, config.Storage.RegistryMaxEntries, config.Limits.EventQueueCapacity,
                OneBotRuntimeState.DeriveRequestFlagKey(config.Storage.RequestFlagSecret, config.AccessToken));
            _actions = new OneBotActionDispatcher(context, _state);

            if (config.Http.Enabled || config.ForwardWebSocket.Enabled)
            {
                _server = await OneBotKestrelServer.StartAsync(
                    new OneBotServerOptions(
                        [$"http://{config.Host}:{config.Port}"], selfId, config.AccessToken,
                        config.Limits.EventQueueCapacity, config.Http.Enabled, config.Http.Path,
                         config.ForwardWebSocket.Enabled, config.ForwardWebSocket.Path,
                         MaxRequestBodyBytes: 0, config.Limits.MaxWebSocketConnections,
                         MaxWebSocketMessageBytes: 0),
                     _actions, runtimeToken).ConfigureAwait(false);
            }

            if (config.ReverseWebSocket.Enabled)
            {
                foreach (var (url, role) in ResolveReverseEndpoints(config.ReverseWebSocket))
                {
                    var endpoint = ParseWebSocketEndpoint(url);
                    var client = new OneBotReverseWebSocketClient(
                        new OneBotReverseWebSocketOptions(endpoint, selfId, role, config.AccessToken,
                            TimeSpan.FromSeconds(config.ReverseWebSocket.ReconnectDelaySeconds),
                            config.Limits.EventQueueCapacity, MaxWebSocketMessageBytes: 0),
                        _actions);
                    _reverseWebSockets.Add(client);
                    _reverseWebSocketTasks.Add(client.RunAsync(runtimeToken));
                }
            }

            _httpPosters = config.HttpTargets.Select(target => CreateHttpPoster(target, selfId)).ToArray();
            if (config.Heartbeat.Enabled) _heartbeatTask = RunHeartbeatAsync(config.Heartbeat.IntervalSeconds, runtimeToken);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PublishAsync(BotEvent source, OneBotEventFormatConfig format, CancellationToken cancellationToken)
    {
        var state = _state ?? throw new InvalidOperationException("The OneBot runtime is not started.");
        var mapped = await OneBotEventMapper.MapAsync(source, format, state.Messages, cancellationToken).ConfigureAwait(false);
        mapped = await EnrichGroupFileUrlsAsync(source, mapped, cancellationToken).ConfigureAwait(false);
        var evt = await PrepareEventAsync(source, mapped, state, cancellationToken).ConfigureAwait(false);
        state.Events.Push(evt);
        var operations = new List<Task>(_httpPosters.Length + 2);
        if (_server is not null) operations.Add(_server.PublishEventAsync(evt, cancellationToken));
        operations.AddRange(_reverseWebSockets.Select(client => client.PublishEventAsync(evt, cancellationToken)));
        operations.AddRange(_httpPosters.Select(poster => poster.PostAsync(evt, cancellationToken)));
        await Task.WhenAll(operations).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var runtimeCancellation = Interlocked.Exchange(ref _runtimeCancellation, null);
        if (runtimeCancellation is null) return;
        await runtimeCancellation.CancelAsync().ConfigureAwait(false);

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        await Task.WhenAll(_reverseWebSocketTasks.Select(IgnoreCancellationAsync)).ConfigureAwait(false);
        await IgnoreCancellationAsync(_heartbeatTask).ConfigureAwait(false);
        _reverseWebSockets.Clear();
        _reverseWebSocketTasks.Clear();
        _heartbeatTask = null;
        _httpPosters = [];
        _actions = null;
        _state = null;
        _selfId = 0;
        foreach (var client in _httpClients) client.Dispose();
        _httpClients.Clear();
        runtimeCancellation.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private OneBotHttpEventPoster CreateHttpPoster(OneBotHttpTargetConfig target, string selfId)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(target.TimeoutSeconds) };
        _httpClients.Add(client);
        return new OneBotHttpEventPoster(client, new OneBotHttpEventPostOptions(
            [new Uri(target.Url, UriKind.Absolute)], target.AccessToken, target.Secret, selfId), _actions);
    }

    private async Task<string> ResolveSelfIdAsync(OneBotServerConfig config)
    {
        try
        {
            var selfId = await context.GetSelfIdAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selfId) && selfId != "0") return selfId;
        }
        catch (NotSupportedException) { }
        return config.SelfId;
    }

    internal async Task<OneBotEvent> EnrichGroupFileUrlsAsync(
        BotEvent source,
        OneBotEvent mapped,
        CancellationToken cancellationToken)
    {
        if (source is not MessageEvent { Raw: QIncomingMessage message })
        {
            // group_upload notices are spread into a file segment by clients (e.g. TRSS Yunzai),
            // so the file object must carry a resolvable url and the legacy fid alias.
            if (source is PlatformEvent { Raw: QGroupFileUpload upload })
            {
                var noticeData = new Dictionary<string, object?>(mapped.Data);
                if (noticeData.TryGetValue("file", out var value) && value is IDictionary<string, object?> noticeFile)
                {
                    noticeFile["fid"] = upload.FileId;
                    var noticeUrl = await TryResolveGroupFileUrlAsync(upload.GroupId, upload.FileId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(noticeUrl)) noticeFile["url"] = noticeUrl;
                }
                return mapped with { Data = noticeData };
            }
            return mapped;
        }

        var files = message.Segments.OfType<QIncomingFile>().ToArray();
        if (files.Length == 0) return mapped;

        // Private file messages: remember the owner so get_file/get_private_file_url work without extra params.
        if (message.Scene != QMessageScene.Group)
        {
            foreach (var file in files)
                _state?.Files.Remember(file.FileId, new PrivateFileReference(message.PeerId, file.FileHash ?? string.Empty, message.SenderId == _selfId));
            return mapped;
        }

        var data = new Dictionary<string, object?>(mapped.Data);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            _state?.GroupFiles.Remember(file.FileId, new GroupFileReference(message.PeerId, file.FileName, file.FileSize));
            var url = await TryResolveGroupFileUrlAsync(message.PeerId, file.FileId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(url)) urls[file.FileId] = url;
        }
        if (urls.Count == 0) return mapped;

        if (data.TryGetValue("message", out var messageValue))
            data["message"] = AddFileUrls(messageValue, urls);
        if (data.TryGetValue("raw_message", out var rawValue) && rawValue is string rawMessage)
            data["raw_message"] = AddFileUrls(rawMessage, urls);
        return mapped with { Data = data };
    }

    private async Task<string?> TryResolveGroupFileUrlAsync(long groupId, string fileId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = await context.GetGroupFileUrlAsync(groupId, fileId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                BotLog.Warning($"[OneBot/File] 群文件下载地址为空，group_id={groupId}，file_id={SafeFileId(fileId)}。");
                return null;
            }
            return url;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            BotLog.Error($"[OneBot/File] 获取群文件下载地址失败，group_id={groupId}，file_id={SafeFileId(fileId)}，{exception.GetType().Name}: {exception}");
            return null;
        }
    }

    private static object? AddFileUrls(object? message, IReadOnlyDictionary<string, string> urls)
    {
        var segments = message switch
        {
            OneBotSegment[] array => array,
            IReadOnlyList<OneBotSegment> list => list.ToArray(),
            string cq => CqCode.Parse(cq).ToArray(),
            _ => null
        };
        if (segments is null) return message;

        var changed = false;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (!string.Equals(segment.Type, "file", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Convert.ToString(segment.Data.GetValueOrDefault("file_id")) ??
                     Convert.ToString(segment.Data.GetValueOrDefault("id"));
            if (string.IsNullOrWhiteSpace(id) || !urls.TryGetValue(id, out var url)) continue;
            var segmentData = new Dictionary<string, object?>(segment.Data) { ["url"] = url };
            segments[index] = segment with { Data = segmentData };
            changed = true;
        }
        if (!changed) return message;
        return message is string ? CqCode.Serialize(segments) : segments;
    }

    private static string SafeFileId(string fileId) => fileId.Length <= 160 ? fileId : fileId[..160] + "...";

    private async Task<OneBotEvent> PrepareEventAsync(BotEvent source, OneBotEvent evt, OneBotRuntimeState state, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object?>(evt.Data);
        MessageReference? message = source switch
        {
            MessageEvent value => Reference(value.Channel, value.MessageId),
            MessageDeletedEvent value => Reference(value.Channel, value.MessageId),
            PlatformEvent { Raw: QGroupEssenceMessageChange value } => new(MessageScene.Group, value.GroupId, value.MessageSeq),
            PlatformEvent { Raw: QGroupMessageReaction value } => new(MessageScene.Group, value.GroupId, value.MessageSeq),
            PlatformEvent { Raw: QMessageRecall value } => new(ToScene(value.Scene), value.PeerId, value.MessageSeq),
            _ => null,
        };
        if (message is not null) data["message_id"] = await state.Messages.RegisterAsync(message, cancellationToken).ConfigureAwait(false);
        if (source is MessageEvent messageEvent)
            await RewriteReplyIdsAsync(messageEvent, data, state, cancellationToken).ConfigureAwait(false);

        switch (source)
        {
            case FriendRequestEvent request:
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: request.Token ?? request.UserId, NativeToken: request.Token), state.RequestFlagKey);
                break;
            case GuildInviteEvent invite when long.TryParse(invite.GuildId, out var groupId):
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("invitation", groupId, NativeToken: invite.Token), state.RequestFlagKey);
                break;
            case PlatformEvent { Raw: QFriendRequestReceived request }:
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("friend", InitiatorUid: request.InitiatorUid), state.RequestFlagKey);
                break;
            case PlatformEvent { Raw: QGroupJoinRequest request }:
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("group", request.GroupId, request.NotificationSeq, Filtered: request.IsFiltered, RequestType: "join_request"), state.RequestFlagKey);
                break;
            case PlatformEvent { Raw: QGroupInvitedJoinRequest request }:
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("group", request.GroupId, request.NotificationSeq, RequestType: "invited_join_request"), state.RequestFlagKey);
                break;
            case PlatformEvent { Raw: QGroupInvitation invitation }:
                data["flag"] = RequestFlagCodec.Encode(new RequestFlag("invitation", invitation.GroupId, invitation.InvitationSeq), state.RequestFlagKey);
                break;
            case PlatformEvent { Raw: QFriendFileUpload file }:
                state.Files.Remember(file.FileId, new PrivateFileReference(file.UserId, file.FileHash ?? string.Empty, file.IsSelf));
                break;
            case PlatformEvent { Raw: QGroupFileUpload file }:
                state.GroupFiles.Remember(file.FileId, new GroupFileReference(file.GroupId, file.FileName, file.FileSize));
                BotLog.Log($"[OneBot/File] 记录群文件，group_id={file.GroupId}，file_id={SafeFileId(file.FileId)}，name={file.FileName}。");
                break;
            case PlatformEvent { Raw: QGroupMessageReaction reaction }:
                data["count"] = state.Reactions.Update(reaction.GroupId, reaction.MessageSeq, reaction.FaceId, reaction.UserId, reaction.IsAdd);
                break;
        }
        return evt with { SelfId = evt.SelfId == 0 ? _selfId : evt.SelfId, Data = data };
    }

    private static MessageReference? Reference(Channel channel, string sequence) =>
        long.TryParse(channel.Id, out var peerId) && long.TryParse(sequence, out var messageSequence)
            ? new MessageReference(channel.Type == ChannelType.Group ? MessageScene.Group : channel.Type == ChannelType.Direct ? MessageScene.Friend : MessageScene.Temp, peerId, messageSequence)
            : null;
    private static MessageScene ToScene(QMessageScene scene) => scene switch { QMessageScene.Group => MessageScene.Group, QMessageScene.Temp => MessageScene.Temp, _ => MessageScene.Friend };

    private static async Task RewriteReplyIdsAsync(MessageEvent source, IDictionary<string, object?> data, OneBotRuntimeState state, CancellationToken cancellationToken)
    {
        if (!long.TryParse(source.Channel.Id, out var peerId)) return;
        var replacements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var quote in source.Segments.OfType<QuoteSegment>())
        {
            if (!long.TryParse(quote.MessageId, out var sequence)) continue;
            var id = await state.Messages.RegisterAsync(new MessageReference(
                source.Channel.Type == ChannelType.Group ? MessageScene.Group : source.Channel.Type == ChannelType.Direct ? MessageScene.Friend : MessageScene.Temp,
                peerId, sequence), cancellationToken).ConfigureAwait(false);
            replacements[quote.MessageId] = id;
        }
        if (replacements.Count == 0) return;
        if (data.TryGetValue("message", out var message) && message is OneBotSegment[] segments)
            data["message"] = segments.Select(segment => segment.Type == "reply" && segment.Data.TryGetValue("id", out var id) && id is not null && replacements.TryGetValue(id.ToString()!, out var mapped)
                ? segment with { Data = new Dictionary<string, object?>(segment.Data) { ["id"] = mapped } }
                : segment).ToArray();
        foreach (var key in new[] { "message", "raw_message" })
            if (data.TryGetValue(key, out var value) && value is string text)
            {
                foreach (var replacement in replacements) text = text.Replace($"[CQ:reply,id={replacement.Key}]", $"[CQ:reply,id={replacement.Value}]", StringComparison.Ordinal);
                data[key] = text;
            }
    }

    private async Task RunHeartbeatAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        if (intervalSeconds < 1) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var milliseconds = checked((long)interval.TotalMilliseconds);
            var operations = new List<Task>(_reverseWebSockets.Count + 1);
            if (_server is not null) operations.Add(_server.PublishHeartbeatAsync(milliseconds, cancellationToken));
            operations.AddRange(_reverseWebSockets.Select(client => client.PublishHeartbeatAsync(milliseconds, cancellationToken)));
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<(string Url, OneBotWebSocketRole Role)> ResolveReverseEndpoints(OneBotReverseWebSocketConfig config)
    {
        var universal = config.UniversalUrl ?? config.Url;
        if (!string.IsNullOrWhiteSpace(universal)) return [(universal, OneBotWebSocketRole.Universal)];

        var endpoints = new List<(string, OneBotWebSocketRole)>();
        if (!string.IsNullOrWhiteSpace(config.ApiUrl)) endpoints.Add((config.ApiUrl, OneBotWebSocketRole.Api));
        if (!string.IsNullOrWhiteSpace(config.EventUrl)) endpoints.Add((config.EventUrl, OneBotWebSocketRole.Event));
        if (endpoints.Count == 0) throw new InvalidOperationException("Reverse WebSocket requires url/universalUrl or apiUrl/eventUrl.");
        return endpoints;
    }

    private static Uri ParseWebSocketEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("ws" or "wss"))
            throw new InvalidOperationException("Reverse WebSocket endpoints must be absolute ws:// or wss:// URLs.");
        return endpoint;
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
