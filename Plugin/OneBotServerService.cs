using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

/// <summary>Owns the runtime lifecycle and converts ShiroBot events before transport dispatch.</summary>
public sealed class OneBotServerService : IAsyncDisposable
{
    private readonly IOneBotServerRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private OneBotServerConfig _config;
    private bool _started;

    public OneBotServerService(IOneBotServerRuntime runtime, OneBotServerConfig config)
    {
        _runtime = runtime;
        _config = config;
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (_started || !_config.Enabled) return;
            await _runtime.StartAsync(_config, _shutdown.Token).ConfigureAwait(false);
            _started = true;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task PublishAsync(BotEvent evt)
    {
        if (!_started) return;
        await _runtime.PublishAsync(evt, _config.EventFormat, _shutdown.Token).ConfigureAwait(false);
    }

    public async Task ReconfigureAsync(OneBotServerConfig config)
    {
        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            _config = config;
            if (_started)
            {
                await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _started = false;
            }
            if (_shutdown.IsCancellationRequested || !_config.Enabled) return;
            await _runtime.StartAsync(_config, _shutdown.Token).ConfigureAwait(false);
            _started = true;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started) await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _started = false;
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
            _shutdown.Dispose();
        }
    }
}
