using System.Security.Cryptography;
using System.Text;
using ShiroBot.Plugin.OneBotServer.Events;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed class OneBotRuntimeState
{
    public OneBotRuntimeState(MessageIdRegistry messages, int maxEntries, int eventQueueCapacity, byte[] requestFlagKey)
    {
        Messages = messages;
        Files = new PrivateFileRegistry(maxEntries);
        Reactions = new ReactionRegistry(maxEntries);
        Events = new EventQueue<OneBotEvent>(eventQueueCapacity);
        RequestFlagKey = requestFlagKey;
    }

    public MessageIdRegistry Messages { get; }
    public PrivateFileRegistry Files { get; }
    public ReactionRegistry Reactions { get; }
    public EventQueue<OneBotEvent> Events { get; }
    public byte[] RequestFlagKey { get; }

    public static byte[] DeriveRequestFlagKey(string? configuredSecret, string? accessToken)
    {
        var material = !string.IsNullOrWhiteSpace(configuredSecret) ? configuredSecret : accessToken ?? string.Empty;
        return SHA256.HashData(Encoding.UTF8.GetBytes("ShiroBot.Plugin.OneBotServer/request-flags/v1\0" + material));
    }
}
