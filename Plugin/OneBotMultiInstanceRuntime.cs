using System.Security.Cryptography;
using System.Text;
using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

/// <summary>Runs one independently scoped OneBot listener for each configured adapter instance.</summary>
public sealed class OneBotMultiInstanceRuntime : IOneBotServerRuntime
{
    private readonly IBotContext _context;
    private readonly string _pluginDirectory;
    private readonly Func<IOneBotContextFacade, string, string, IOneBotServerRuntime> _runtimeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, IOneBotServerRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    public OneBotMultiInstanceRuntime(
        IBotContext context,
        string pluginDirectory,
        Func<IOneBotContextFacade, string, IOneBotServerRuntime>? runtimeFactory = null,
        Func<IOneBotContextFacade, string, string, IOneBotServerRuntime>? instanceRuntimeFactory = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pluginDirectory = pluginDirectory ?? throw new ArgumentNullException(nameof(pluginDirectory));
        _runtimeFactory = instanceRuntimeFactory ?? ((facade, directory, _) =>
            runtimeFactory?.Invoke(facade, directory) ?? new OneBotServerRuntime(facade, directory));
    }

    public async Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtimes.Count != 0) throw new InvalidOperationException("OneBot instance listeners are already running.");
            var bindings = OneBotInstanceBinding.Resolve(config, _context.GetAdapterInstances(), _pluginDirectory);
            var started = new Dictionary<string, IOneBotServerRuntime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var binding in bindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var facade = InstanceScopedOneBotContextFacade.Bind(
                        new OneBotContextFacade(_context), _context, binding.InstanceId);
                    var runtime = _runtimeFactory(facade, _pluginDirectory, binding.InstanceId);
                    try
                    {
                        await runtime.StartAsync(WithInstanceSecret(binding.Config, binding.InstanceId), cancellationToken).ConfigureAwait(false);
                        started.Add(binding.InstanceId, runtime);
                    }
                    catch
                    {
                        try { await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                        await runtime.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                _runtimes = started;
            }
            catch
            {
                foreach (var runtime in started.Values)
                {
                    try { await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    try { await runtime.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimes = _runtimes.Values.ToArray();
            _runtimes = new Dictionary<string, IOneBotServerRuntime>(StringComparer.OrdinalIgnoreCase);
            List<Exception>? errors = null;
            foreach (var runtime in runtimes)
            {
                try { await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
                try { await runtime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
            }
            if (errors is { Count: > 0 }) throw new AggregateException("One or more OneBot instance listeners failed to stop cleanly.", errors);
        }
        finally { _gate.Release(); }
    }

    public async Task PublishAsync(BotEvent evt, OneBotEventFormatConfig format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var instanceId = evt.InstanceId;
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Hold the gate through dispatch so Stop/Reconfigure cannot dispose a runtime in flight.
            if (_runtimes.TryGetValue(instanceId, out var runtime))
                await runtime.PublishAsync(evt, format, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }

    private static OneBotServerConfig WithInstanceSecret(OneBotServerConfig config, string instanceId)
    {
        var secret = config.Storage.RequestFlagSecret ?? config.AccessToken ?? string.Empty;
        var scoped = SHA256.HashData(Encoding.UTF8.GetBytes($"{secret}\0onebot-instance:{instanceId.ToUpperInvariant()}"));
        return config with { Storage = config.Storage with { RequestFlagSecret = Convert.ToHexString(scoped) } };
    }
}
