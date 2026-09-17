using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.Plugin.OneBotServer.Plugin;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Tests.Plugin;

[TestClass]
public sealed class OneBotRuntimeAndServiceTests
{
    [TestMethod]
    public async Task Runtime_FailedStartRollsBackAndCanStartAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runtime = new OneBotServerRuntime(new RuntimeFacade(), directory);
        var invalid = DisabledConfig() with { ReverseWebSocket = new() { Enabled = true, Url = "invalid" } };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.StartAsync(invalid, CancellationToken.None));
        await runtime.StartAsync(DisabledConfig(), CancellationToken.None);
        await runtime.StopAsync(CancellationToken.None);

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Runtime_PrefersLiveSelfIdAndPersistsInboundMessageReference()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var facade = new RuntimeFacade();
        var runtime = new OneBotServerRuntime(facade, directory);
        var config = DisabledConfig();
        await runtime.StartAsync(config, CancellationToken.None);

        await runtime.PublishAsync(new MessageEvent
        {
            Platform = "qq",
            SelfId = null,
            MessageId = "88",
            Channel = Channel.Group("77"),
            Sender = new User("66"),
            Segments = [new TextSegment("hello")],
        }, config.EventFormat, CancellationToken.None);
        await runtime.StopAsync(CancellationToken.None);

        var registry = await Infrastructure.MessageIdRegistry.OpenAsync(Path.Combine(directory, "data", "message-ids.json"), 10);
        Assert.AreEqual(1, facade.SelfIdCalls);
        Assert.IsTrue(registry.TryGet(new(Infrastructure.MessageScene.Group, 77, 88), out var messageId));
        Assert.AreEqual(1, messageId);
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void Configuration_ExposesRegistryRequestSecretAndHttpTargetSecret()
    {
        var config = new OneBotServerConfig
        {
            Storage = new() { RegistryMaxEntries = 123, RequestFlagSecret = "secret" },
            HttpTargets = [new() { Url = "https://example.test", Secret = "callback-secret" }],
        };

        Assert.AreEqual(123, config.Storage.RegistryMaxEntries);
        Assert.AreEqual("secret", config.Storage.RequestFlagSecret);
        Assert.AreEqual("callback-secret", config.HttpTargets.Single().Secret);
    }

    [TestMethod]
    public async Task Service_ReconfigureIsSerializedAndDisposePreventsRestart()
    {
        var runtime = new RecordingRuntime();
        var service = new OneBotServerService(runtime, DisabledConfig() with { Enabled = true });
        await service.StartAsync();

        var first = service.ReconfigureAsync(DisabledConfig() with { Enabled = true, Port = 5701 });
        await runtime.SecondStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.ReconfigureAsync(DisabledConfig() with { Enabled = true, Port = 5702 });
        Assert.IsFalse(second.IsCompleted);
        runtime.ReleaseSecondStart.SetResult();
        await Task.WhenAll(first, second);
        var startsBeforeDispose = runtime.StartCount;
        await service.DisposeAsync();

        Assert.AreEqual(3, startsBeforeDispose);
        Assert.AreEqual(startsBeforeDispose, runtime.StartCount);
        Assert.AreEqual(3, runtime.StopCount);
    }

    [TestMethod]
    public async Task Service_FailedReconfigureRestoresPreviousConfiguration()
    {
        var runtime = new RecordingRuntime { FailPort = 5702 };
        var original = DisabledConfig() with { Port = 5701 };
        var service = new OneBotServerService(runtime, original);
        await service.StartAsync();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ReconfigureAsync(original with { Port = 5702 }));

        StringAssert.Contains(exception.Message, "previous configuration was restored");
        CollectionAssert.AreEqual(new[] { 5701, 5702, 5701 }, runtime.StartedPorts);
        Assert.AreEqual(1, runtime.StopCount);
        await service.DisposeAsync();
        Assert.AreEqual(2, runtime.StopCount);
    }

    private static OneBotServerConfig DisabledConfig() => new()
    {
        Enabled = true,
        SelfId = "42",
        Http = new() { Enabled = false },
        ForwardWebSocket = new() { Enabled = false },
        Storage = new() { MessageIdRegistryPath = "data/message-ids.json", RegistryMaxEntries = 10 },
    };

    private sealed class RuntimeFacade : OneBotContextFacadeBase
    {
        public int SelfIdCalls { get; private set; }
        public override Task<string> GetSelfIdAsync() { SelfIdCalls++; return Task.FromResult("10001"); }
    }

    private sealed class RecordingRuntime : IOneBotServerRuntime
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int? FailPort { get; init; }
        public List<int> StartedPorts { get; } = [];
        public TaskCompletionSource SecondStartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken)
        {
            StartCount++;
            StartedPorts.Add(config.Port);
            if (config.Port == FailPort) throw new InvalidOperationException("Port is unavailable.");
            if (StartCount == 2)
            {
                SecondStartEntered.SetResult();
                await ReleaseSecondStart.Task.WaitAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) { StopCount++; return Task.CompletedTask; }
        public Task PublishAsync(BotEvent evt, OneBotEventFormatConfig format, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
