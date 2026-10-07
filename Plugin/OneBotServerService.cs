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
        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (!_started || _shutdown.IsCancellationRequested) return;
            await _runtime.PublishAsync(evt, _config.EventFormat, _shutdown.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ReconfigureAsync(OneBotServerConfig config)
    {
        await _lifecycle.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            var previous = _config;
            var wasStarted = _started;
            try
            {
                if (_started)
                {
                    _started = false;
                    await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (_shutdown.IsCancellationRequested) return;
                if (config.Enabled)
                {
                    await _runtime.StartAsync(config, _shutdown.Token).ConfigureAwait(false);
                    _started = true;
                }
                _config = config;
            }
            catch (Exception reloadError)
            {
                _started = false;
                _config = previous;
                if (wasStarted && previous.Enabled)
                {
                    try
                    {
                        await _runtime.StartAsync(previous, _shutdown.Token).ConfigureAwait(false);
                        _started = true;
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(
                            $"OneBot Server configuration reload failed and the previous configuration could not be restored. Reload: {reloadError.Message}; rollback: {rollbackError.Message}",
                            new AggregateException(reloadError, rollbackError));
                    }
                }

                throw new InvalidOperationException(
                    "OneBot Server configuration reload failed; the previous configuration was restored.",
                    reloadError);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        List<Exception>? errors = null;
        try
        {
            if (_started)
            {
                _started = false;
                try { await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
            }
            try { await _runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
            _shutdown.Dispose();
        }
        if (errors is { Count: > 0 }) throw new AggregateException("One or more OneBot service resources failed to dispose cleanly.", errors);
    }
}
