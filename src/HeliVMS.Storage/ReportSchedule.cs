using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>報表郵寄週期（§14.1 #9）。</summary>
public enum ReportCadence
{
    Off = 0,
    Daily = 1,
    Weekly = 2,
}

/// <summary>
/// 報表排程判斷（§14.1 #9）：純函式、無 I/O，讓「該不該寄」能在測試中精確驗證。
///
/// <para>
/// 之所以用「本地日曆日／週起點」而不是單純的固定間隔：操作者設定的語意是
/// 「每天早上 8 點」或「每週一 8 點」，不是「距上次寄送滿 24 小時」。
/// 若用間隔，程式重啟或補寄會讓寄送時間逐次飄移。
/// </para>
/// </summary>
public static class ReportSchedule
{
    public const string EnabledKey = "report.mail.enabled";
    public const string CadenceKey = "report.mail.cadence";
    public const string TimeKey = "report.mail.time";
    public const string WeeklyDayKey = "report.mail.weekly_day";
    public const string LastSentKey = "report.mail.last_sent";

    public const string DefaultTime = "08:00";

    public static ReportCadence ParseCadence(string? raw, ReportCadence fallback = ReportCadence.Off)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "daily" => ReportCadence.Daily,
            "weekly" => ReportCadence.Weekly,
            "off" => ReportCadence.Off,
            _ => fallback,
        };

    public static string ToKey(ReportCadence cadence) => cadence switch
    {
        ReportCadence.Daily => "daily",
        ReportCadence.Weekly => "weekly",
        _ => "off",
    };

    public static bool TryParseTime(string? raw, out TimeOnly at)
        => TimeOnly.TryParseExact(
            raw?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at);

    public static bool TryParseDay(string? raw, out DayOfWeek day)
        => Enum.TryParse(raw?.Trim(), ignoreCase: true, out day) && Enum.IsDefined(day);

    /// <summary>該週期對應的報表回顧天數（每日＝1 天、每週＝7 天、關閉＝0）。</summary>
    public static int WindowDays(ReportCadence cadence) => cadence switch
    {
        ReportCadence.Daily => 1,
        ReportCadence.Weekly => 7,
        _ => 0,
    };

    /// <summary>
    /// 是否已到達本次寄送時點。以本地時間判斷；<paramref name="lastSentLocal"/> 為 null 表示從未寄過
    /// （通過時間檢查即可寄）。同一個日曆日／同一週只寄一次，避免計時器每幾分鐘輪詢就重複寄送。
    /// </summary>
    public static bool IsDue(
        DateTime nowLocal,
        DateTime? lastSentLocal,
        ReportCadence cadence,
        TimeOnly at,
        DayOfWeek weeklyDay)
    {
        if (cadence == ReportCadence.Off)
        {
            return false;
        }

        if (TimeOnly.FromDateTime(nowLocal) < at)
        {
            return false;
        }

        if (lastSentLocal is not { } last)
        {
            return true;
        }

        if (cadence == ReportCadence.Daily)
        {
            return last.Date < nowLocal.Date;
        }

        var offset = ((int)nowLocal.DayOfWeek - (int)weeklyDay + 7) % 7;
        var weekStart = nowLocal.Date.AddDays(-offset);
        return last.Date < weekStart;
    }

    /// <summary>下一個預定寄送時刻（供 UI 顯示「下次寄送」；已過今天/本週則順延）。</summary>
    public static DateTime? NextDueLocal(DateTime nowLocal, ReportCadence cadence, TimeOnly at, DayOfWeek weeklyDay)
    {
        if (cadence == ReportCadence.Off)
        {
            return null;
        }

        var todayAt = nowLocal.Date.Add(at.ToTimeSpan());
        if (cadence == ReportCadence.Daily)
        {
            return nowLocal < todayAt ? todayAt : todayAt.AddDays(1);
        }

        var daysUntil = ((int)weeklyDay - (int)nowLocal.DayOfWeek + 7) % 7;
        var candidate = todayAt.AddDays(daysUntil);
        return candidate <= nowLocal ? candidate.AddDays(7) : candidate;
    }
}
