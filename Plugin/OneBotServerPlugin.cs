using ShiroBot.SDK.Config;
using ShiroBot.Plugin.OneBotServer.Bridges;
using ShiroBot.Plugin.OneBotServer.Configuration;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

[BotPlugin(
    "OneBotServer",
    Name = "OneBot Server",
    Version = "0.2.2",
    Author = "ShirokaProject",
    Category = PluginCategory.Utility,
    Description = "Exposes ShiroBot QQ events and actions through the OneBot v11 protocol.",
    GithubRepo = "ShirokaProject/ShiroBot.Plugin.OneBotServer",
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed class OneBotServerPlugin : PluginBase<OneBotServerConfig>
{
    private OneBotServerConfig _config = new();
    private OneBotServerService? _service;
    private readonly CancellationTokenSource _unloading = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private string _configurationFingerprint = string.Empty;

    protected override void ConfigureRoutes() => Events.Map<BotEvent>(PublishEventAsync);

    protected override async Task LoadAsync()
    {
        RemoveObsoleteRelayLimits(Context.Config.ConfigPath);
        _config = Settings;
        Context.Config.Save(_config);
        _configurationFingerprint = Fingerprint(_config);
        _service = new OneBotServerService(new OneBotMultiInstanceRuntime(Context, Context.PluginDirectory), _config);
        await _service.StartAsync().ConfigureAwait(false);
        BotLog.Info("OneBot Server plugin loaded.");
    }

    protected override async Task OnUnloadAsync()
    {
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

    protected override async Task OnConfigChangedAsync(
        OneBotServerConfig previous, OneBotServerConfig current, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _unloading.Token.ThrowIfCancellationRequested();
            var fingerprint = Fingerprint(current);
            if (string.Equals(fingerprint, _configurationFingerprint, StringComparison.Ordinal)) return;
            if (_service is null) throw new InvalidOperationException("OneBot service is not running.");
            await _service.ReconfigureAsync(current).ConfigureAwait(false);
            _config = current;
            _configurationFingerprint = fingerprint;
            BotLog.Info($"OneBot Server configuration reloaded for {current.Instances.Count} configured instance(s).");
        }
        finally { _configurationGate.Release(); }
    }

    private static string Fingerprint(OneBotServerConfig config) => JsonSerializer.Serialize(config);

    private static void RemoveObsoleteRelayLimits(string configPath)
    {
        if (!File.Exists(configPath)) return;

        var lines = File.ReadAllLines(configPath);
        var filtered = lines.Where(line =>
        {
            var value = line.TrimStart();
            return !value.StartsWith("max_request_body_bytes", StringComparison.OrdinalIgnoreCase) &&
                   !value.StartsWith("max_web_socket_message_bytes", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (filtered.Length == lines.Length) return;

        var temporaryPath = configPath + ".migrate-" + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temporaryPath, filtered);
        File.Move(temporaryPath, configPath, overwrite: true);
        BotLog.Info("OneBot Server removed obsolete HTTP/WebSocket size limit settings; relay payloads are now passed through without a plugin-side size limit.");
    }
}
