namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed record PrivateFileReference(long UserId, string FileHash, bool IsSelfSend);

public sealed class PrivateFileRegistry(int maxEntries = 10_000)
{
    private readonly BoundedRegistry<string, PrivateFileReference> entries = new(maxEntries);
    public void Remember(string fileId, PrivateFileReference reference) => entries.Set(fileId, reference);
    public bool TryResolve(string fileId, out PrivateFileReference? reference) => entries.TryGet(fileId, out reference);
}

public sealed class ReactionRegistry(int maxEntries = 10_000)
{
    private readonly BoundedRegistry<(long GroupId, long Sequence, string EmojiId, long UserId), byte> entries = new(maxEntries);
    public int Update(long groupId, long sequence, string emojiId, long userId, bool isAdd)
    {
        var key = (groupId, sequence, emojiId, userId);
        if (isAdd) entries.Set(key, 0); else entries.Remove(key);
        return entries.Keys.Count(value => value.GroupId == groupId && value.Sequence == sequence && value.EmojiId == emojiId);
    }

    public IReadOnlyList<long> GetUsers(long groupId, long sequence, string emojiId, int count) =>
        entries.Keys
            .Where(value => value.GroupId == groupId && value.Sequence == sequence && value.EmojiId == emojiId)
            .Select(value => value.UserId)
            .Take(count)
            .ToArray();
}

internal sealed class BoundedRegistry<TKey, TValue>(int maxEntries) where TKey : notnull
{
    private readonly int maxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));
    private readonly object gate = new();
    private readonly Dictionary<TKey, TValue> values = [];
    public IReadOnlyCollection<TKey> Keys { get { lock (gate) return values.Keys.ToArray(); } }
    public void Set(TKey key, TValue value) { lock (gate) { values.Remove(key); values[key] = value; while (values.Count > maxEntries) values.Remove(values.Keys.First()); } }
    public bool TryGet(TKey key, out TValue? value) { lock (gate) return values.TryGetValue(key, out value); }
    public bool Remove(TKey key) { lock (gate) return values.Remove(key); }
}
