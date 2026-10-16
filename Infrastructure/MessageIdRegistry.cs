using System.Text.Json;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public enum MessageScene { Friend, Group, Temp }
public sealed record OneBotMessageReference(MessageScene Scene, long PeerId, long Sequence);

public sealed class MessageIdRegistry
{
    private const int MaxMessageId = int.MaxValue;
    private readonly string path;
    private readonly int maxEntries;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Dictionary<int, OneBotMessageReference> byId = [];
    private readonly Dictionary<OneBotMessageReference, int> byReference = [];
    private readonly Queue<int> insertionOrder = [];
    private int nextId = 1;

    private readonly int minimumNextId;
    private MessageIdRegistry(string path, int maxEntries, int minimumNextId) { this.path = path; this.maxEntries = maxEntries; this.minimumNextId = minimumNextId; }

    public int Count { get { lock (stateLock) return byId.Count; } }
    public static async Task<MessageIdRegistry> OpenAsync(string path, int maxEntries = 100_000, CancellationToken cancellationToken = default, int minimumNextId = 1)
    {
        if (maxEntries is < 1 or > MaxMessageId) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (minimumNextId is < 1 or > MaxMessageId) throw new ArgumentOutOfRangeException(nameof(minimumNextId));
        var registry = new MessageIdRegistry(Path.GetFullPath(path), maxEntries, minimumNextId);
        await registry.LoadAsync(cancellationToken);
        return registry;
    }

    public static async Task<int> ReadNextIdAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 1;
        PersistedRegistry? persisted;
        try { persisted = await ReadRegistryAsync(path, cancellationToken); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid legacy message ID registry JSON", exception); }
        if (persisted is null || persisted.Version != 1 || persisted.NextId is < 1 or > MaxMessageId || persisted.Entries is null)
            throw new InvalidDataException("Invalid legacy message ID registry format");
        var historicalMax = 0;
        foreach (var entry in persisted.Entries)
        {
            if (entry.Id is < 1 or > MaxMessageId) throw new InvalidDataException("Invalid legacy message ID registry entry");
            historicalMax = Math.Max(historicalMax, entry.Id);
        }
        return historicalMax < MaxMessageId ? Math.Max(persisted.NextId, historicalMax + 1) : persisted.NextId;
    }

    public bool TryResolve(int messageId, out OneBotMessageReference? reference) { lock (stateLock) return byId.TryGetValue(messageId, out reference); }
    public bool TryGet(OneBotMessageReference reference, out int messageId) { lock (stateLock) return byReference.TryGetValue(reference, out messageId); }

    public async Task<int> RegisterAsync(OneBotMessageReference reference, CancellationToken cancellationToken = default)
    {
        Validate(reference);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (stateLock)
            {
                if (byReference.TryGetValue(reference, out var existing)) return existing;
            }

            int nextCandidate;
            int id;
            bool evictOldest;
            lock (stateLock)
            {
                (id, nextCandidate) = Allocate(byId, nextId);
                evictOldest = byId.Count >= maxEntries;
            }

            // The gate serializes writers. Readers continue to see the previous
            // committed state until the atomic replacement succeeds.
            await PersistAsync(byId, insertionOrder, nextCandidate, cancellationToken,
                new KeyValuePair<int, OneBotMessageReference>(id, reference), evictOldest);
            lock (stateLock)
            {
                if (evictOldest)
                {
                    var expired = insertionOrder.Dequeue();
                    byReference.Remove(byId[expired]);
                    byId.Remove(expired);
                }
                byId.Add(id, reference);
                byReference.Add(reference, id);
                insertionOrder.Enqueue(id);
                nextId = nextCandidate;
            }
            return id;
        }
        finally { gate.Release(); }
    }

    private static (int Id, int NextId) Allocate(IReadOnlyDictionary<int, OneBotMessageReference> entries, int firstCandidate)
    {
        var candidate = firstCandidate;
        for (var attempts = 0; attempts <= entries.Count; attempts++)
        {
            var current = candidate;
            candidate = current == MaxMessageId ? 1 : current + 1;
            if (!entries.ContainsKey(current)) return (current, candidate);
        }
        throw new InvalidOperationException("OneBot message ID space is exhausted");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            nextId = minimumNextId;
            if (minimumNextId > 1) await PersistAsync(byId, insertionOrder, nextId, cancellationToken);
            return;
        }
        PersistedRegistry? persisted;
        try { persisted = await ReadRegistryAsync(path, cancellationToken); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid message ID registry JSON", exception); }
        if (persisted is null || persisted.Version != 1 || persisted.NextId is < 1 or > MaxMessageId || persisted.Entries is null) throw new InvalidDataException("Invalid message ID registry format");
        foreach (var entry in persisted.Entries.TakeLast(maxEntries))
        {
            var reference = new OneBotMessageReference(entry.Scene, entry.PeerId, entry.Sequence);
            Validate(reference);
            if (entry.Id is < 1 or > MaxMessageId || !byId.TryAdd(entry.Id, reference) || !byReference.TryAdd(reference, entry.Id)) throw new InvalidDataException("Duplicate or invalid message ID registry entry");
            insertionOrder.Enqueue(entry.Id);
        }
        nextId = persisted.NextId;
    }

    private static async Task<PersistedRegistry?> ReadRegistryAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PersistedRegistry>(stream, cancellationToken: cancellationToken);
    }

    private async Task PersistAsync(IReadOnlyDictionary<int, OneBotMessageReference> entries, IEnumerable<int> order,
        int candidate, CancellationToken cancellationToken,
        KeyValuePair<int, OneBotMessageReference>? added = null, bool skipOldest = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("Version", 1);
                writer.WriteNumber("NextId", candidate);
                writer.WriteStartArray("Entries");
                var index = 0;
                foreach (var messageId in order)
                {
                    if (skipOldest) { skipOldest = false; continue; }
                    WriteEntry(writer, messageId, entries[messageId]);
                    if (++index % 64 == 0) await writer.FlushAsync(cancellationToken);
                }
                if (added is { } item) WriteEntry(writer, item.Key, item.Value);
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteEntry(Utf8JsonWriter writer, int id, OneBotMessageReference reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Id", id);
        writer.WriteNumber("Scene", (int)reference.Scene);
        writer.WriteNumber("PeerId", reference.PeerId);
        writer.WriteNumber("Sequence", reference.Sequence);
        writer.WriteEndObject();
    }

    private static void Validate(OneBotMessageReference reference)
    {
        if (reference.PeerId < 0 || reference.Sequence < 0) throw new ArgumentOutOfRangeException(nameof(reference), "Message reference values must be non-negative");
    }

    private sealed record PersistedRegistry(int Version, int NextId, PersistedEntry[] Entries);
    private sealed record PersistedEntry(int Id, MessageScene Scene, long PeerId, long Sequence);
}
