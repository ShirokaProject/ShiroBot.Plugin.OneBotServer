namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed class EventQueue<T>
{
    private readonly int maxEvents;
    private readonly int maxConsumers;
    private readonly TimeSpan consumerTtl;
    private readonly List<(long Sequence, T Event)> events = [];
    private readonly Dictionary<string, Consumer> consumers = [];
    private long sequence;
    private TaskCompletionSource<bool> signal = NewSignal();

    public EventQueue(int maxEvents = 100, int maxConsumers = 100, TimeSpan? consumerTtl = null)
    {
        if (maxEvents < 1 || maxConsumers < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        this.maxEvents = maxEvents;
        this.maxConsumers = maxConsumers;
        this.consumerTtl = consumerTtl ?? TimeSpan.FromMinutes(5);
        if (this.consumerTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(consumerTtl));
    }

    public void Push(T @event)
    {
        lock (events)
        {
            events.Add((++sequence, @event));
            if (events.Count > maxEvents) events.RemoveAt(0);
            signal.TrySetResult(true);
            signal = NewSignal();
        }
    }

    public async Task<IReadOnlyList<T>> FetchAsync(string cursor, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor is "__proto__" or "constructor" or "prototype") throw new ArgumentException("Invalid event consumer cursor", nameof(cursor));
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task wait;
        Consumer consumer;
        lock (events)
        {
            CleanupConsumers();
            if (!consumers.TryGetValue(cursor, out consumer!))
            {
                if (consumers.Count >= maxConsumers) consumers.Remove(consumers.MinBy(pair => pair.Value.LastAccess).Key);
                consumer = new Consumer(sequence, DateTimeOffset.UtcNow);
                consumers.Add(cursor, consumer);
            }
            var pending = events.Where(item => item.Sequence > consumer.Sequence).Select(item => item.Event).ToArray();
            if (pending.Length > 0 || timeout == TimeSpan.Zero) return Advance(consumer, pending);
            wait = signal.Task;
        }
        try
        {
            await wait.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return [];
        }
        lock (events)
        {
            return Advance(consumer, events.Where(item => item.Sequence > consumer.Sequence).Select(item => item.Event).ToArray());
        }
    }

    private IReadOnlyList<T> Advance(Consumer consumer, T[] result) { consumer.Sequence = sequence; consumer.LastAccess = DateTimeOffset.UtcNow; return result; }
    private void CleanupConsumers() { var cutoff = DateTimeOffset.UtcNow - consumerTtl; foreach (var key in consumers.Where(pair => pair.Value.LastAccess < cutoff).Select(pair => pair.Key).ToArray()) consumers.Remove(key); }
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Consumer(long sequence, DateTimeOffset lastAccess) { public long Sequence { get; set; } = sequence; public DateTimeOffset LastAccess { get; set; } = lastAccess; }
}
