using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>
/// NTP 對時相關的設定鍵與狀態存取（M305；M310 起校時可由 <c>HeliVMS.Agent</c> 服務代為執行）。
///
/// <para>
/// 桌面程式與 Agent 服務共用同一顆 SQLite 設定庫，鍵名集中在這裡，避免兩邊各寫一份字串而默默對不上。
/// 服務以 LocalSystem／LocalService 身分執行，預設持有 <c>SeSystemtimePrivilege</c>，因此由它實際
/// 呼叫 <c>SetSystemTime</c>；桌面只負責查詢、顯示，並在自身無權限時於設定庫留下「請求校時」記號。
/// </para>
/// </summary>
public static class NtpSettings
{
    public const string Enabled = "ntp.enabled";
    public const string Server = "ntp.server";
    public const string LastSyncUtc = "ntp.last_sync_utc";
    public const string LastOffsetMs = "ntp.last_offset_ms";
    public const string RequestUtc = "ntp.request_utc";
    public const string AppliedRequestUtc = "ntp.applied_request_utc";
    public const string ClockAttemptUtc = "ntp.clock_attempt_utc";
    public const string ClockSetUtc = "ntp.clock_set_utc";
    public const string ClockError = "ntp.clock_error";

    /// <summary>服務自動對時的間隔；與桌面主視窗的週期一致（NTP 伺服器通常也建議這個頻率）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>預設伺服器；設定留空時使用。刻意選 Windows 內建的公用時間來源。</summary>
    public const string DefaultServer = "time.windows.com";

    public static string ReadServer(SettingsRepository settings)
    {
        var configured = settings.Get(Server);
        return string.IsNullOrWhiteSpace(configured) ? DefaultServer : configured.Trim();
    }

    public static bool IsEnabled(SettingsRepository settings) => settings.Get(Enabled) == "1";

    /// <summary>桌面程式要求服務立即校時：寫下一個單調遞增的請求時間。</summary>
    public static void RequestSync(SettingsRepository settings)
        => settings.Set(RequestUtc, FormatUtc(DateTime.UtcNow));

    public static DateTime? ReadUtc(SettingsRepository settings, string key) => ParseUtc(settings.Get(key));

    /// <summary>上次對時（查詢）的時間與偏移量；從未對時時回傳 null。</summary>
    public static (DateTime ServerUtc, long OffsetMs)? ReadLast(SettingsRepository settings)
    {
        if (ParseUtc(settings.Get(LastSyncUtc)) is not DateTime when)
        {
            return null;
        }

        var offset = long.TryParse(settings.Get(LastOffsetMs), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? ms
            : 0;
        return (when, offset);
    }

    /// <summary>
    /// 是否該進行一次校時：有尚未套用的請求，或距上次嘗試已達 <paramref name="interval"/>（第一次沒有
    /// 上次嘗試時間，視為到期）。
    /// </summary>
    /// <remarks>
    /// 只比時間、不碰資料庫，讓「何時該校時」這條判斷能被單元測試直接驗證；服務端每個 tick 都靠它決定動不動。
    /// </remarks>
    public static bool IsDue(
        DateTime? lastAttemptUtc,
        DateTime? requestUtc,
        DateTime? appliedRequestUtc,
        DateTime nowUtc,
        TimeSpan interval)
    {
        // 有請求且尚未套用（請求時間比上次套用的新）→ 立即校時。
        if (requestUtc is DateTime request && (appliedRequestUtc is not DateTime applied || request > applied))
        {
            return true;
        }

        // 週期到了（沒有上次嘗試＝服務剛啟動）→ 校時。
        return lastAttemptUtc is not DateTime last || nowUtc - last >= interval;
    }

    public static string FormatUtc(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    public static string FormatOffsetMs(TimeSpan offset)
        => ((long)offset.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ? ts : null;
}
