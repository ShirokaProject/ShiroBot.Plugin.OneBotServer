using ShiroBot.Plugin.OneBotServer.Configuration;
using ShiroBot.Plugin.OneBotServer.Events;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Plugin;

/// <summary>Transport boundary implemented by the HTTP/WebSocket integration.</summary>
public interface IOneBotServerRuntime : IAsyncDisposable
{
    Task StartAsync(OneBotServerConfig config, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task PublishAsync(BotEvent evt, OneBotEventFormatConfig format, CancellationToken cancellationToken);
}
