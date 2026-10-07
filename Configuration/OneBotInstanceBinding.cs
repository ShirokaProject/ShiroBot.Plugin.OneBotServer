using System.Net;
using System.Security.Cryptography;
using System.Text;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.OneBotServer.Configuration;

public sealed record BoundOneBotInstance(string InstanceId, OneBotServerConfig Config);

/// <summary>Resolves endpoint configuration to loaded adapter instances and prevents ambiguous listeners.</summary>
public static class OneBotInstanceBinding
{
    public static IReadOnlyList<BoundOneBotInstance> Resolve(
        OneBotServerConfig config,
        IReadOnlyList<AdapterInstanceInfo> availableInstances,
        string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availableInstances);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (config.Instances.Count != 0 && !string.IsNullOrWhiteSpace(config.InstanceId))
            throw new InvalidOperationException("Configure either instance_id or instances, not both.");
        if (config.Instances.Count > 1 && !string.IsNullOrWhiteSpace(config.SelfId) && config.SelfId != "0")
            throw new InvalidOperationException("self_id cannot be shared across multiple adapter instances; leave it as '0' so each instance resolves its own login ID.");

        (string? InstanceId, string? Host, int? Port, string? AccessToken)[] configured = config.Instances.Count != 0
            ? config.Instances.Select(entry => (InstanceId: (string?)entry.InstanceId, entry.Host, entry.Port, entry.AccessToken)).ToArray()
            : new[] { (string.IsNullOrWhiteSpace(config.InstanceId) ? null : config.InstanceId, (string?)null, (int?)null, (string?)null) };

        if (config.Instances.Count == 0 && configured[0].InstanceId is null)
        {
            if (availableInstances.Count != 1)
                throw new InvalidOperationException("OneBot instance_id is required when the host has zero or multiple adapter instances.");
            configured[0].InstanceId = availableInstances[0].Id;
        }

        var result = new List<BoundOneBotInstance>(configured.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instanceIdValue, hostOverride, portOverride, accessTokenOverride) in configured)
        {
            var instanceId = instanceIdValue;
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new InvalidOperationException("Every OneBot instances entry must specify instance_id.");
            instanceId = instanceId.Trim();
            if (!seen.Add(instanceId))
                throw new InvalidOperationException($"OneBot adapter instance '{instanceId}' is configured more than once.");

            var instance = availableInstances.SingleOrDefault(item => string.Equals(item.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
                throw new InvalidOperationException($"Configured OneBot adapter instance '{instanceId}' is not loaded.");
            instanceId = instance.Id;
            var host = NormalizeHost(hostOverride ?? config.Host);
            var port = portOverride ?? config.Port;
            if (port is < 1 or > 65535)
                throw new InvalidOperationException($"OneBot listener port for adapter instance '{instanceId}' must be between 1 and 65535.");

            var storagePath = GetInstanceRegistryPath(pluginDirectory, config.Storage.MessageIdRegistryPath, instanceId);
            var legacyRegistryPath = Path.GetFullPath(config.Storage.MessageIdRegistryPath, pluginDirectory);
            var scopedConfig = config with
            {
                LegacyMessageIdRegistryPath = legacyRegistryPath,
                InstanceId = instanceId,
                Instances = [],
                Host = host,
                Port = port,
                AccessToken = accessTokenOverride ?? config.AccessToken,
                Storage = config.Storage with { MessageIdRegistryPath = storagePath }
            };
            result.Add(new BoundOneBotInstance(instanceId, scopedConfig));
        }

        ValidateEndpointConflicts(result);
        return result;
    }

    public static string GetInstanceStorageDirectory(string pluginDirectory, string instanceId) =>
        Path.Combine(pluginDirectory, "storage", "instances", SafeInstanceName(instanceId));

    public static string GetInstanceRegistryPath(string pluginDirectory, string configuredPath, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        var fullPath = Path.GetFullPath(configuredPath, pluginDirectory);
        var directory = Path.GetDirectoryName(fullPath) ?? pluginDirectory;
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "message-ids.json";
        return Path.Combine(directory, "instances", SafeInstanceName(instanceId), fileName);
    }

    private static string SafeInstanceName(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId.ToUpperInvariant())));
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("OneBot listener host cannot be empty.");
        host = host.Trim();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        if (host is "*" or "+") return host;
        if (host.Contains('/') || host.Contains('@') || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new InvalidOperationException($"Invalid OneBot listener host '{host}'.");
        return host.TrimEnd('.');
    }

    private static void ValidateEndpointConflicts(IReadOnlyList<BoundOneBotInstance> instances)
    {
        for (var left = 0; left < instances.Count; left++)
        for (var right = left + 1; right < instances.Count; right++)
        {
            var a = instances[left].Config;
            var b = instances[right].Config;
            var aListensLocally = a.Http.Enabled || a.ForwardWebSocket.Enabled;
            var bListensLocally = b.Http.Enabled || b.ForwardWebSocket.Enabled;
            if (!aListensLocally || !bListensLocally) continue;
            if (a.Port == b.Port && HostsOverlap(a.Host, b.Host))
                throw new InvalidOperationException($"OneBot adapter instances '{instances[left].InstanceId}' and '{instances[right].InstanceId}' have conflicting listeners on {a.Host}:{a.Port}.");
        }
    }

    private static bool HostsOverlap(string left, string right)
    {
        var a = left.Trim('[', ']').ToLowerInvariant();
        var b = right.Trim('[', ']').ToLowerInvariant();
        if (IsWildcard(a) || IsWildcard(b) || a == b) return true;
        if (a == "localhost" && IsLoopback(b) || b == "localhost" && IsLoopback(a)) return true;
        return false;
    }

    private static bool IsWildcard(string host) => host is "*" or "+" or "0.0.0.0" or "::";
    private static bool IsLoopback(string host) => IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
