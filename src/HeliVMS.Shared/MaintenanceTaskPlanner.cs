namespace HeliVMS.Shared;

/// <summary>排程任務的動作類型（§14 排程任務）。</summary>
public enum MaintenanceTaskKind
{
    /// <summary>排程重開機（先請本程式收尾，再交給 Windows 的 shutdown）。</summary>
    Reboot,

    /// <summary>定時截圖：對目標頻道（或全部頻道）各抓一張畫面存證。</summary>
    Snapshot,

    /// <summary>重連攝影機：把全部監看工作階段重建一次（串流不穩時的夜間保養）。</summary>
    ReconnectChannels,

    /// <summary>重新啟動本程式（長時間執行的行程定期釋放記憶體與控制代碼；不重開機）。</summary>
    RestartApp,
}

/// <summary>排程任務的週期類型。</summary>
public enum MaintenanceScheduleKind
{
    /// <summary>每天 HHMM。</summary>
    Daily,

    /// <summary>每週選定的星期幾（多位元遮罩）HHMM。</summary>
    Weekly,

    /// <summary>每隔 N 分鐘一次。</summary>
    Interval,
}

/// <summary>
/// 星期遮罩（<see cref="MaintenanceScheduleKind.Weekly"/> 用）：bit0＝週日 … bit6＝週六。
/// 與 .NET 的 <see cref="DayOfWeek"/> 序數一致，遮罩就是 <c>1 &lt;&lt; (int)day</c>。
/// </summary>
public static class MaintenanceWeekdays
{
    /// <summary>週日到週六全部選取。</summary>
    public const int All = 0b0111_1111;

    public static int MaskFor(DayOfWeek day) => 1 << (int)day;

    public static bool Has(int mask, DayOfWeek day) => (mask & MaskFor(day)) != 0;
}

/// <summary>
/// 排程任務的「什麼時候該跑」計算（純函式，無 IO）。
/// </summary>
/// <remarks>
/// <para>
/// 到點判定刻意<b>不做跨時段補跑</b>：某個排定時段被錯過（機器凌晨沒開、程式沒在跑），
/// 就整個跳過，等下一個週期。判準是「上一個排定時段已被執行過，且今天的到點時刻已過」。
/// 如果用「時間過了且上次執行比它早就補跑」，操作員早上把機器開機、HeliVMS 自啟之後
/// 30 秒內就會把機器又重開一次——那不是保養，是故障。截圖漏一次的下一次週期會補上，
/// 代價可接受；重開機更沒有補跑的理由。
/// </para>
/// <para>
/// 「從未執行」視為到點（時間已過即跑）：宿主層在建任務時就先把 <c>last_run</c> 寫成當下，
/// 所以正常情況下不會出現 null；這條只是資料異常時的兜底。第一次執行落在<b>下一個</b>
/// 到點時刻這件事，由宿主的「建立即寫 last_run」保證，不放在規劃器。
/// </para>
/// </remarks>
public static class MaintenanceTaskPlanner
{
    /// <summary>是否到點。<paramref name="lastRunLocal"/> 為 null 表示從未執行（宿主層通常不會讓它發生）。</summary>
    public static bool IsDue(
        MaintenanceTaskKind kind,
        MaintenanceScheduleKind schedule,
        int timeOfDay,
        int weekdaysMask,
        int intervalMinutes,
        DateTime nowLocal,
        DateTime? lastRunLocal)
    {
        return schedule switch
        {
            MaintenanceScheduleKind.Daily => IsDailyDue(timeOfDay, nowLocal, lastRunLocal),
            MaintenanceScheduleKind.Weekly => IsWeeklyDue(timeOfDay, weekdaysMask, nowLocal, lastRunLocal),
            MaintenanceScheduleKind.Interval => IsIntervalDue(intervalMinutes, nowLocal, lastRunLocal),
            _ => false,
        };

        // kind 目前不影響到點（三種動作共用同一套週期）；保留在參數裡是為了讓
        // 呼叫端不必自己拆 record，也讓未來「某種動作限定某種週期」有落腳處。
    }

    /// <summary>
    /// 每天 HHMM：過了今天的到點時刻，且上一個排定時段（昨天同一時刻）已被執行過。
    /// 錯過的時段不補跑（見型別註解）。
    /// </summary>
    public static bool IsDailyDue(int timeOfDay, DateTime nowLocal, DateTime? lastRunLocal)
    {
        var at = AtToday(timeOfDay, nowLocal);
        if (nowLocal < at)
        {
            return false;
        }

        return lastRunLocal is not { } last
            || (last >= at.AddDays(-1) && last < at);
    }

    /// <summary>
    /// 每週遮罩 HHMM：今天在遮罩內才看今天；過了今天的到點時刻，且上一個排定日同時刻
    /// 已被執行過。不是排定日不跑，錯過的排定日也不補跑。
    /// </summary>
    public static bool IsWeeklyDue(int timeOfDay, int weekdaysMask, DateTime nowLocal, DateTime? lastRunLocal)
    {
        if (!MaintenanceWeekdays.Has(weekdaysMask, nowLocal.DayOfWeek))
        {
            return false;
        }

        var at = AtToday(timeOfDay, nowLocal);
        if (nowLocal < at)
        {
            return false;
        }

        if (lastRunLocal is not { } last)
        {
            return true;
        }

        // 上一個排定日：從昨天往回找最多 6 天。
        var prevDate = at.Date.AddDays(-1);
        while (!MaintenanceWeekdays.Has(weekdaysMask, prevDate.DayOfWeek))
        {
            prevDate = prevDate.AddDays(-1);
        }

        var prev = prevDate.AddHours(at.Hour).AddMinutes(at.Minute);
        return last >= prev && last < at;
    }

    /// <summary>每隔 N 分鐘：距上次執行滿 N 分鐘。從未執行視為立刻到點（截圖／重連的漏跑在啟動時補上無害）。</summary>
    public static bool IsIntervalDue(int intervalMinutes, DateTime nowLocal, DateTime? lastRunLocal)
    {
        if (intervalMinutes <= 0)
        {
            return false;
        }

        return lastRunLocal is not { } last || nowLocal - last >= TimeSpan.FromMinutes(intervalMinutes);
    }

    /// <summary>今天本地時間的到點時刻（HHMM；分鐘與小時由呼叫端先驗過）。</summary>
    public static DateTime AtToday(int timeOfDay, DateTime nowLocal)
    {
        var hour = Math.Clamp(timeOfDay / 100, 0, 23);
        var minute = Math.Clamp(timeOfDay % 100, 0, 59);
        return nowLocal.Date.AddHours(hour).AddMinutes(minute);
    }
}
