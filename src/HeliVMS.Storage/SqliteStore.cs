using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// SQLite 儲存層（§4 index.db；§21.2 #3：WAL＋busy_timeout、單一寫入佇列）。
/// 時間一律以 ISO8601 UTC（TEXT）儲存（§3.2：全程 UTC 基準）。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private const int CurrentSchemaVersion = 31;
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
        Execute("PRAGMA foreign_keys=ON;");
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

        if (version < 8)
        {
            CreateEventDispositionsTableV8();
        }

        if (version < 9)
        {
            CreateIoTablesV9();
        }

        if (version < 10)
        {
            CreateMapTablesV10();
        }

        if (version < 11)
        {
            CreateUsersTableV11();
        }

        if (version < 12)
        {
            CreateExportJobsTableV12();
        }

        if (version < 13)
        {
            CreateBackupLogTableV13();
        }

        if (version < 14)
        {
            CreateAlarmTriageTableV14();
        }

        if (version < 15)
        {
            AddMapScaleV15();
        }

        if (version < 16)
        {
            CreateAuthProvidersTableV16();
        }

        if (version < 17)
        {
            CreateShareLinksTableV17();
        }

        if (version < 18)
        {
            CreateAnalyticsZonesTableV18();
        }

        if (version < 19)
        {
            CreateEvidenceManifestsTableV19();
        }

        if (version < 20)
        {
            AddSmartAlertColumnsV20();
        }

        if (version < 21)
        {
            CreateOffsiteJobsTableV21();
        }

        if (version < 22)
        {
            ExpandAnalyticsModulesV22();
        }

        if (version < 23)
        {
            ExpandAnalyticsModulesV23();
        }

        if (version < 24)
        {
            CreateAiRulesTableV24();
        }

        if (version < 25)
        {
            CreateLegalHoldsTableV25();
        }

        if (version < 26)
        {
            CreatePatrolsTablesV26();
        }

        if (version < 27)
        {
            CreateSensorIoTablesV27();
        }

        if (version < 28)
        {
            CreateFailoverTablesV28();
        }

        if (version < 29)
        {
            CreateFullTextSearchTablesV29();
        }

        if (version < 30)
        {
            CreateDoorEventTablesV30();
        }

        if (version < 31)
        {
            CreatePosEventTablesV31();
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

    private void CreateEventDispositionsTableV8()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS event_dispositions (
                event_id    INTEGER PRIMARY KEY REFERENCES alarm_events(id) ON DELETE CASCADE,
                status      TEXT    NOT NULL DEFAULT 'pending',
                assigned_to TEXT,
                note        TEXT,
                updated_at  TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS event_disposition_trail (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id    INTEGER NOT NULL REFERENCES alarm_events(id) ON DELETE CASCADE,
                status      TEXT    NOT NULL,
                assigned_to TEXT,
                note        TEXT,
                changed_at  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_disp_trail_event ON event_disposition_trail(event_id);
            """);
    }

    private void CreateIoTablesV9()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS io_devices (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                protocol    TEXT    NOT NULL DEFAULT 'modbus_tcp',
                host        TEXT    NOT NULL,
                port        INTEGER NOT NULL DEFAULT 502,
                unit_id     INTEGER NOT NULL DEFAULT 1,
                enabled     INTEGER NOT NULL DEFAULT 1,
                poll_ms     INTEGER NOT NULL DEFAULT 500,
                created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS io_channels (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id      INTEGER NOT NULL REFERENCES io_devices(id) ON DELETE CASCADE,
                direction      TEXT    NOT NULL CHECK (direction IN ('DI', 'DO')),
                io_index       INTEGER NOT NULL,
                name           TEXT    NOT NULL,
                enabled        INTEGER NOT NULL DEFAULT 1,
                debounce_ms    INTEGER NOT NULL DEFAULT 200,
                polarity       INTEGER NOT NULL DEFAULT 0,
                camera_id      INTEGER,
                alarm_priority TEXT    NOT NULL DEFAULT 'normal',
                UNIQUE (device_id, direction, io_index)
            );
            CREATE INDEX IF NOT EXISTS idx_io_ch_device ON io_channels(device_id);
            """);
    }

    private void CreateMapTablesV10()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS maps (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                type        TEXT    NOT NULL DEFAULT 'plan',
                image_path  TEXT    NOT NULL,
                width       INTEGER NOT NULL,
                height      INTEGER NOT NULL,
                enabled     INTEGER NOT NULL DEFAULT 1,
                sort_order  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS map_devices (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                map_id      INTEGER NOT NULL REFERENCES maps(id) ON DELETE CASCADE,
                device_type TEXT    NOT NULL CHECK (device_type IN ('camera', 'io')),
                channel_id  INTEGER NOT NULL,
                x           REAL    NOT NULL DEFAULT 0.5,
                y           REAL    NOT NULL DEFAULT 0.5,
                angle       REAL    NOT NULL DEFAULT 0,
                fov_deg     REAL    NOT NULL DEFAULT 90,
                fov_depth   REAL    NOT NULL DEFAULT 3,
                enabled     INTEGER NOT NULL DEFAULT 1,
                UNIQUE (map_id, device_type, channel_id)
            );
            CREATE INDEX IF NOT EXISTS idx_map_dev_map ON map_devices(map_id);
            """);
    }

    private void CreateUsersTableV11()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                username      TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT    NOT NULL,
                role          TEXT    NOT NULL DEFAULT 'viewer' CHECK (role IN ('admin', 'viewer')),
                display_name  TEXT,
                enabled       INTEGER NOT NULL DEFAULT 1,
                failed_logins INTEGER NOT NULL DEFAULT 0,
                locked_until  TEXT,
                last_login    TEXT,
                created_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_users_username ON users(username);
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

    private void CreateExportJobsTableV12()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS export_jobs (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id     INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                stream         TEXT    NOT NULL DEFAULT 'main',
                start_time     TEXT    NOT NULL,
                end_time       TEXT    NOT NULL,
                status         TEXT    NOT NULL DEFAULT 'queued',
                output_path    TEXT,
                file_size_bytes INTEGER,
                sha256         TEXT,
                error          TEXT,
                created_at     TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                started_at     TEXT,
                finished_at    TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_export_jobs_status ON export_jobs(status);
            """);
    }

    private void CreateBackupLogTableV13()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS backup_log (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                run_at         TEXT    NOT NULL,
                source_root    TEXT    NOT NULL,
                target_root    TEXT    NOT NULL,
                checkpoint_utc TEXT,
                copied_count   INTEGER NOT NULL DEFAULT 0,
                copied_bytes   INTEGER NOT NULL DEFAULT 0,
                failed_count   INTEGER NOT NULL DEFAULT 0,
                detail         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_backup_log_target ON backup_log(target_root, run_at);
            """);
    }

    /// <summary>M47 事件分診（§14.7 #3）：優先序／處理時限／負責人（一事件一列）。</summary>
    private void CreateAlarmTriageTableV14()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS alarm_triage (
                event_id   INTEGER PRIMARY KEY REFERENCES alarm_events(id) ON DELETE CASCADE,
                priority   TEXT    NOT NULL DEFAULT 'normal',
                due_utc    TEXT,
                owner      TEXT,
                updated_at TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_triage_due ON alarm_triage(due_utc);
            """);
    }

    /// <summary>M49 智慧地圖（§14.7 #11）：地圖比例尺（每像素公尺；0＝未標定）。冪等。</summary>
    private void AddMapScaleV15()
    {
        var hasColumn = Query(
            "SELECT COUNT(*) FROM pragma_table_info('maps') WHERE name='scale_m_per_px';",
            static r =>
            {
                r.Read();
                return r.GetInt64(0) > 0;
            });

        if (!hasColumn)
        {
            Execute("ALTER TABLE maps ADD COLUMN scale_m_per_px REAL NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>M50 企業身份整合（§14.7 #1）：OIDC／LDAP 身分提供者。</summary>
    private void CreateAuthProvidersTableV16()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS auth_providers (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                kind        TEXT    NOT NULL CHECK (kind IN ('oidc', 'ldap')),
                enabled     INTEGER NOT NULL DEFAULT 1,
                config_json TEXT    NOT NULL,
                created_at  TEXT    NOT NULL
            );
            """);
    }

    /// <summary>M51 外部安全共享（§14.7 #4）：具到期／次數／密碼／撤銷的分享連結。</summary>
    private void CreateShareLinksTableV17()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS share_links (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                token         TEXT    NOT NULL UNIQUE,
                kind          TEXT    NOT NULL CHECK (kind IN ('segment', 'snapshot', 'evidence')),
                resource_path TEXT    NOT NULL,
                label         TEXT,
                password_hash TEXT,
                created_at    TEXT    NOT NULL,
                created_by    TEXT,
                expires_at    TEXT,
                max_uses      INTEGER NOT NULL DEFAULT 0,
                use_count     INTEGER NOT NULL DEFAULT 0,
                revoked       INTEGER NOT NULL DEFAULT 0,
                last_used_at  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_share_expires ON share_links(expires_at);
            """);
    }

    /// <summary>M52 模組化分析情境（§14.7 #6）：分析區（多邊形／方向／門檻）。</summary>
    private void CreateAnalyticsZonesTableV18()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS analytics_zones (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT    NOT NULL,
                channel_id    INTEGER NOT NULL,
                module        TEXT    NOT NULL CHECK (module IN ('line_cross', 'intrusion', 'crowd', 'loitering', 'stationary')),
                enabled       INTEGER NOT NULL DEFAULT 1,
                polygon       TEXT    NOT NULL,
                direction     TEXT    NOT NULL DEFAULT 'both' CHECK (direction IN ('both', 'a_to_b', 'b_to_a')),
                min_count     INTEGER NOT NULL DEFAULT 0,
                dwell_seconds INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_analytics_channel ON analytics_zones(channel_id);
            """);
    }

    /// <summary>M58 智慧分析模組二（§5.6）：analytics_zones 模組 CHECK 擴充 traffic／heatmap（重建資料表，保留資料）。</summary>
    private void ExpandAnalyticsModulesV22()
    {
        Execute(
            """
            CREATE TABLE analytics_zones_new (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT    NOT NULL,
                channel_id    INTEGER NOT NULL,
                module        TEXT    NOT NULL CHECK (module IN ('line_cross', 'intrusion', 'crowd', 'loitering', 'stationary', 'traffic', 'heatmap')),
                enabled       INTEGER NOT NULL DEFAULT 1,
                polygon       TEXT    NOT NULL,
                direction     TEXT    NOT NULL DEFAULT 'both' CHECK (direction IN ('both', 'a_to_b', 'b_to_a')),
                min_count     INTEGER NOT NULL DEFAULT 0,
                dwell_seconds INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT    NOT NULL
            );
            INSERT INTO analytics_zones_new (id, name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at)
                SELECT id, name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at FROM analytics_zones;
            DROP TABLE analytics_zones;
            ALTER TABLE analytics_zones_new RENAME TO analytics_zones;
            CREATE INDEX IF NOT EXISTS idx_analytics_channel ON analytics_zones(channel_id);
            """);
    }

    /// <summary>M59 尾隨/逆行（§5.6）：analytics_zones 模組 CHECK 再擴充 tailgating（重建資料表，保留資料）。</summary>
    private void ExpandAnalyticsModulesV23()
    {
        Execute(
            """
            CREATE TABLE analytics_zones_new (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT    NOT NULL,
                channel_id    INTEGER NOT NULL,
                module        TEXT    NOT NULL CHECK (module IN ('line_cross', 'intrusion', 'crowd', 'loitering', 'stationary', 'traffic', 'heatmap', 'tailgating')),
                enabled       INTEGER NOT NULL DEFAULT 1,
                polygon       TEXT    NOT NULL,
                direction     TEXT    NOT NULL DEFAULT 'both' CHECK (direction IN ('both', 'a_to_b', 'b_to_a')),
                min_count     INTEGER NOT NULL DEFAULT 0,
                dwell_seconds INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT    NOT NULL
            );
            INSERT INTO analytics_zones_new (id, name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at)
                SELECT id, name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at FROM analytics_zones;
            DROP TABLE analytics_zones;
            ALTER TABLE analytics_zones_new RENAME TO analytics_zones;
            CREATE INDEX IF NOT EXISTS idx_analytics_channel ON analytics_zones(channel_id);
            """);
    }

    /// <summary>M62 複合事件規則（§5.10）：ai_rules 設定層資料表。</summary>
    private void CreateAiRulesTableV24()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS ai_rules (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                name           TEXT    NOT NULL,
                expression_json TEXT   NOT NULL,
                actions_json   TEXT    NOT NULL,
                enabled        INTEGER NOT NULL DEFAULT 1,
                created_at     TEXT    NOT NULL
            );
            """);
    }

    /// <summary>M66 保存鎖定（§14.1 #13）：指定通道時段豁免配額汰除；沖銷（軟刪）留稽核欄位。</summary>
    private void CreateLegalHoldsTableV25()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS legal_holds (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id     INTEGER NOT NULL,
                from_utc       TEXT    NOT NULL,
                to_utc         TEXT    NOT NULL,
                reason         TEXT    NOT NULL,
                created_by     TEXT    NOT NULL,
                created_at     TEXT    NOT NULL,
                revoked_at     TEXT,
                revoked_by     TEXT,
                revoked_reason TEXT
            );
            """);
    }

    /// <summary>M72 巡航排程（§47 計畫收尾）：每通道一套巡航 Plan + 依序預設點步驟；UI 管理、引擎取用。</summary>
    private void CreatePatrolsTablesV26()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS patrols (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT    NOT NULL,
                channel_id   INTEGER NOT NULL,
                enabled      INTEGER NOT NULL DEFAULT 0,
                window_start TEXT    NOT NULL DEFAULT '00:00',
                window_end   TEXT    NOT NULL DEFAULT '23:59',
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS patrol_steps (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                patrol_id    INTEGER NOT NULL,
                seq          INTEGER NOT NULL,
                preset_name  TEXT    NOT NULL,
                dwell_seconds INTEGER NOT NULL DEFAULT 10
            );
            CREATE INDEX IF NOT EXISTS idx_patrol_steps_patrol_seq ON patrol_steps (patrol_id, seq);
            """);
    }

    /// <summary>M74 感測器 IO（§14.1 #16）：乾接點 DI/DO 埠設定＋動作鏈規則；狀態由 IoRuleEngine 維護。</summary>
    private void CreateSensorIoTablesV27()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS io_ports (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id   INTEGER NOT NULL,
                kind         TEXT    NOT NULL,
                number       INTEGER NOT NULL,
                name         TEXT    NOT NULL,
                polarity     TEXT    NOT NULL DEFAULT 'normally_open',
                debounce_ms  INTEGER NOT NULL DEFAULT 0,
                enabled      INTEGER NOT NULL DEFAULT 1,
                UNIQUE (channel_id, kind, number)
            );
            CREATE TABLE IF NOT EXISTS io_rules (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                input_port_id   INTEGER NOT NULL,
                action_kind     TEXT    NOT NULL DEFAULT 'alarm',
                output_port_id  INTEGER,
                event_type      TEXT,
                retrigger_sec   INTEGER NOT NULL DEFAULT 30,
                enabled         INTEGER NOT NULL DEFAULT 1
            );
            """);
    }

    /// <summary>M54 智慧警報（§14.7 #8）：alert_rules 增列聚合窗／類別類別篩選（冪等）。</summary>
    private void AddSmartAlertColumnsV20()
    {
        EnsureColumn("alert_rules", "match_event_types", "TEXT");
        EnsureColumn("alert_rules", "frame_minutes", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("alert_rules", "min_events_in_window", "INTEGER NOT NULL DEFAULT 1");
    }

    private void CreateOffsiteJobsTableV21()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS offsite_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                interval_minutes INTEGER NOT NULL DEFAULT 1440,
                enabled INTEGER NOT NULL DEFAULT 0,
                last_run_utc TEXT,
                last_result TEXT,
                last_error TEXT,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """);
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        var has = Query(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';",
            static r =>
            {
                r.Read();
                return r.GetInt64(0) > 0;
            });

        if (!has)
        {
            Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }
    }

    /// <summary>M53 數位證據完整性（§14.7 #5）：證據集的 manifest 記錄（含數位簽章）。</summary>
    private void CreateEvidenceManifestsTableV19()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS evidence_manifests (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                directory_path TEXT    NOT NULL UNIQUE,
                manifest_json   TEXT    NOT NULL,
                status          TEXT    NOT NULL DEFAULT 'created',
                created_at      TEXT    NOT NULL,
                last_verified_at TEXT
            );
            """);
    }

    /// <summary>M87 容錯（§14.7 #9）：failover 單列租約表（id 固定 1，insert-or-replace 覆寫）。</summary>
    private void CreateFailoverTablesV28()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS failover_state (
                id                INTEGER PRIMARY KEY,
                server_id         TEXT NOT NULL,
                lease_expires_utc TEXT NOT NULL
            );
            """);
    }

    /// <summary>M90 法證語意搜尋 L0（§14.7 #7）：alarm_events 全文 FTS5 外部內容表＋同步 trigger
    /// （event_type＋detail 進索引；AI/AD/AU 三向同步；既有列由 EventSearchRepository 建構時回填）。</summary>
    private void CreateFullTextSearchTablesV29()
    {
        Execute(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS alarm_events_fts USING fts5(
                id UNINDEXED,
                event_type,
                detail,
                content='alarm_events',
                content_rowid='id'
            );

            CREATE TRIGGER IF NOT EXISTS alarm_events_fts_ai AFTER INSERT ON alarm_events BEGIN
                INSERT INTO alarm_events_fts(rowid, event_type, detail)
                VALUES (new.id, new.event_type, new.detail);
            END;

            CREATE TRIGGER IF NOT EXISTS alarm_events_fts_ad AFTER DELETE ON alarm_events BEGIN
                INSERT INTO alarm_events_fts(alarm_events_fts, rowid, event_type, detail)
                VALUES ('delete', old.id, old.event_type, old.detail);
            END;

            CREATE TRIGGER IF NOT EXISTS alarm_events_fts_au AFTER UPDATE OF event_type, detail ON alarm_events BEGIN
                INSERT INTO alarm_events_fts(alarm_events_fts, rowid, event_type, detail)
                VALUES ('delete', old.id, old.event_type, old.detail);
                INSERT INTO alarm_events_fts(rowid, event_type, detail)
                VALUES (new.id, new.event_type, new.detail);
            END;
            """);
    }

    /// <summary>M92 門禁（§14.7 #8）：卡片進出事件表＋時序索引（device・card・door）。</summary>
    private void CreateDoorEventTablesV30()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS door_events (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id        INTEGER NOT NULL,
                door_id          INTEGER NOT NULL,
                card_id          TEXT NOT NULL,
                direction        TEXT NOT NULL CHECK (direction IN ('In', 'Out')),
                granted          INTEGER NOT NULL,
                reason           TEXT NOT NULL,
                occurred_at_utc  TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_door_events_device_time  ON door_events(device_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS idx_door_events_card_time    ON door_events(card_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS idx_door_events_door_time    ON door_events(door_id, occurred_at_utc);
            """);
    }

    /// <summary>M93 POS 交易（§14.7 #8）：交易存錄＋設備時序索引＋交易號索引。</summary>
    private void CreatePosEventTablesV31()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS pos_events (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id        INTEGER NOT NULL,
                register_id      TEXT NOT NULL DEFAULT '',
                transaction_no   TEXT NOT NULL,
                amount_cents     INTEGER NOT NULL,
                occurred_at_utc  TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_pos_events_device_time ON pos_events(device_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS idx_pos_events_transaction ON pos_events(transaction_no);
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