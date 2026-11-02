using HeliVMS.Shared;

namespace HeliVMS.Storage;

/// <summary>
/// 一筆排程任務（§14）：什麼動作、什麼週期、上次跑到哪裡。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LastRunUtc"/> 在建任務時就由 <see cref="ScheduledTaskRepository.Add"/> 寫成當下，
/// 而不是留空：排程判定以「上次執行」為基準，留空會讓新建的「每天 03:00 重開機」在
/// 15:00 建立當下就判定到點，機器立刻重開機。
/// </para>
/// <para>
/// 執行端（App 的宿主）在<b>動手前</b>先呼叫 <see cref="ScheduledTaskRepository.MarkRunStarted"/>，
/// 跑完才補 <see cref="ScheduledTaskRepository.SetLastResult"/>。順序反了（跑完才記）遇上斷電或當機，
/// 同一顆重開機任務會每個輪詢週期重跑一次，機器就陷入 30 秒一次的開機循環。
/// </para>
/// </remarks>
public sealed record ScheduledTaskRecord(
    long Id,
    string Name,
    MaintenanceTaskKind Kind,
    bool Enabled,
    MaintenanceScheduleKind ScheduleKind,
    int TimeOfDay,
    int WeekdaysMask,
    int IntervalMinutes,
    int? ChannelId,
    DateTime? LastRunUtc,
    string? LastResult);

/// <summary>排程任務存取（§14）：CRUD＋執行紀錄。</summary>
public sealed class ScheduledTaskRepository
{
    private readonly SqliteStore _store;

    public ScheduledTaskRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 建立任務；<c>last_run_utc</c> 固定寫成當下（見 <see cref="ScheduledTaskRecord"/> 的說明）。
    /// 回傳 Id。
    /// </summary>
    public long Add(
        string name,
        MaintenanceTaskKind kind,
        MaintenanceScheduleKind scheduleKind,
        int timeOfDay = 0,
        int weekdaysMask = MaintenanceWeekdays.All,
        int intervalMinutes = 0,
        int? channelId = null,
        bool enabled = true)
    {
        Validate(kind, scheduleKind, timeOfDay, weekdaysMask, intervalMinutes);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _store.Query(
            """
            INSERT INTO scheduled_tasks (name, kind, enabled, schedule_kind, time_of_day, weekdays, interval_min, channel_id, last_run_utc)
            VALUES ($n, $k, $e, $s, $t, $w, $m, $c, $lr);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name.Trim());
                cmd.Parameters.AddWithValue("$k", kind.ToString());
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$s", scheduleKind.ToString());
                cmd.Parameters.AddWithValue("$t", timeOfDay);
                cmd.Parameters.AddWithValue("$w", weekdaysMask);
                cmd.Parameters.AddWithValue("$m", intervalMinutes);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lr", DateTime.UtcNow);
            });
    }

    /// <summary>全部任務，依 Id 排序。</summary>
    public IReadOnlyList<ScheduledTaskRecord> List()
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, schedule_kind, time_of_day, weekdays, interval_min,
                   channel_id, last_run_utc, last_result
            FROM scheduled_tasks ORDER BY id;
            """,
            ReadRecords);
    }

    /// <summary>啟用中的任務（宿主每個輪詢週期查詢）。</summary>
    public IReadOnlyList<ScheduledTaskRecord> ListEnabled()
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, schedule_kind, time_of_day, weekdays, interval_min,
                   channel_id, last_run_utc, last_result
            FROM scheduled_tasks WHERE enabled = 1 ORDER BY id;
            """,
            ReadRecords);
    }

    /// <summary>整列覆寫任務內容（警報中心的「編輯」）；<b>不動</b> <c>last_run_utc</c>／<c>last_result</c>。回傳是否實際存在。</summary>
    /// <remarks>
    /// 編輯保留執行狀態是安全規則：把「每天 03:00 重開機」改成「每天 04:00」時，
    /// last_run 還是今天稍早的值——今天不會再跑，下次跑到點是明天 04:00。
    /// 如果 Update 順手把 last_run 重置成當下，等於「編輯＝重置今天已跑」，
    /// 一顆已經跑過的任務會在下一個輪詢被判成到點再跑一次。
    /// </remarks>
    public bool Update(
        long id,
        string name,
        MaintenanceTaskKind kind,
        MaintenanceScheduleKind scheduleKind,
        int timeOfDay = 0,
        int weekdaysMask = MaintenanceWeekdays.All,
        int intervalMinutes = 0,
        int? channelId = null,
        bool enabled = true)
    {
        Validate(kind, scheduleKind, timeOfDay, weekdaysMask, intervalMinutes);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _store.Query(
            """
            UPDATE scheduled_tasks
            SET name = $n, kind = $k, enabled = $e, schedule_kind = $s, time_of_day = $t,
                weekdays = $w, interval_min = $m, channel_id = $c
            WHERE id = $id;
            SELECT changes();
            """,
            static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name.Trim());
                cmd.Parameters.AddWithValue("$k", kind.ToString());
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$s", scheduleKind.ToString());
                cmd.Parameters.AddWithValue("$t", timeOfDay);
                cmd.Parameters.AddWithValue("$w", weekdaysMask);
                cmd.Parameters.AddWithValue("$m", intervalMinutes);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
            });
    }

    /// <summary>刪除任務。</summary>
    public bool Delete(long id)
    {
        _store.Execute("DELETE FROM scheduled_tasks WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        return true;
    }

    /// <summary>只切換啟用／停用。</summary>
    public bool SetEnabled(long id, bool enabled)
    {
        return _store.Query(
            "UPDATE scheduled_tasks SET enabled = $e WHERE id = $id; SELECT changes();",
            static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            });
    }

    /// <summary>記下「這次已經動手跑」；執行前就要呼叫（先記後跑，見 <see cref="ScheduledTaskRecord"/>）。</summary>
    public bool MarkRunStarted(long id, DateTime whenUtc)
    {
        return _store.Query(
            "UPDATE scheduled_tasks SET last_run_utc = $t WHERE id = $id; SELECT changes();",
            static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$t", whenUtc);
            });
    }

    /// <summary>補寫執行結果（成功訊息或失敗原因；顯示在任務清單）。</summary>
    public bool SetLastResult(long id, string result)
    {
        return _store.Query(
            "UPDATE scheduled_tasks SET last_result = $r WHERE id = $id; SELECT changes();",
            static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$r", result);
            });
    }

    private static IReadOnlyList<ScheduledTaskRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var list = new List<ScheduledTaskRecord>();
        while (r.Read())
        {
            list.Add(new ScheduledTaskRecord(
                r.GetInt64(0),
                r.GetString(1),
                Enum.Parse<MaintenanceTaskKind>(r.GetString(2)),
                r.GetInt64(3) != 0,
                Enum.Parse<MaintenanceScheduleKind>(r.GetString(4)),
                r.GetInt32(5),
                r.GetInt32(6),
                r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetDateTime(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        }

        return list;
    }

    /// <summary>動作與週期的組合規則：重開機只能每天／每週（「每隔 N 分鐘重開機」不是保養，是災難）。</summary>
    private static void Validate(
        MaintenanceTaskKind kind,
        MaintenanceScheduleKind scheduleKind,
        int timeOfDay,
        int weekdaysMask,
        int intervalMinutes)
    {
        switch (scheduleKind)
        {
            case MaintenanceScheduleKind.Daily:
                ValidateTimeOfDay(timeOfDay);
                break;
            case MaintenanceScheduleKind.Weekly:
                ValidateTimeOfDay(timeOfDay);
                if (weekdaysMask is < 1 or > MaintenanceWeekdays.All)
                {
                    throw new ArgumentException("Weekdays mask must select at least one day.", nameof(weekdaysMask));
                }

                break;
            case MaintenanceScheduleKind.Interval:
                if (intervalMinutes is < 1 or > 7 * 24 * 60)
                {
                    throw new ArgumentOutOfRangeException(nameof(intervalMinutes), intervalMinutes, "Interval must be 1..10080 minutes.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scheduleKind), scheduleKind, "Unknown schedule kind.");
        }

        if (kind == MaintenanceTaskKind.Reboot && scheduleKind == MaintenanceScheduleKind.Interval)
        {
            throw new ArgumentException("Reboot cannot be scheduled by interval.", nameof(scheduleKind));
        }
    }

    private static void ValidateTimeOfDay(int timeOfDay)
    {
        var hour = timeOfDay / 100;
        var minute = timeOfDay % 100;
        if (timeOfDay is < 0 or > 2359 || hour > 23 || minute > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(timeOfDay), timeOfDay, "Time of day must be HHMM.");
        }
    }
}
