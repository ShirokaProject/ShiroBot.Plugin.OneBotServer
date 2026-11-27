using SQLitePCL;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

internal sealed class MessageIdDatabase(sqlite3 handle) : IDisposable
{
    private readonly Dictionary<string, sqlite3_stmt> statements = new(StringComparer.Ordinal);
    private bool disposed;

    static MessageIdDatabase() => Batteries_V2.Init();

    public static MessageIdDatabase Open(string path)
    {
        var result = raw.sqlite3_open_v2(path, out var handle,
            raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE | raw.SQLITE_OPEN_FULLMUTEX, null);
        var database = new MessageIdDatabase(handle);
        try
        {
            database.Check(result);
            database.Check(raw.sqlite3_busy_timeout(handle, 5000));
            database.ExecuteScript("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA cache_size = -512;");
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public long Scalar(string sql, ReadOnlySpan<long> values = default) =>
        Read(sql, values, static row => raw.sqlite3_column_int64(row, 0));

    public T? Read<T>(string sql, ReadOnlySpan<long> values, Func<sqlite3_stmt, T> read)
    {
        var statement = Bind(sql, values);
        try
        {
            var result = raw.sqlite3_step(statement);
            if (result == raw.SQLITE_ROW) return read(statement);
            if (result != raw.SQLITE_DONE) throw Error(result);
            return default;
        }
        finally { raw.sqlite3_reset(statement); }
    }

    public void Execute(string sql, ReadOnlySpan<long> values = default)
    {
        var statement = Bind(sql, values);
        try
        {
            var result = raw.sqlite3_step(statement);
            if (result != raw.SQLITE_DONE) throw Error(result);
        }
        finally { raw.sqlite3_reset(statement); }
    }

    public void ExecuteScript(string sql) => Check(raw.sqlite3_exec(handle, sql));

    public void Rollback()
    {
        if (raw.sqlite3_get_autocommit(handle) == 0) Execute("ROLLBACK");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var statement in statements.Values) statement.Dispose();
        statements.Clear();
        handle.Dispose();
    }

    private sqlite3_stmt Bind(string sql, ReadOnlySpan<long> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!statements.TryGetValue(sql, out var statement))
        {
            Check(raw.sqlite3_prepare_v2(handle, sql, out statement));
            statements.Add(sql, statement);
        }
        for (var i = 0; i < values.Length; i++) Check(raw.sqlite3_bind_int64(statement, i + 1, values[i]));
        return statement;
    }

    private void Check(int result)
    {
        if (result != raw.SQLITE_OK) throw Error(result);
    }

    private IOException Error(int result) =>
        new($"SQLite message ID registry error ({result}): {raw.sqlite3_errmsg(handle).utf8_to_string()}");
}
