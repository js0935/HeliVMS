using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// SQLite 儲存層（§4 index.db；§21.2 #3：WAL＋busy_timeout、單一寫入佇列）。
/// 時間一律以 ISO8601 UTC（TEXT）儲存（§3.2：全程 UTC 基準）。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private const int CurrentSchemaVersion = 56;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    // 環境交易：InTransaction 期間設定，讓巢狀的 Execute／Query 自動加入同一交易
    // （Microsoft.Data.Sqlite 要求交易中的命令帶 Transaction，否則丟 InvalidOperationException）。
    private SqliteTransaction? _tx;

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

        if (version < 32)
        {
            CreateEdgeBackfillTablesV32();
        }

        if (version < 33)
        {
            CreateEdgeSmartEventTablesV33();
        }

        if (version < 34)
        {
            CreateUnifiedSearchTablesV34();
        }

        if (version < 35)
        {
            CreateFailoverEventTablesV35();
        }

        if (version < 36)
        {
            CreateLoginSessionTablesV36();
        }

        if (version < 37)
        {
            CreateRedactionTablesV37();
        }

        if (version < 38)
        {
            CreateAlarmWorkflowTablesV38();
        }

        if (version < 39)
        {
            CreateSmartwallTablesV39();
        }

        if (version < 40)
        {
            CreateAuditLogTablesV40();
        }

        if (version < 41)
        {
            CreateEmbeddingTablesV41();
        }

        if (version < 42)
        {
            CreateAudioTablesV42();
        }

        if (version < 43)
        {
            CreateLicenseTableV43();
        }

        if (version < 44)
        {
            AddDeviceSdUrlV44();
        }

        if (version < 45)
        {
            AddChannelEnabledV45();
        }

        if (version < 46)
        {
            AddDeviceStreamUrlsV46();
        }

        if (version < 47)
        {
            AddChannelDisplayCellV47();
        }

        if (version < 48)
        {
            CreateCentralNodesTableV48();
        }

        if (version < 49)
        {
            CreateChannelGroupTablesV49();
        }

        if (version < 50)
        {
            CreateDeviceGroupTablesV50();
        }

        if (version < 51)
        {
            CreateDeviceOperationTableV51();
        }

        if (version < 52)
        {
            CreateSettingsVersionTableV52();
        }

        if (version < 53)
        {
            AddSegmentRetentionIndexV53();
        }

        if (version < 54)
        {
            AddSegmentCheckedAtV54();
        }

        if (version < 55)
        {
            CreateRecordingLeaseTableV55();
        }

        if (version < 56)
        {
            CreateBookmarkTableV56();
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
                created_at          TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                sd_url              TEXT,
                main_rtsp           TEXT,
                sub_rtsp            TEXT
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
                enabled              INTEGER NOT NULL DEFAULT 1,
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

    /// <summary>M94 邊緣補抓（§14.7 #10）：devices 增列 sd_url（設備端 SD 側錄／回放串流位址）。</summary>
    private void AddDeviceSdUrlV44()
    {
        EnsureColumn("devices", "sd_url", "TEXT");
    }

    /// <summary>
    /// channels 增列 enabled。
    ///
    /// 這個欄位之前只存在於 C# 的 <c>ChannelInfo.Enabled</c>，資料庫裡沒有對應欄位，
    /// 所以「停用某台攝影機」在 repository 的 SELECT 與 UPDATE 兩端都沒有接上：
    /// 讀回來永遠是預設值 true，寫回去也不會生效。操作於是無法排除故障或暫停的攝影機，
    /// 而錄影排程照樣往它開錄——現場看到的還是「錄影正常，沒有錯誤」，
    /// 只是多一台壞掉的攝影機在整天寫著壞掉的片段。
    ///
    /// 預設 1（啟用），既有安裝的行為不變。
    /// </summary>
    private void AddChannelEnabledV45()
    {
        EnsureColumn("channels", "enabled", "INTEGER NOT NULL DEFAULT 1");
    }

    /// <summary>
    /// devices 增列 main_rtsp／sub_rtsp。
    ///
    /// 攝影機（device）與頻道（channel）分離管理後，ONVIF 加入攝影機時就取得其主／子碼流位址，
    /// 需先存回設備。之後「頻道管理」才能離線把某台攝影機的碼流分配成頻道，
    /// 不必每次重新連線探測。既有安裝的設備兩欄為 NULL，行為不變。
    /// </summary>
    private void AddDeviceStreamUrlsV46()
    {
        EnsureColumn("devices", "main_rtsp", "TEXT");
        EnsureColumn("devices", "sub_rtsp", "TEXT");
    }

    /// <summary>
    /// channels 增列 display_cell：操作員可指定某台攝影機固定在監看區第幾格。
    ///
    /// 預設 NULL（未指定）＝沿用建立順序自動排，既有安裝行為不變。
    /// 只影響監看格位，不影響錄影、查詢或事件。
    /// </summary>
    private void AddChannelDisplayCellV47()
    {
        EnsureColumn("channels", "display_cell", "INTEGER");
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

    /// <summary>M94 邊緣補抓回灌（§14.7 #10）：job 狀態機＋狀態索引。</summary>
    private void CreateEdgeBackfillTablesV32()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS edge_backfill_jobs (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id        INTEGER NOT NULL,
                channel_id       INTEGER NOT NULL,
                start_utc        TEXT NOT NULL,
                end_utc          TEXT NOT NULL,
                status           TEXT NOT NULL DEFAULT 'Pending',
                attempts         INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT,
                last_error       TEXT,
                completed_utc    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_edge_backfill_device ON edge_backfill_jobs(device_id, start_utc);
            CREATE INDEX IF NOT EXISTS idx_edge_backfill_status  ON edge_backfill_jobs(status, next_attempt_utc);
            """);
    }

    /// <summary>M95 消費邊緣 AI metadata（§14.7 #13）L1：穩定類別/軌跡/方向事件的持久化＋時序索引。</summary>
    private void CreateEdgeSmartEventTablesV33()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS edge_smart_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id       INTEGER NOT NULL,
                class_name      TEXT NOT NULL,
                track_id        TEXT NOT NULL DEFAULT '',
                direction       TEXT NOT NULL,
                x1              INTEGER NOT NULL,
                y1              INTEGER NOT NULL,
                x2              INTEGER NOT NULL,
                y2              INTEGER NOT NULL,
                occurred_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_edge_smart_device_time  ON edge_smart_events(device_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS idx_edge_smart_class_time   ON edge_smart_events(class_name, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS idx_edge_smart_direction    ON edge_smart_events(direction, occurred_at_utc);
            """);
    }

    /// <summary>M97 法證語意搜尋多源化（§14.7 #7）L1：門禁/POS/Edge AI 各建 FTS5 外部內容表＋trigger 同步。</summary>
    private void CreateUnifiedSearchTablesV34()
    {
        Execute(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS door_events_fts USING fts5(
                id UNINDEXED,
                card_id,
                direction,
                reason,
                content='door_events',
                content_rowid='id'
            );

            CREATE TRIGGER IF NOT EXISTS door_events_fts_ai AFTER INSERT ON door_events BEGIN
                INSERT INTO door_events_fts(rowid, card_id, direction, reason)
                VALUES (new.id, new.card_id, new.direction, new.reason);
            END;

            CREATE TRIGGER IF NOT EXISTS door_events_fts_ad AFTER DELETE ON door_events BEGIN
                INSERT INTO door_events_fts(door_events_fts, rowid, card_id, direction, reason)
                VALUES ('delete', old.id, old.card_id, old.direction, old.reason);
            END;

            CREATE TRIGGER IF NOT EXISTS door_events_fts_au AFTER UPDATE OF card_id, direction, reason ON door_events BEGIN
                INSERT INTO door_events_fts(door_events_fts, rowid, card_id, direction, reason)
                VALUES ('delete', old.id, old.card_id, old.direction, old.reason);
                INSERT INTO door_events_fts(rowid, card_id, direction, reason)
                VALUES (new.id, new.card_id, new.direction, new.reason);
            END;

            CREATE VIRTUAL TABLE IF NOT EXISTS pos_events_fts USING fts5(
                id UNINDEXED,
                register_id,
                transaction_no,
                content='pos_events',
                content_rowid='id'
            );

            CREATE TRIGGER IF NOT EXISTS pos_events_fts_ai AFTER INSERT ON pos_events BEGIN
                INSERT INTO pos_events_fts(rowid, register_id, transaction_no)
                VALUES (new.id, new.register_id, new.transaction_no);
            END;

            CREATE TRIGGER IF NOT EXISTS pos_events_fts_ad AFTER DELETE ON pos_events BEGIN
                INSERT INTO pos_events_fts(pos_events_fts, rowid, register_id, transaction_no)
                VALUES ('delete', old.id, old.register_id, old.transaction_no);
            END;

            CREATE TRIGGER IF NOT EXISTS pos_events_fts_au AFTER UPDATE OF register_id, transaction_no ON pos_events BEGIN
                INSERT INTO pos_events_fts(pos_events_fts, rowid, register_id, transaction_no)
                VALUES ('delete', old.id, old.register_id, old.transaction_no);
                INSERT INTO pos_events_fts(rowid, register_id, transaction_no)
                VALUES (new.id, new.register_id, new.transaction_no);
            END;

            CREATE VIRTUAL TABLE IF NOT EXISTS edge_smart_events_fts USING fts5(
                id UNINDEXED,
                class_name,
                track_id,
                direction,
                content='edge_smart_events',
                content_rowid='id'
            );

            CREATE TRIGGER IF NOT EXISTS edge_smart_events_fts_ai AFTER INSERT ON edge_smart_events BEGIN
                INSERT INTO edge_smart_events_fts(rowid, class_name, track_id, direction)
                VALUES (new.id, new.class_name, new.track_id, new.direction);
            END;

            CREATE TRIGGER IF NOT EXISTS edge_smart_events_fts_ad AFTER DELETE ON edge_smart_events BEGIN
                INSERT INTO edge_smart_events_fts(edge_smart_events_fts, rowid, class_name, track_id, direction)
                VALUES ('delete', old.id, old.class_name, old.track_id, old.direction);
            END;

            CREATE TRIGGER IF NOT EXISTS edge_smart_events_fts_au AFTER UPDATE OF class_name, track_id, direction ON edge_smart_events BEGIN
                INSERT INTO edge_smart_events_fts(edge_smart_events_fts, rowid, class_name, track_id, direction)
                VALUES ('delete', old.id, old.class_name, old.track_id, old.direction);
                INSERT INTO edge_smart_events_fts(rowid, class_name, track_id, direction)
                VALUES (new.id, new.class_name, new.track_id, new.direction);
            END;
            """);
    }

    /// <summary>M98 容錯實體接管（§14.7 #9）L1：failover 接管/交還/當選事件軌跡表＋時序索引。</summary>
    private void CreateFailoverEventTablesV35()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS failover_events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id  TEXT NOT NULL,
                mode       TEXT NOT NULL CHECK (mode IN ('Leader', 'Takeover', 'Relinquish')),
                detail     TEXT NOT NULL,
                at_utc     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_failover_events_time ON failover_events(at_utc);
            """);
    }

    /// <summary>M99 企業登入落地（§14.7 #1）：登入 session 表（LDAP/OIDC 登入後簽發）＋時效索引。</summary>
    private void CreateLoginSessionTablesV36()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS login_sessions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT    NOT NULL UNIQUE,
                user_id    INTEGER NOT NULL,
                username   TEXT    NOT NULL,
                role       TEXT    NOT NULL,
                provider   TEXT    NOT NULL DEFAULT 'local',
                issued_at  TEXT    NOT NULL,
                expires_at TEXT    NOT NULL,
                revoked_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_login_sessions_session ON login_sessions(session_id);
            CREATE INDEX IF NOT EXISTS idx_login_sessions_expiry  ON login_sessions(expires_at);
            """);
    }

    private void CreateRedactionTablesV37()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS redaction_regions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                source_type    TEXT    NOT NULL,
                ref_id         INTEGER NOT NULL,
                channel_id     INTEGER NOT NULL,
                occurred_at_utc TEXT   NOT NULL,
                x              INTEGER NOT NULL,
                y              INTEGER NOT NULL,
                width          INTEGER NOT NULL,
                height         INTEGER NOT NULL,
                filled         INTEGER NOT NULL DEFAULT 0,
                created_at_utc TEXT   NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_redaction_source   ON redaction_regions(source_type, ref_id);
            CREATE INDEX IF NOT EXISTS idx_redaction_channel  ON redaction_regions(channel_id, occurred_at_utc);
            """);
    }

    /// <summary>回放書籤（§8.5 §11.4 側欄書籤列表）：操作員在某通道某時刻標記的重點與筆記。</summary>
    private void CreateBookmarkTableV56()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS bookmarks (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id     INTEGER NOT NULL,
                at_utc         TEXT    NOT NULL,
                note           TEXT    NOT NULL DEFAULT '',
                created_by     TEXT    NOT NULL DEFAULT '',
                created_at_utc TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_bookmarks_channel ON bookmarks(channel_id, at_utc);
            """);
    }

    private void CreateAlarmWorkflowTablesV38()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS alarm_notes (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id   INTEGER NOT NULL,
                author     TEXT    NOT NULL,
                note       TEXT    NOT NULL,
                created_at TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_alarm_notes_event ON alarm_notes(event_id, id);

            CREATE TABLE IF NOT EXISTS alarm_escalations (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id      INTEGER NOT NULL,
                level         INTEGER NOT NULL,
                from_priority TEXT    NOT NULL,
                to_priority   TEXT    NOT NULL,
                due_utc       TEXT    NOT NULL,
                created_at    TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_alarm_escalation_event ON alarm_escalations(event_id, id);
            """);
    }

    private void CreateSmartwallTablesV39()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS smartwall_layouts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT    NOT NULL UNIQUE,
                rows         INTEGER NOT NULL,
                cols         INTEGER NOT NULL,
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS smartwall_tiles (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                layout_id   INTEGER NOT NULL,
                row         INTEGER NOT NULL,
                col         INTEGER NOT NULL,
                rowspan     INTEGER NOT NULL,
                colspan     INTEGER NOT NULL,
                channel_id  INTEGER NULL,
                view        INTEGER NOT NULL,
                position    INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_smartwall_tile_layout ON smartwall_tiles(layout_id, position);
            """);
    }

    private void CreateAuditLogTablesV40()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT    NOT NULL,
                actor       TEXT    NOT NULL,
                action      TEXT    NOT NULL,
                category    TEXT    NOT NULL,
                target_type TEXT    NULL,
                target_id   INTEGER NULL,
                detail      TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS idx_audit_log_time ON audit_log(occurred_at DESC, id DESC);
            """);
    }

    private void CreateEmbeddingTablesV41()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS clip_embeddings (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                source_type TEXT    NOT NULL,
                ref_id      INTEGER NOT NULL,
                label       TEXT    NULL,
                vector      BLOB    NOT NULL,
                norm        REAL    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_clip_embeddings_type_id ON clip_embeddings(source_type, ref_id);
            """);
    }

    private void CreateAudioTablesV42()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS event_audio (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id       INTEGER NOT NULL UNIQUE,
                channel_id     INTEGER NOT NULL,
                started_at_utc TEXT    NOT NULL,
                duration_ms    INTEGER NOT NULL,
                mime           TEXT    NOT NULL,
                bytes          BLOB    NOT NULL,
                sha256         TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_event_audio_event ON event_audio(event_id);
            CREATE INDEX IF NOT EXISTS idx_event_audio_time ON event_audio(started_at_utc);
            """);
    }

    /// <summary>
    /// 授權表（§19.7）：product 端保存已匯入的授權與其驗證歷程。
    /// <c>device_code</c> 唯一（同一台機器只認一張授權，換發即覆寫並重新計 first_seen 以外的欄位）。
    /// <c>tier</c>／<c>max_cameras</c>／<c>expired_at</c> 為快取欄，避免 UI 與稽核查詢時還要解簽 payload。
    /// </summary>
    private void CreateLicenseTableV43()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS license (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                key_text      TEXT    NOT NULL,
                device_code   TEXT    NOT NULL UNIQUE,
                license_id    TEXT,
                tier          TEXT,
                max_cameras   INTEGER NOT NULL,
                features      TEXT    NOT NULL,
                issuer        TEXT,
                expired_at    TEXT    NULL,
                first_seen    TEXT    NOT NULL,
                last_verified TEXT    NULL,
                status        TEXT    NOT NULL,
                created_by    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_license_status ON license(status);
            CREATE INDEX IF NOT EXISTS idx_license_verified ON license(last_verified DESC);
            """);
    }

    /// <summary>
    /// 中央節點清單（§14.1 #10 中央/多機集群管理，M295）。
    /// 中央站以此登記要監控的子節點；<c>base_url</c> 唯一，避免同一台被重複探測。
    /// <c>api_key</c> 以明文保存，與同庫的授權 <c>key_text</c> 一致——資料庫本身即安全邊界。
    /// <c>online</c>／<c>version</c>／<c>channels</c>／<c>last_seen</c> 是最近一次探測的快取，
    /// 供 UI 不必逐台連線即可顯示狀態。
    /// </summary>
    private void CreateCentralNodesTableV48()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS central_nodes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                base_url    TEXT    NOT NULL UNIQUE,
                api_key     TEXT    NOT NULL,
                enabled     INTEGER NOT NULL,
                node_id     TEXT    NULL,
                version     TEXT    NULL,
                channels    INTEGER NULL,
                online      INTEGER NULL,
                last_seen   TEXT    NULL,
                last_error  TEXT    NULL,
                created_at  TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_central_nodes_enabled ON central_nodes(enabled);
            """);
    }

    /// <summary>
    /// 監看群組（v49）：監控中心可用頁簽切換自選的攝影機子集。
    ///
    /// <para>
    /// <c>channel_groups</c>＝一個頁簽；<c>position</c> 決定頁簽順序（「全部」固定為第一個頁簽，不在此表）。
    /// <c>channel_group_members</c>＝群組成員與其在該群組內的顯示順序；
    /// <c>UNIQUE(channel_id)</c> 就是「一台攝影機最多屬於一個群組」這條規則本身——放在 schema 而不是程式碼裡，
    /// 重複指派在資料層直接被擋下，不依賴每個呼叫端都記得先解除舊歸屬。
    /// </para>
    /// <para>
    /// 兩個外鍵都是 <c>ON DELETE CASCADE</c>：刪群組連帶清成員、刪頻道也連帶清成員。
    /// 少了後者，刪掉頻道後 <c>channel_group_members</c> 會留下指向不存在頻道的孤兒列，
    /// 監看群組頁簽上就會少一格卻找不到原因（孤兒列不會有任何錯誤）。
    /// </para>
    /// </summary>
    private void CreateChannelGroupTablesV49()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS channel_groups (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                name     TEXT    NOT NULL UNIQUE,
                position INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS channel_group_members (
                group_id   INTEGER NOT NULL REFERENCES channel_groups(id) ON DELETE CASCADE,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                position   INTEGER NOT NULL,
                PRIMARY KEY (group_id, channel_id),
                UNIQUE (channel_id)
            );

            CREATE INDEX IF NOT EXISTS idx_channel_group_members_group
                ON channel_group_members(group_id, position);
            """);
    }

    /// <summary>
    /// 裝置群組（v50，§7.4）：攝影機設備樹的「群組／分區」層。
    ///
    /// <para>
    /// <c>device_groups</c> 自我參照（<c>parent_id</c>）以便分區可再分子區；
    /// 設備以 <c>devices.group_id</c> 掛到群組下，通道再隨設備掛入，形成「群組→設備→通道」三層樹。
    /// </para>
    /// <para>
    /// 兩個刪除路徑都刻意用 <c>SET NULL</c> 而非 CASCADE：刪一個分區不應該連帶刪掉整批攝影機
    /// （那會連錄影索引一起清掉）。刪群組只讓底下設備／子群組回到「未分組」，由操作員自行處置。
    /// </para>
    /// </summary>
    private void CreateDeviceGroupTablesV50()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS device_groups (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT    NOT NULL UNIQUE,
                parent_id INTEGER NULL REFERENCES device_groups(id) ON DELETE SET NULL,
                position  INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_device_groups_parent
                ON device_groups(parent_id, position);
            """);

        EnsureColumn("devices", "group_id", "INTEGER REFERENCES device_groups(id) ON DELETE SET NULL");
    }

    /// <summary>
    /// 遠端操作紀錄（v51，§7.4）：每台設備的「重啟／韌體升級／診斷」等命令與結果。
    ///
    /// <para>
    /// 這裡刻意用 <c>CASCADE</c>（不同於群組的 <c>SET NULL</c>）：這張表是「設備的操作歷史」，
    /// 設備一旦刪除，其歷史也失去主體，留著只會指向不存在的設備。真正的稽核軌跡另有
    /// <c>audit_log</c>（不隨設備刪除），所以刪設備不會讓「誰在何時重啟過」這件事消失。
    /// </para>
    /// </summary>
    private void CreateDeviceOperationTableV51()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS device_operations (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id  INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
                op_type    TEXT    NOT NULL,
                param      TEXT    NULL,
                result     TEXT    NOT NULL,
                detail     TEXT    NULL,
                created_at TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_device_operations_device
                ON device_operations(device_id, created_at);
            """);
    }

    /// <summary>
    /// 設定版本快照（v52，§9.5）：整份 <c>app_settings</c> 的 JSON 快照，供「一鍵回滾至先前版本」。
    ///
    /// <para>
    /// 快照存的是「當時全部鍵值」的完整複本，而非差異：設定量小（數十鍵），完整複本
    /// 才能在還原時也把「快照之後才新增的鍵」一併移除，得到真正一致的舊狀態。
    /// </para>
    /// </summary>
    private void CreateSettingsVersionTableV52()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS settings_versions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot   TEXT    NOT NULL,
                key_count  INTEGER NOT NULL,
                reason     TEXT    NULL,
                actor      TEXT    NOT NULL,
                created_at TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_settings_versions_created
                ON settings_versions(created_at);
            """);
    }

    /// <summary>
    /// 為 <c>segments</c> 補上 <c>(status, start_time)</c> 複合索引。
    ///
    /// <para>
    /// 保留清理（<c>status='final' AND start_time &lt; ?</c> 依 <c>start_time</c> 排序）、
    /// 最舊區段查詢（<c>status='final'</c> 依 <c>start_time</c> 排序，配額與浮水印用）
    /// 與備份全量掃描（<c>status='final'</c>）都以 <c>status</c> 過濾、以全域
    /// <c>start_time</c> 排序，但原本唯一的索引是 <c>(channel_id, start_time)</c>——
    /// 前置欄位是 channel_id，這些查詢一個都用不上，只能整表掃描再排序。
    /// </para>
    /// <para>
    /// 現場症狀：16 路 30 天全時錄影（數十萬至百萬列）時，每次保留清理都變成整表掃描，
    /// 而清理又是「刪一筆、重查一次」的迴圈，磁碟寫入速度遠快於清理，空間只增不減。
    /// </para>
    /// </summary>
    private void AddSegmentRetentionIndexV53()
    {
        Execute(
            """
            CREATE INDEX IF NOT EXISTS idx_seg_status_time ON segments(status, start_time);
            """);
    }

    /// <summary>
    /// 錄影完整性自我檢查（§11.1 寫盤保護、§15.7 定期 ffprobe 抽查）用的「已檢查時刻」欄位。
    ///
    /// <para>
    /// 抽驗是分頁進行的：每輪取少數「尚未檢查」的已封檔區段，檢查後記錄時刻，下一輪才會往前推進。
    /// 若不用欄位記進度，每輪都從最舊的開始重驗，較新的段永遠輪不到。以部分索引
    /// （只涵蓋 <c>status='final' AND checked_at_utc IS NULL</c>）支撐「取 N 筆待檢查」查詢，
    /// 索引會隨檢查進度自我縮小，不會隨千萬列歷史資料膨脹。
    /// </para>
    /// </summary>
    private void AddSegmentCheckedAtV54()
    {
        EnsureColumn("segments", "checked_at_utc", "TEXT");
        Execute(
            """
            CREATE INDEX IF NOT EXISTS idx_seg_unchecked
                ON segments(start_time) WHERE status = 'final' AND checked_at_utc IS NULL;
            """);
    }

    /// <summary>
    /// 錄影單一擁有者租約（M321，§11.1 進程分離）。桌面與 Headless 服務共用同一個資料庫，
    /// 以此單列（<c>id=1</c>）仲裁「誰在錄影」：只有租約持有者開錄影機，避免兩份行程同時
    /// 對同一台攝影機各開一個 ffmpeg、寫出重複區段而 log 乾淨。
    ///
    /// <para>
    /// 語意比照 <see cref="FailoverRepository"/> 的 <c>failover_state</c> 單列租約：空庫即取得、
    /// 同一擁有者續約、他人租約過期可接管；另帶 <c>priority</c>，讓「服務」（較高）能主動搶下
    /// 正在由「桌面」（較低）持有的租約——否則先開的行會永遠佔住租約，做不到「錄影核心＝服務」。
    /// </para>
    /// </summary>
    private void CreateRecordingLeaseTableV55()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS recording_lease (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                owner_id TEXT NOT NULL,
                priority INTEGER NOT NULL,
                lease_expires_utc TEXT NOT NULL
            );
            """);
    }

    /// <summary>
    /// 在單一交易中執行多步驟寫入；任何例外都整批復原（§9.5 設定還原需「全有或全無」，
    /// 否則刪光設定後補回途中失敗會留下半套設定）。交易內可直接呼叫 <see cref="Execute"/>／
    /// <see cref="Query"/>，它們會自動加入同一交易；巢狀呼叫則沿用外層交易。
    /// </summary>
    public void InTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_tx is not null)
            {
                action();
                return;
            }

            using var tx = _connection.BeginTransaction();
            _tx = tx;
            try
            {
                action();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                _tx = null;
            }
        }
    }

    /// <summary>執行無回傳 SQL（呼叫端負責參數）。</summary>
    public void Execute(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _tx;
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
            cmd.Transaction = _tx;
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