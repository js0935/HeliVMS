using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// SQLite 儲存層（§4 index.db；§21.2 #3：WAL＋busy_timeout、單一寫入佇列）。
/// 時間一律以 ISO8601 UTC（TEXT）儲存（§3.2：全程 UTC 基準）。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private const int CurrentSchemaVersion = 7;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public SqliteStore(string databasePath)
    {
        DatabasePath = databasePath;
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

    public string DatabasePath { get; }

    /// <summary>建立/遷移資料表結構（§4；v1 舊庫直接重建）。</summary>
    public void Initialize()
    {
        var version = Query(
            "PRAGMA user_version;",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });

        if (version == 0)
        {
            var hasChannels = Query(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='channels';",
                static r =>
                {
                    r.Read();
                    return r.GetInt32(0) > 0;
                });

            if (hasChannels)
            {
                RebuildForV2();
            }
            else
            {
                CreateSchemaV2();
            }
        }

        if (version < 3)
        {
            CreateScheduleTableV3();
        }

        if (version < 4)
        {
            CreateDetectionTableV4();
        }

        if (version < 5)
        {
            CreateAppSettingsTableV5();
        }

        if (version < 6)
        {
            CreateNotificationLogTableV6();
        }

        if (version < 7)
        {
            CreateAlertRulesTableV7();
        }

        Execute("PRAGMA user_version = CURRENT_SCHEMA_VERSION;".Replace(
            "CURRENT_SCHEMA_VERSION", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)));
    }

    private void RebuildForV2()
    {
        Execute(
            """
            DROP TABLE IF EXISTS alarm_events;
            DROP TABLE IF EXISTS segment_keyframes;
            DROP TABLE IF EXISTS segments;
            DROP TABLE IF EXISTS channels;
            DROP TABLE IF EXISTS devices;
            """);
        ClearWal();
        CreateSchemaV2();
    }

    private void ClearWal()
    {
        try
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // 無 WAL 檔時可忽略
        }
    }

    private void CreateScheduleTableV3()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS recording_schedule (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id  INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                days_mask   INTEGER NOT NULL DEFAULT 127,
                start_min   INTEGER NOT NULL,
                end_min     INTEGER NOT NULL,
                enabled     INTEGER NOT NULL DEFAULT 1,
                created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_sched_channel ON recording_schedule(channel_id);
            """);
    }

    private void CreateDetectionTableV4()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS detections (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id   INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                class        TEXT    NOT NULL,
                confidence   REAL    NOT NULL,
                x            REAL    NOT NULL,
                y            REAL    NOT NULL,
                w            REAL    NOT NULL,
                h            REAL    NOT NULL,
                detected_at  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_detections_time ON detections(detected_at);
            CREATE INDEX IF NOT EXISTS idx_detections_channel ON detections(channel_id);
            """);
    }

    private void CreateAppSettingsTableV5()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """);
    }

    private void CreateNotificationLogTableV6()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS notification_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ts          TEXT    NOT NULL,
                channel_id  INTEGER NOT NULL,
                event_type  TEXT    NOT NULL,
                route       TEXT    NOT NULL,
                ok          INTEGER NOT NULL,
                attempts    INTEGER NOT NULL DEFAULT 1,
                detail      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_notification_log_ts ON notification_log(ts);
            """);
    }

    private void CreateAlertRulesTableV7()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS alert_rules (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                event_type  TEXT,
                channel_id  INTEGER,
                keyword     TEXT,
                channels    TEXT,
                enabled     INTEGER NOT NULL DEFAULT 1,
                created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_alert_rules_enabled ON alert_rules(enabled);
            """);
    }

    private void CreateSchemaV2()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS devices (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT    NOT NULL,
                ip                  TEXT    NOT NULL UNIQUE,
                port                INTEGER NOT NULL DEFAULT 80,
                username            TEXT,
                password_encrypted  TEXT,
                vendor              TEXT    NOT NULL DEFAULT 'generic',
                enabled             INTEGER NOT NULL DEFAULT 1,
                created_at          TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS channels (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id            INTEGER REFERENCES devices(id) ON DELETE CASCADE,
                name                 TEXT    NOT NULL,
                main_rtsp            TEXT    NOT NULL,
                sub_rtsp             TEXT,
                codec                TEXT    NOT NULL DEFAULT 'h264',
                audio_enabled        INTEGER NOT NULL DEFAULT 1,
                audio_encoder        TEXT    NOT NULL DEFAULT 'copy',
                recording_mode       INTEGER NOT NULL DEFAULT 0,
                motion_enabled       INTEGER NOT NULL DEFAULT 0,
                motion_sensitivity   REAL    NOT NULL DEFAULT 0.5,
                created_at           TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS segments (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                stream        TEXT    NOT NULL DEFAULT 'main',
                start_time    TEXT    NOT NULL,
                end_time      TEXT,
                file_path     TEXT    NOT NULL,
                size_bytes    INTEGER,
                duration_sec  REAL,
                status        TEXT    NOT NULL DEFAULT 'tmp',
                sha256        TEXT,
                UNIQUE(channel_id, stream, start_time)
            );
            CREATE INDEX IF NOT EXISTS idx_seg_channel_time ON segments(channel_id, start_time);

            CREATE TABLE IF NOT EXISTS segment_keyframes (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                segment_id    INTEGER NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
                time_sec      REAL    NOT NULL,
                moof_offset   INTEGER NOT NULL,
                UNIQUE(segment_id, time_sec)
            );
            CREATE INDEX IF NOT EXISTS idx_kf_segment ON segment_keyframes(segment_id);

            CREATE TABLE IF NOT EXISTS alarm_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                event_type    TEXT    NOT NULL,
                start_time    TEXT    NOT NULL,
                end_time      TEXT,
                snapshot_path TEXT,
                detail        TEXT,
                acknowledged  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_event_time ON alarm_events(start_time);
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

        SqliteConnection.ClearPool(_connection);   // 釋放 WAL/共享連線句柄，供測試與熱切換移除檔案
        _connection.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>UTC → ISO8601（可排序、人可讀）。</summary>
    public static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ISO8601 → UTC。</summary>
    public static DateTime FromIso(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}