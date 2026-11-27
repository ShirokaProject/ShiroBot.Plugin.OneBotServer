using System.Text.Json;
using SQLitePCL;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public enum MessageScene { Friend, Group, Temp }
public sealed record OneBotMessageReference(MessageScene Scene, long PeerId, long Sequence);

public sealed class MessageIdRegistry : IDisposable
{
    private const int MaxMessageId = int.MaxValue;
    private const string FindId = """
        SELECT id FROM messages WHERE account_id = ?1 AND scene = ?2 AND peer_id = ?3 AND sequence = ?4
        """;
    private const string InsertMessage = """
        INSERT INTO messages (id, account_id, scene, peer_id, sequence) VALUES (?1, ?2, ?3, ?4, ?5)
        """;
    private const string ReadNextId = "SELECT next_id FROM registry_state WHERE singleton = 1";
    // SQLite assigns consecutive positions. Eviction removes only a prefix, so trimming can seek by rowid.
    private const string Trim = """
        DELETE FROM messages WHERE position <= (SELECT max(position) FROM messages) - ?1
        """;

    private readonly MessageIdDatabase database;
    private readonly int maxEntries;
    private readonly long accountId;
    private readonly SemaphoreSlim gate = new(1, 1);

    private MessageIdRegistry(MessageIdDatabase database, int maxEntries, long accountId)
    {
        this.database = database;
        this.maxEntries = maxEntries;
        this.accountId = accountId;
    }

    public int Count
    {
        get
        {
            gate.Wait();
            try { return (int)database.Scalar("SELECT count(*) FROM messages WHERE account_id = ?1", [accountId]); }
            finally { gate.Release(); }
        }
    }

    public static async Task<MessageIdRegistry> OpenAsync(
        string path, int maxEntries = 100_000, CancellationToken cancellationToken = default,
        long accountId = 0, string? sharedLegacyPath = null)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        cancellationToken.ThrowIfCancellationRequested();
        var databasePath = GetDatabasePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var database = MessageIdDatabase.Open(databasePath);
        try
        {
            // Read JSON without holding a write lock. Another process may finish importing while we read it.
            PersistedRegistry? legacy = null;
            var legacyNextId = 1;
            const string readVersion = "PRAGMA user_version";
            if (database.Scalar(readVersion) == 0)
            {
                legacy = await ReadLegacyAsync(path, cancellationToken).ConfigureAwait(false);
                if (legacy is null)
                    legacyNextId = await ReadLegacyNextIdAsync(sharedLegacyPath, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            database.Execute("BEGIN IMMEDIATE");
            var version = database.Scalar(readVersion);
            if (version == 0)
            {
                database.ExecuteScript("""
                    CREATE TABLE registry_state (
                        singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                        next_id INTEGER NOT NULL CHECK (next_id BETWEEN 1 AND 2147483647));
                    CREATE TABLE messages (
                        position INTEGER PRIMARY KEY,
                        id INTEGER NOT NULL UNIQUE CHECK (id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL,
                        scene INTEGER NOT NULL, peer_id INTEGER NOT NULL, sequence INTEGER NOT NULL,
                        UNIQUE (account_id, scene, peer_id, sequence));
                    PRAGMA user_version = 1;
                    """);
                database.Execute("INSERT INTO registry_state VALUES (1, ?1)", [legacy?.NextId ?? legacyNextId]);
                if (legacy is not null)
                {
                    foreach (var entry in legacy.Entries.TakeLast(maxEntries))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var reference = new OneBotMessageReference(entry.Scene, entry.PeerId, entry.Sequence);
                        Validate(reference);
                        if (entry.Id < 1) throw new InvalidDataException("Invalid message ID registry entry");
                        database.Execute(InsertMessage, [entry.Id, accountId, (int)reference.Scene, reference.PeerId, reference.Sequence]);
                    }
                }
            }
            else if (version != 1)
            {
                throw new InvalidDataException("Unsupported SQLite message ID registry version");
            }

            database.Execute(Trim, [maxEntries]);
            cancellationToken.ThrowIfCancellationRequested();
            database.Execute("COMMIT");
            return new MessageIdRegistry(database, maxEntries, accountId);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    internal static string GetDatabasePath(string path)
    {
        path = Path.GetFullPath(path);
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, ".sqlite3")
            : path + ".sqlite3";
    }

    public bool TryResolve(int messageId, out OneBotMessageReference? reference)
    {
        gate.Wait();
        try
        {
            reference = database.Read("""
                SELECT scene, peer_id, sequence FROM messages WHERE id = ?1 AND account_id = ?2
                """, [messageId, accountId], static row => new OneBotMessageReference(
                (MessageScene)raw.sqlite3_column_int(row, 0), raw.sqlite3_column_int64(row, 1), raw.sqlite3_column_int64(row, 2)));
            return reference is not null;
        }
        finally { gate.Release(); }
    }

    public bool TryGet(OneBotMessageReference reference, out int messageId)
    {
        gate.Wait();
        try
        {
            messageId = GetId(reference);
            return messageId != 0;
        }
        finally { gate.Release(); }
    }

    public async Task<int> RegisterAsync(OneBotMessageReference reference, CancellationToken cancellationToken = default)
    {
        Validate(reference);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetId(reference);
            if (id != 0) return id;

            database.Execute("BEGIN IMMEDIATE");
            try
            {
                id = GetId(reference);
                if (id == 0)
                {
                    id = (int)database.Scalar(ReadNextId);
                    if (id == 0) throw new InvalidDataException("Missing SQLite message ID allocation cursor");
                    var firstCandidate = id;
                    while (database.Scalar("SELECT id FROM messages WHERE id = ?1", [id]) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        id = NextId(id);
                        if (id == firstCandidate) throw new InvalidOperationException("OneBot message ID space is exhausted");
                    }
                    database.Execute(InsertMessage, [id, accountId, (int)reference.Scene, reference.PeerId, reference.Sequence]);
                    database.Execute(Trim, [maxEntries]);
                    database.Execute("UPDATE registry_state SET next_id = ?1 WHERE singleton = 1", [NextId(id)]);
                }
                cancellationToken.ThrowIfCancellationRequested();
                database.Execute("COMMIT");
                return id;
            }
            catch
            {
                database.Rollback();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private int GetId(OneBotMessageReference reference) =>
        (int)database.Scalar(FindId, [accountId, (int)reference.Scene, reference.PeerId, reference.Sequence]);

    private static int NextId(int current) => current == MaxMessageId ? 1 : current + 1;

    public void Dispose()
    {
        gate.Wait();
        try { database.Dispose(); }
        finally { gate.Release(); }
    }

    private static async Task<int> ReadLegacyNextIdAsync(string? path, CancellationToken cancellationToken)
    {
        var persisted = await ReadLegacyAsync(path, cancellationToken).ConfigureAwait(false);
        if (persisted is null) return 1;

        var historicalMax = 0;
        foreach (var entry in persisted.Entries)
        {
            if (entry.Id < 1) throw new InvalidDataException("Invalid legacy message ID registry entry");
            historicalMax = Math.Max(historicalMax, entry.Id);
        }
        return historicalMax < MaxMessageId ? Math.Max(persisted.NextId, historicalMax + 1) : persisted.NextId;
    }

    private static async Task<PersistedRegistry?> ReadLegacyAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        PersistedRegistry? persisted;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            persisted = await JsonSerializer.DeserializeAsync<PersistedRegistry>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid legacy message ID registry JSON", exception); }

        if (persisted is not { Version: 1, NextId: > 0, Entries: not null })
            throw new InvalidDataException("Invalid legacy message ID registry format");
        return persisted;
    }

    private static void Validate(OneBotMessageReference reference)
    {
        if (reference.PeerId < 0 || reference.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(reference), "Message reference values must be non-negative");
    }

    private sealed record PersistedRegistry(int Version, int NextId, PersistedEntry[] Entries);
    private sealed record PersistedEntry(int Id, MessageScene Scene, long PeerId, long Sequence);
}
