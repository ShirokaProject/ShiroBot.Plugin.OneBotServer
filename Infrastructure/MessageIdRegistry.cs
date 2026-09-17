using System.Text.Json;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public enum MessageScene { Friend, Group, Temp }
public sealed record MessageReference(MessageScene Scene, long PeerId, long Sequence);

public sealed class MessageIdRegistry
{
    private const int MaxMessageId = int.MaxValue;
    private readonly string path;
    private readonly int maxEntries;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Dictionary<int, MessageReference> byId = [];
    private readonly Dictionary<MessageReference, int> byReference = [];
    private readonly Queue<int> insertionOrder = [];
    private int nextId = 1;

    private MessageIdRegistry(string path, int maxEntries) { this.path = path; this.maxEntries = maxEntries; }

    public int Count { get { lock (stateLock) return byId.Count; } }
    public static async Task<MessageIdRegistry> OpenAsync(string path, int maxEntries = 100_000, CancellationToken cancellationToken = default)
    {
        if (maxEntries is < 1 or > MaxMessageId) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var registry = new MessageIdRegistry(Path.GetFullPath(path), maxEntries);
        await registry.LoadAsync(cancellationToken);
        return registry;
    }

    public bool TryResolve(int messageId, out MessageReference? reference) { lock (stateLock) return byId.TryGetValue(messageId, out reference); }
    public bool TryGet(MessageReference reference, out int messageId) { lock (stateLock) return byReference.TryGetValue(reference, out messageId); }

    public async Task<int> RegisterAsync(MessageReference reference, CancellationToken cancellationToken = default)
    {
        Validate(reference);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (stateLock)
            {
                if (byReference.TryGetValue(reference, out var existing)) return existing;
            }

            Dictionary<int, MessageReference> nextById;
            Dictionary<MessageReference, int> nextByReference;
            Queue<int> nextOrder;
            int nextCandidate;
            int id;
            lock (stateLock)
            {
                nextById = new Dictionary<int, MessageReference>(byId);
                nextByReference = new Dictionary<MessageReference, int>(byReference);
                nextOrder = new Queue<int>(insertionOrder);
                (id, nextCandidate) = Allocate(nextById, nextId);
                nextById.Add(id, reference);
                nextByReference.Add(reference, id);
                nextOrder.Enqueue(id);
                while (nextById.Count > maxEntries)
                {
                    var expired = nextOrder.Dequeue();
                    nextByReference.Remove(nextById[expired]);
                    nextById.Remove(expired);
                }
            }

            await PersistAsync(nextById, nextOrder, nextCandidate, cancellationToken);
            lock (stateLock)
            {
                byId.Clear();
                byReference.Clear();
                insertionOrder.Clear();
                foreach (var pair in nextById) byId.Add(pair.Key, pair.Value);
                foreach (var pair in nextByReference) byReference.Add(pair.Key, pair.Value);
                foreach (var messageId in nextOrder) insertionOrder.Enqueue(messageId);
                nextId = nextCandidate;
            }
            return id;
        }
        finally { gate.Release(); }
    }

    private static (int Id, int NextId) Allocate(IReadOnlyDictionary<int, MessageReference> entries, int firstCandidate)
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
        if (!File.Exists(path)) return;
        PersistedRegistry? persisted;
        try { persisted = JsonSerializer.Deserialize<PersistedRegistry>(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid message ID registry JSON", exception); }
        if (persisted is null || persisted.Version != 1 || persisted.NextId is < 1 or > MaxMessageId) throw new InvalidDataException("Invalid message ID registry format");
        foreach (var entry in persisted.Entries.TakeLast(maxEntries))
        {
            var reference = new MessageReference(entry.Scene, entry.PeerId, entry.Sequence);
            Validate(reference);
            if (entry.Id is < 1 or > MaxMessageId || !byId.TryAdd(entry.Id, reference) || !byReference.TryAdd(reference, entry.Id)) throw new InvalidDataException("Duplicate or invalid message ID registry entry");
            insertionOrder.Enqueue(entry.Id);
        }
        nextId = persisted.NextId;
    }

    private async Task PersistAsync(IReadOnlyDictionary<int, MessageReference> entries, IEnumerable<int> order, int candidate, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = JsonSerializer.Serialize(new PersistedRegistry(1, candidate, order.Select(id => new PersistedEntry(id, entries[id].Scene, entries[id].PeerId, entries[id].Sequence)).ToArray()));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream)) { await writer.WriteAsync(data.AsMemory(), cancellationToken); await writer.FlushAsync(cancellationToken); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(MessageReference reference)
    {
        if (reference.PeerId < 0 || reference.Sequence < 0) throw new ArgumentOutOfRangeException(nameof(reference), "Message reference values must be non-negative");
    }

    private sealed record PersistedRegistry(int Version, int NextId, PersistedEntry[] Entries);
    private sealed record PersistedEntry(int Id, MessageScene Scene, long PeerId, long Sequence);
}
