using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Plugin;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ShiroBot.Model.QQ;

namespace ShiroBot.Plugin.OneBotServer.Tests.Plugin;

[TestClass]
public sealed class InstanceBindingTests
{
    [TestMethod]
    public void Resolve_RequiresExplicitSelectionWhenMultipleAdaptersAndSeparatesStorage()
    {
        var context = new FakeContext();
        Assert.ThrowsExactly<InvalidOperationException>(() => OneBotInstanceBinding.Resolve(new(), context.Instances, context.PluginDirectory));
        var bindings = OneBotInstanceBinding.Resolve(new()
        {
            Instances =
            [
                new() { InstanceId = "first", Port = 5701 },
                new() { InstanceId = "second", Host = "localhost", Port = 5702 }
            ]
        }, context.Instances, context.PluginDirectory);

        Assert.AreEqual(2, bindings.Count);
        Assert.AreNotEqual(bindings[0].Config.Storage.MessageIdRegistryPath, bindings[1].Config.Storage.MessageIdRegistryPath);
        Assert.AreNotEqual(OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, "first"),
            OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, "second"));
    }

    [TestMethod]
    public void Resolve_RejectsConflictingListenersAndMissingInstance()
    {
        var context = new FakeContext();
        var conflict = new OneBotServerConfig { Instances =
        [
            new() { InstanceId = "first", Host = "127.0.0.1", Port = 5701 },
            new() { InstanceId = "second", Host = "localhost", Port = 5701 }
        ] };
        Assert.ThrowsExactly<InvalidOperationException>(() => OneBotInstanceBinding.Resolve(conflict, context.Instances, context.PluginDirectory));
        Assert.ThrowsExactly<InvalidOperationException>(() => OneBotInstanceBinding.Resolve(new() { InstanceId = "removed" }, context.Instances, context.PluginDirectory));
    }

    [TestMethod]
    public void Resolve_AllowsSameUnusedLocalPortForReverseOnlyBindings()
    {
        var context = new FakeContext();
        var config = new OneBotServerConfig
        {
            Http = new() { Enabled = false },
            ForwardWebSocket = new() { Enabled = false },
            ReverseWebSocket = new() { Enabled = true, UniversalUrl = "ws://127.0.0.1:2536/one" },
            Instances = [new() { InstanceId = "first", Port = 5700 }, new() { InstanceId = "second", Port = 5700 }]
        };

        var bindings = OneBotInstanceBinding.Resolve(config, context.Instances, context.PluginDirectory);
        Assert.AreEqual(2, bindings.Count);
    }

    [TestMethod]
    public void Resolve_RequiresPerInstanceSelfIdsToBeResolvedAutomatically()
    {
        var context = new FakeContext();
        var config = new OneBotServerConfig
        {
            SelfId = "10001",
            Instances = [new() { InstanceId = "first", Port = 5711 }, new() { InstanceId = "second", Port = 5712 }]
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => OneBotInstanceBinding.Resolve(config, context.Instances, context.PluginDirectory));
    }

    [TestMethod]
    public void Resolve_AllowsNonQqAdapterInstanceForGenericMessageApis()
    {
        var instance = new AdapterInstanceInfo("matrix", "Matrix", "adapter", "Adapter", "1", "matrix", null);
        var result = OneBotInstanceBinding.Resolve(new() { InstanceId = "matrix" }, [instance], Path.GetTempPath());
        Assert.AreEqual("matrix", result.Single().InstanceId);
    }

    [TestMethod]
    public void StoragePath_IsBoundedAndStableAcrossInstanceIdCase()
    {
        var longId = "MiXeD" + new string('x', 500);
        var path = OneBotInstanceBinding.GetInstanceStorageDirectory(Path.GetTempPath(), longId);
        Assert.IsTrue(Path.GetFileName(path).Length <= 64);
        Assert.AreEqual(path, OneBotInstanceBinding.GetInstanceStorageDirectory(Path.GetTempPath(), longId.ToUpperInvariant()));
    }

    [TestMethod]
    public async Task LegacyRegistrySeedsNewInstanceIdsWithoutImportingOldReferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "onebot-legacy-test-" + Guid.NewGuid().ToString("N"));
        var legacyPath = Path.Combine(directory, "message-ids.json");
        var instancePath = Path.Combine(directory, "instances", "first", "message-ids.json");
        var legacy = await Infrastructure.MessageIdRegistry.OpenAsync(legacyPath, 10);
        var oldReference = new Infrastructure.OneBotMessageReference(Infrastructure.MessageScene.Group, 77, 88);
        var oldId = await legacy.RegisterAsync(oldReference);
        var seed = await Infrastructure.MessageIdRegistry.ReadNextIdAsync(legacyPath);

        var current = await Infrastructure.MessageIdRegistry.OpenAsync(instancePath, 10, minimumNextId: seed);
        var currentReference = new Infrastructure.OneBotMessageReference(Infrastructure.MessageScene.Group, 77, 89);
        var currentId = await current.RegisterAsync(currentReference);
        Assert.IsTrue(currentId > oldId);
        Assert.IsFalse(current.TryResolve(oldId, out _));

        var reopened = await Infrastructure.MessageIdRegistry.OpenAsync(instancePath, 10);
        Assert.IsFalse(reopened.TryResolve(oldId, out _));
        var afterRestart = await reopened.RegisterAsync(new(Infrastructure.MessageScene.Group, 77, 90));
        Assert.IsTrue(afterRestart > currentId);
        Assert.IsTrue(File.Exists(legacyPath));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task ScopedFacade_PreservesInstanceAcrossAwaitAndUsesSeparatedStorage()
    {
        var context = new FakeContext();
        var facade = InstanceScopedOneBotContextFacade.Bind(new ScopeProbeFacade(context), context, "second");
        await facade.GetSelfIdAsync();
        Assert.AreEqual("second", context.ObservedAfterAwait);
        Assert.AreEqual("outer", context.InstanceId);
        Assert.AreEqual(OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, "second"), facade.StorageDirectory);

        var firstCache = Path.Combine(OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, "first"), "cache");
        var secondCache = Path.Combine(OneBotInstanceBinding.GetInstanceStorageDirectory(context.PluginDirectory, "second"), "cache");
        Directory.CreateDirectory(firstCache);
        Directory.CreateDirectory(secondCache);
        File.WriteAllText(Path.Combine(firstCache, "keep"), "one");
        File.WriteAllText(Path.Combine(secondCache, "remove"), "two");
        await facade.CleanCacheAsync();
        Assert.IsTrue(File.Exists(Path.Combine(firstCache, "keep")));
        Assert.IsFalse(File.Exists(Path.Combine(secondCache, "remove")));
        if (Directory.Exists(context.PluginDirectory)) Directory.Delete(context.PluginDirectory, recursive: true);
    }

    [TestMethod]
    public async Task ProductionFacade_SendsRichMessagesWithExplicitFallbacksAndReportsFailure()
    {
        var context = new FakeContext();
        var fake = DispatchProxy.Create<IMessageContext, FakeMessageContext>();
        var handler = (FakeMessageContext)(object)fake;
        context.Message = fake;
        var facade = new OneBotContextFacade(context);
        var card = new CardSegment
        {
            Title = "Title", Description = "Description", ImageUrl = "https://example.test/image",
            Url = "https://example.test/card", Fields = [new("Field A", "Value A"), new("Field B", "Value B")]
        };

        handler.Capabilities = new MessageCapabilities { NativeFeatures = MessageFeatures.Text | MessageFeatures.Markdown | MessageFeatures.Card, CanMixMarkdown = true };
        Assert.AreEqual("native-id", await facade.SendAsync(Channel.Direct("123"), [new MarkdownSegment("**bold**"), card]));
        Assert.IsNotNull(handler.LastOutgoing);
        Assert.AreEqual(MessageFallbackOptions.MarkdownAsText | MessageFallbackOptions.CardAsMarkdown | MessageFallbackOptions.CardAsText,
            handler.LastOutgoing.AllowedFallbacks);
        Assert.AreEqual("Value B", ((CardSegment)handler.LastPrepared!.Message.Segments[1]).Fields[1].Value);
        var retainedCard = (CardSegment)handler.LastPrepared.Message.Segments[1];
        Assert.AreEqual(card.Title, retainedCard.Title);
        Assert.AreEqual(card.Description, retainedCard.Description);
        Assert.AreEqual(card.ImageUrl, retainedCard.ImageUrl);
        Assert.AreEqual(card.Url, retainedCard.Url);
        CollectionAssert.AreEqual(card.Fields.ToArray(), retainedCard.Fields.ToArray());

        handler.Capabilities = new MessageCapabilities { NativeFeatures = MessageFeatures.Text };
        Assert.AreEqual("native-id", await facade.SendAsync(Channel.Direct("123"), [new MarkdownSegment("**bold**"), card]));
        Assert.IsInstanceOfType(handler.LastPrepared!.Message.Segments[0], typeof(TextSegment));
        Assert.IsInstanceOfType(handler.LastPrepared.Message.Segments[1], typeof(TextSegment));
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, "Value A");
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, "Value B");
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, card.Title!);
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, card.Description!);
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, card.ImageUrl!);
        StringAssert.Contains(((TextSegment)handler.LastPrepared.Message.Segments[1]).Text, card.Url!);

        handler.SendResult = new SentMessage(string.Empty) { IsSuccess = false, ErrorMessage = "adapter rejected" };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            facade.SendAsync(Channel.Direct("123"), [new TextSegment("plain")]));
        StringAssert.Contains(error.Message, "adapter rejected");
    }

    [TestMethod]
    public async Task ProductionFacade_MapsQueriedMessagesAndQqEntitiesToNumericOneBotJson()
    {
        var context = new FakeContext();
        var messages = DispatchProxy.Create<IMessageContext, FakeMessageContext>();
        var messageHandler = (FakeMessageContext)(object)messages;
        messageHandler.QueriedMessage = new MessageEvent
        {
            Platform = "qq", InstanceId = "first", MessageId = "889", Channel = Channel.Group("77"),
            Sender = new User("66") { Name = "sender" },
            Segments = [new MarkdownSegment("**hello**"), new CardSegment { Title = "Card", Fields = [new("Field", "Value")] }, new QuoteSegment("88")]
        };
        context.Message = messages;
        var system = DispatchProxy.Create<IQSystemApi, FakeSystemApi>();
        ((FakeSystemApi)(object)system).Friends = [new QFriend { UserId = "123", Nickname = "friend" }];
        var group = DispatchProxy.Create<IQGroupApi, FakeGroupApi>();
        ((FakeGroupApi)(object)group).Member = new QGroupMember { GroupId = "77", UserId = "66", Role = QGroupRole.Unknown };
        context.Extensions[typeof(IQSystemApi)] = system;
        context.Extensions[typeof(IQGroupApi)] = group;
        var facade = new OneBotContextFacade(context);

        var queried = JsonSerializer.SerializeToElement(await facade.GetAsync("889", Channel.Group("77")));
        Assert.AreEqual(JsonValueKind.Number, queried.GetProperty("sender").GetProperty("user_id").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, queried.GetProperty("group_id").ValueKind);
        Assert.AreEqual("markdown", queried.GetProperty("message")[0].GetProperty("type").GetString());
        Assert.AreEqual("**hello**", queried.GetProperty("message")[0].GetProperty("data").GetProperty("content").GetString());
        Assert.AreEqual("text", queried.GetProperty("message")[1].GetProperty("type").GetString());
        Assert.AreEqual("reply", queried.GetProperty("message")[2].GetProperty("type").GetString());
        Assert.AreEqual(JsonValueKind.Number, queried.GetProperty("message")[2].GetProperty("data").GetProperty("id").ValueKind);

        var friend = JsonSerializer.SerializeToElement(await facade.GetFriendsAsync(false)).EnumerateArray().Single();
        Assert.AreEqual(JsonValueKind.Number, friend.GetProperty("user_id").ValueKind);
        var member = JsonSerializer.SerializeToElement(await facade.GetGroupMemberAsync(77, 66, false));
        Assert.AreEqual(JsonValueKind.Number, member.GetProperty("group_id").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, member.GetProperty("user_id").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, member.GetProperty("role").ValueKind);
    }

    [TestMethod]
    public async Task Coordinator_RoutesOnlyExactInstanceAndRollsBackPartialStart()
    {
        var context = new FakeContext();
        var recorders = new List<RecordingRuntime>();
        var coordinator = new OneBotMultiInstanceRuntime(context, context.PluginDirectory, (_, _) =>
        {
            var runtime = new RecordingRuntime();
            recorders.Add(runtime);
            return runtime;
        });
        var config = new OneBotServerConfig { Instances =
        [
            new() { InstanceId = "first", Port = 5711 },
            new() { InstanceId = "second", Port = 5712 }
        ] };
        await coordinator.StartAsync(config, CancellationToken.None);
        await coordinator.PublishAsync(new BotOfflineEvent { Platform = "qq", InstanceId = "second" }, config.EventFormat, CancellationToken.None);
        await coordinator.PublishAsync(new BotOfflineEvent { Platform = "qq", InstanceId = null }, config.EventFormat, CancellationToken.None);
        await coordinator.PublishAsync(new BotOfflineEvent { Platform = "qq", InstanceId = "unbound" }, config.EventFormat, CancellationToken.None);
        Assert.AreEqual(0, recorders[0].Events.Count);
        Assert.AreEqual(1, recorders[1].Events.Count);
        await coordinator.StopAsync(CancellationToken.None);

        var failIndex = 0;
        var rollbackRuntimes = new List<RecordingRuntime>();
        var failing = new OneBotMultiInstanceRuntime(context, context.PluginDirectory, (_, _) =>
        {
            var runtime = new RecordingRuntime { FailStart = ++failIndex == 2 };
            rollbackRuntimes.Add(runtime);
            return runtime;
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.StartAsync(config, CancellationToken.None));
        Assert.AreEqual(1, rollbackRuntimes[0].StopCount);
        Assert.AreEqual(1, rollbackRuntimes[0].DisposeCount);
    }

    [TestMethod]
    public async Task Coordinator_StopAttemptsCleanupForEveryInstanceAfterOneFails()
    {
        var context = new FakeContext();
        var runtimes = new List<RecordingRuntime>();
        var coordinator = new OneBotMultiInstanceRuntime(context, context.PluginDirectory, (_, _) =>
        {
            var runtime = new RecordingRuntime { FailStop = runtimes.Count == 0 };
            runtimes.Add(runtime);
            return runtime;
        });
        await coordinator.StartAsync(new OneBotServerConfig { Instances =
        [new() { InstanceId = "first", Port = 5711 }, new() { InstanceId = "second", Port = 5712 }] }, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<AggregateException>(() => coordinator.StopAsync(CancellationToken.None));
        Assert.AreEqual(1, runtimes[0].DisposeCount);
        Assert.AreEqual(1, runtimes[1].StopCount);
        Assert.AreEqual(1, runtimes[1].DisposeCount);
    }

    [TestMethod]
    public async Task Service_RemovedConfiguredAdapterFailsReloadWithoutDefaultFallback()
    {
        var context = new FakeContext();
        var service = new OneBotServerService(new OneBotMultiInstanceRuntime(context, context.PluginDirectory,
            (_, _) => new RecordingRuntime()), new OneBotServerConfig { Instances =
        [
            new() { InstanceId = "first", Port = 5711 },
            new() { InstanceId = "second", Port = 5712 }
        ] });
        await service.StartAsync();
        context.Instances = [context.Instances[0]];

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ReconfigureAsync(new OneBotServerConfig { InstanceId = "second", Port = 5712 }));

        StringAssert.Contains(error.Message, "previous configuration could not be restored");
        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task Coordinator_HttpActionsInvokeTheSelectedAdapterInstance()
    {
        var context = new FakeContext();
        var firstPort = GetFreePort();
        var secondPort = GetFreePort();
        var coordinator = new OneBotMultiInstanceRuntime(context, context.PluginDirectory,
            instanceRuntimeFactory: (_, directory, instanceId) => new OneBotServerRuntime(
                InstanceScopedOneBotContextFacade.Bind(new HttpProbeFacade(context), context, instanceId), directory));
        var config = new OneBotServerConfig
        {
            Http = new() { Enabled = true },
            ForwardWebSocket = new() { Enabled = false },
            Instances = [new() { InstanceId = "first", Port = firstPort }, new() { InstanceId = "second", Port = secondPort }]
        };
        await coordinator.StartAsync(config, CancellationToken.None);
        try
        {
            using var client = new HttpClient();
            var firstResponse = await client.GetStringAsync($"http://127.0.0.1:{firstPort}/get_login_info");
            var secondResponse = await client.GetStringAsync($"http://127.0.0.1:{secondPort}/get_login_info");
            StringAssert.Contains(firstResponse, "first");
            StringAssert.Contains(secondResponse, "second");
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [TestMethod]
    public async Task Service_PortConflictDuringReloadRestoresPreviousHttpListener()
    {
        var context = new FakeContext { Instances = [new("first", "First", "qq", "QQ", "1", "qq", "onebot")] };
        var originalPort = GetFreePort();
        var occupiedPort = GetFreePort();
        using var occupiedListener = new TcpListener(System.Net.IPAddress.Loopback, occupiedPort);
        occupiedListener.Start();
        var service = new OneBotServerService(
            new OneBotMultiInstanceRuntime(context, context.PluginDirectory,
                instanceRuntimeFactory: (_, directory, instanceId) => new OneBotServerRuntime(
                    InstanceScopedOneBotContextFacade.Bind(new HttpProbeFacade(context), context, instanceId), directory)),
            new OneBotServerConfig
            {
                Http = new() { Enabled = true },
                ForwardWebSocket = new() { Enabled = false },
                Instances = [new() { InstanceId = "first", Port = originalPort }]
            });
        await service.StartAsync();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ReconfigureAsync(new OneBotServerConfig
            {
                Http = new() { Enabled = true },
                ForwardWebSocket = new() { Enabled = false },
                Instances = [new() { InstanceId = "first", Port = occupiedPort }]
            }));
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"http://127.0.0.1:{originalPort}/get_login_info");
            StringAssert.Contains(response, "first");
        }
        finally { await service.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Service_StopFailureDuringReloadAttemptsPreviousConfiguration()
    {
        var runtime = new LifecycleRuntime { FailNextStop = true };
        var original = new OneBotServerConfig { Port = 5711 };
        var service = new OneBotServerService(runtime, original);
        await service.StartAsync();

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ReconfigureAsync(original with { Port = 5712 }));

        StringAssert.Contains(error.Message, "previous configuration was restored");
        CollectionAssert.AreEqual(new[] { 5711, 5711 }, runtime.StartedPorts);
        Assert.AreEqual(1, runtime.StopCount);
        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task Service_DisposeStillDisposesRuntimeWhenStopFails()
    {
        var runtime = new LifecycleRuntime { FailEveryStop = true, FailDispose = true };
        var service = new OneBotServerService(runtime, new OneBotServerConfig());
        await service.StartAsync();

        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => service.DisposeAsync().AsTask());

        Assert.AreEqual(1, runtime.StopCount);
        Assert.AreEqual(1, runtime.DisposeCount);
        Assert.AreEqual(2, error.InnerExceptions.Count);
    }

    [TestMethod]
    public async Task Service_ReconfigureWaitsForEventDispatchToFinish()
    {
        var runtime = new LifecycleRuntime { BlockPublish = true };
        var service = new OneBotServerService(runtime, new OneBotServerConfig());
        await service.StartAsync();
        var publish = service.PublishAsync(new BotOfflineEvent { Platform = "qq", InstanceId = "first" });
        await runtime.PublishEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reload = service.ReconfigureAsync(new OneBotServerConfig { Port = 5711 });
        await Task.Delay(30);
        Assert.IsFalse(reload.IsCompleted);
        runtime.ReleasePublish.SetResult();
        await Task.WhenAll(publish, reload);
        Assert.AreEqual(1, runtime.Published.Count);
        await service.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingRuntime : IOneBotServerRuntime
    {
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public List<BotEvent> Events { get; } = [];
        public Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken) =>
            FailStart ? Task.FromException(new InvalidOperationException("test start failure")) : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return FailStop ? Task.FromException(new InvalidOperationException("stop failed")) : Task.CompletedTask;
        }
        public Task PublishAsync(BotEvent evt, OneBotEventFormatConfig format, CancellationToken cancellationToken) { Events.Add(evt); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class LifecycleRuntime : IOneBotServerRuntime
    {
        public bool FailNextStop { get; set; }
        public bool FailEveryStop { get; init; }
        public bool FailDispose { get; init; }
        public bool BlockPublish { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public List<int> StartedPorts { get; } = [];
        public List<BotEvent> Published { get; } = [];
        public TaskCompletionSource PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken) { StartedPorts.Add(config.Port); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            if (FailEveryStop || FailNextStop)
            {
                FailNextStop = false;
                return Task.FromException(new InvalidOperationException("stop failed"));
            }
            return Task.CompletedTask;
        }
        public async Task PublishAsync(BotEvent evt, OneBotEventFormatConfig format, CancellationToken cancellationToken)
        {
            PublishEntered.TrySetResult();
            if (BlockPublish) await ReleasePublish.Task.WaitAsync(cancellationToken);
            Published.Add(evt);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return FailDispose ? ValueTask.FromException(new InvalidOperationException("dispose failed")) : ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeProbeFacade(FakeContext context) : OneBotContextFacadeBase
    {
        public override async Task<string> GetSelfIdAsync()
        {
            await Task.Yield();
            context.ObservedAfterAwait = context.InstanceId;
            return "1";
        }
    }

    private sealed class HttpProbeFacade(FakeContext context) : OneBotContextFacadeBase
    {
        public override Task<string> GetSelfIdAsync() => Task.FromResult("10001");
        public override Task<object?> GetLoginInfoAsync() => Task.FromResult<object?>(new { instance_id = context.InstanceId });
    }

    private sealed class FakeContext : IBotContext
    {
        private readonly AsyncLocal<string?> _selected = new();
        public FakeContext() => PluginDirectory = Path.Combine(Path.GetTempPath(), "onebot-instance-tests");
        public IReadOnlyList<AdapterInstanceInfo> Instances { get; set; } =
        [
            new("first", "First", "qq", "QQ", "1", "qq", "onebot"),
            new("second", "Second", "qq", "QQ", "1", "qq", "onebot")
        ];
        public string? ObservedAfterAwait { get; set; }
        public string Platform => "qq";
        public string? InstanceId => _selected.Value ?? "outer";
        public AdapterInstanceInfo? AdapterInstance => Instances.FirstOrDefault(item => item.Id == InstanceId);
        public IReadOnlyList<AdapterInstanceInfo> GetAdapterInstances() => Instances;
        public IMessageContext Message { get; set; } = null!;
        public Dictionary<Type, object> Extensions { get; } = [];
        public IChannelService Channel => null!;
        public IUserService User => null!;
        public IUpdater Updater => null!;
        public IConfigContext Config => null!;
        public IWebHostContext WebHost => null!;
        public IPluginServices Services => null!;
        public string PluginDirectory { get; }
        public IReadOnlyList<UserReference> OwnerList => [];
        public IReadOnlyList<UserReference> AdminList => [];
        public TService? GetAdapterExtension<TService>() where TService : class => Extensions.GetValueOrDefault(typeof(TService)) as TService;
        public IDisposable UseInstance(string instanceId)
        {
            var previous = _selected.Value;
            _selected.Value = instanceId;
            return new RestoreScope(_selected, previous);
        }
        public IRenderContext? Render => null;
    }

    public class FakeMessageContext : DispatchProxy
    {
        public MessageCapabilities Capabilities { get; set; } = new() { NativeFeatures = MessageFeatures.Text };
        public OutgoingMessage? LastOutgoing { get; private set; }
        public PreparedMessage? LastPrepared { get; private set; }
        public SentMessage SendResult { get; set; } = new("native-id");
        public MessageEvent? QueriedMessage { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMessageContext.GetMessageCapabilities)) return Capabilities;
            if (targetMethod?.Name == nameof(IMessageContext.GetMessageAsync)) return Task.FromResult(QueriedMessage);
            if (targetMethod?.Name == nameof(IMessageContext.SendMessageAsync) && args is { Length: >= 2 })
            {
                if (args[1] is OutgoingMessage outgoing)
                {
                    LastOutgoing = outgoing;
                    LastPrepared = MessagePreparation.Prepare(outgoing, Capabilities);
                    return Task.FromResult(SendResult with { Transformations = LastPrepared.Transformations });
                }
                if (args[1] is IReadOnlyList<MessageSegment>) return Task.FromResult(SendResult);
            }
            throw new NotSupportedException($"Unexpected fake message API call: {targetMethod?.Name}");
        }
    }

    public class FakeSystemApi : DispatchProxy
    {
        public IReadOnlyList<QFriend> Friends { get; set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IQSystemApi.GetFriendListAsync) => Task.FromResult(Friends),
            _ => throw new NotSupportedException($"Unexpected fake QQ system API call: {targetMethod?.Name}")
        };
    }

    public class FakeGroupApi : DispatchProxy
    {
        public QGroupMember Member { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IQGroupApi.GetGroupMemberInfoAsync) => Task.FromResult(Member),
            _ => throw new NotSupportedException($"Unexpected fake QQ group API call: {targetMethod?.Name}")
        };
    }

    private sealed class RestoreScope(AsyncLocal<string?> selected, string? previous) : IDisposable
    {
        public void Dispose() => selected.Value = previous;
    }
}
