using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// SQLite 儲存層（§4；§21.2 #3：WAL＋busy_timeout）。
/// 單一連線、單一寫入鎖，避免多執行緒寫鎖。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public SqliteStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("PRAGMA synchronous=NORMAL;");
    }

    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>建立資料表（冪等）。</summary>
    public void Initialize()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS channels (
                id            INTEGER PRIMARY KEY,
                name          TEXT    NOT NULL,
                main_url      TEXT    NOT NULL,
                sub_url       TEXT,
                recording_mode INTEGER NOT NULL DEFAULT 0,
                enabled       INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS segments (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id  INTEGER NOT NULL,
                start_ms    INTEGER NOT NULL,
                end_ms      INTEGER,
                file_path   TEXT    NOT NULL,
                size_bytes  INTEGER NOT NULL DEFAULT 0,
                format      TEXT    NOT NULL DEFAULT 'mpegts',
                status      INTEGER NOT NULL DEFAULT 0,
                created_ms  INTEGER NOT NULL,
                FOREIGN KEY (channel_id) REFERENCES channels(id)
            );

            CREATE INDEX IF NOT EXISTS ix_segments_channel_start
                ON segments(channel_id, start_ms);
            """);
    }

    /// <summary>執行無回傳 SQL（呼叫端負責參數）。</summary>
    public void Execute(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>執行查詢，回傳 reader 結果（呼叫端封閉在同步函式內）。</summary>
    public T Query<T>(string sql, Func<SqliteDataReader, T> read, Action<SqliteCommand>? bind = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            using var reader = cmd.ExecuteReader();
            return read(reader);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}