using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 中繼資料保管期限清理（警報事件／事件錄音／稽核日誌）：桌面與 WebApi 兩條保留迴圈共用。
/// 這些表過去只寫不刪（事件錄音又因沒有外鍵而成為孤兒），必須能依天數汰除、且天數為 0 時不動。
/// </summary>
public sealed class MetadataRetentionPrunerTests
{
    private static SqliteStore NewStore()
    {
        var store = new SqliteStore(Path.Combine(Path.GetTempPath(), $"hv-mrp-{Guid.NewGuid():N}.db"));
        store.Initialize();
        store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'ch1', 'rtsp://127.0.0.1:8554/ch1', NULL, 'h264', 0, 'copy');
            """);
        return store;
    }

    private static long Count(SqliteStore store, string table)
        => store.Query<long>($"SELECT COUNT(*) FROM {table};", r => r.Read() ? r.GetInt64(0) : 0);

    [Fact]
    public void 設定鍵與預設值是不可變的持久化契約()
    {
        // 這些鍵會寫進 settings 表成為持久資料；若被悄悄改名，既有部署的保管天數會被默默忽略
        // （改回預設值），而且不會有任何錯誤。改動必須是有意識的，並同步 WebApi 的驗證與文件。
        Assert.Equal("alarm.retention.days", MetadataRetentionPruner.AlarmDaysKey);
        Assert.Equal("audit.retention.days", MetadataRetentionPruner.AuditDaysKey);
        Assert.Equal(365, MetadataRetentionPruner.DefaultDays);
    }

    [Fact]
    public void Prune_依天數清除中繼資料並保留近期項()
    {
        using var store = NewStore();
        var alarms = new AlarmEventRepository(store);
        var audio = new EventAudioRepository(store);
        var audit = new AuditLogRepository(store);
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var oldAlarm = alarms.Insert(1, "motion", now.AddDays(-100), detail: "old");
        var newAlarm = alarms.Insert(1, "motion", now.AddMinutes(-5), detail: "new");
        audio.Save(oldAlarm, 1, now.AddDays(-100), 1000, "audio/ogg", [1, 2, 3]);
        audio.Save(newAlarm, 1, now.AddMinutes(-5), 1000, "audio/ogg", [4, 5, 6]);
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: now.AddDays(-100));
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: now.AddMinutes(-5));

        var result = MetadataRetentionPruner.Prune(store, now, alarmDays: 30, auditDays: 30);

        Assert.Equal(1, result.AlarmPurged);
        Assert.Equal(1, result.AudioPurged);
        Assert.Equal(1, result.AuditPurged);
        Assert.Null(audio.GetByEvent(oldAlarm));
        Assert.NotNull(audio.GetByEvent(newAlarm));
        Assert.Equal(1, audit.Count(new AuditLogQuery { Action = "config.change" }));
    }

    [Fact]
    public void Prune_天數為零時不清除任何項目()
    {
        using var store = NewStore();
        var alarms = new AlarmEventRepository(store);
        var audio = new EventAudioRepository(store);
        var audit = new AuditLogRepository(store);
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var oldAlarm = alarms.Insert(1, "motion", now.AddDays(-3650), detail: "old");
        audio.Save(oldAlarm, 1, now.AddDays(-3650), 1000, "audio/ogg", [7, 8, 9]);
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: now.AddDays(-3650));

        var result = MetadataRetentionPruner.Prune(store, now, alarmDays: 0, auditDays: 0);

        Assert.Equal(0, result.AlarmPurged);
        Assert.Equal(0, result.AudioPurged);
        Assert.Equal(0, result.AuditPurged);
        Assert.Equal(1, Count(store, "alarm_events"));
        Assert.Equal(1, Count(store, "event_audio"));
        Assert.Equal(1, audit.Count(new AuditLogQuery { Action = "config.change" }));
    }
}
