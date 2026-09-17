using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

[BotPlugin(
    "OneBotServer",
    Name = "OneBot Server",
    Version = "0.1.0",
    Author = "ShirokaProject",
    Category = PluginCategory.Utility,
    Description = "Exposes ShiroBot QQ events and actions through the OneBot v11 protocol.",
    GithubRepo = "ShirokaProject/ShiroBot.Plugin.OneBotServer",
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed class OneBotServerPlugin : PluginBase
{
    private OneBotServerConfig _config = new();
    private OneBotServerService? _service;
    private IDisposable? _configWatcher;
    private readonly CancellationTokenSource _unloading = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);

    protected override void ConfigureRoutes() => Events.Map<BotEvent>(PublishEventAsync);

    protected override async Task LoadAsync()
    {
        _config = Context.Config.Load<OneBotServerConfig>();
        Context.Config.Save(_config);
        _service = new OneBotServerService(new OneBotServerRuntime(new OneBotContextFacade(Context), Context.PluginDirectory), _config);
        _configWatcher = Context.Config.Watch<OneBotServerConfig>(ApplyConfiguration);
        await _service.StartAsync().ConfigureAwait(false);
        BotLog.Info("OneBot Server plugin loaded.");
    }

    protected override async Task OnUnloadAsync()
    {
        _configWatcher?.Dispose();
        await _unloading.CancelAsync().ConfigureAwait(false);
        await _configurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_service is not null) await _service.DisposeAsync().ConfigureAwait(false);
        }
        finally { _configurationGate.Release(); }
        BotLog.Info("OneBot Server plugin unloaded.");
    }

    private Task PublishEventAsync(BotEvent evt) => _service?.PublishAsync(evt) ?? Task.CompletedTask;

    private void ApplyConfiguration(OneBotServerConfig config)
    {
        _config = config;
        if (_service is not null && !_unloading.IsCancellationRequested) _ = ReconfigureAsync(config);
    }

    private async Task ReconfigureAsync(OneBotServerConfig config)
    {
        try
        {
            await _configurationGate.WaitAsync(_unloading.Token).ConfigureAwait(false);
            try
            {
                if (!_unloading.IsCancellationRequested) await _service!.ReconfigureAsync(config).ConfigureAwait(false);
            }
            finally { _configurationGate.Release(); }
        }
        catch (OperationCanceledException) when (_unloading.IsCancellationRequested) { }
        catch (Exception exception)
        {
            BotLog.Error($"Failed to apply OneBot Server configuration: {exception}");
        }
    }
}
