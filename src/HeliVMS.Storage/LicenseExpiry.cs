namespace HeliVMS.Storage;

/// <summary>授權到期提醒等級（M211／§19.4「到期前 14 天 UI 浮條提醒」）。</summary>
public enum LicenseExpiryStage
{
    /// <summary>不需提醒：永久授權、距到期仍遠、或根本没有有效授權。</summary>
    None,

    /// <summary>14 天內到期（規格指定的提醒窗口）。</summary>
    Upcoming,

    /// <summary>3 天內到期：來不及走採購流程了，語氣要更急。</summary>
    Urgent,

    /// <summary>已到期：新增錄影已停（既有仍可回放），提醒的是續期而非功能失效。</summary>
    Expired,
}

/// <summary>到期提醒的呈現內容（純函式輸出，方便桌面端、CLI、WebApi 共用同一句話）。</summary>
/// <param name="Stage">提醒等級。</param>
/// <param name="DaysRemaining">剩餘天數，已到期為負；永久授權為 null。</param>
/// <param name="ExpiresUtc">到期時刻；永久授權為 null。</param>
public sealed record LicenseExpiryNotice(
    LicenseExpiryStage Stage,
    int? DaysRemaining,
    DateTime? ExpiresUtc)
{
    /// <summary>是否需要在 UI 上顯示浮條。</summary>
    public bool ShouldWarn => Stage != LicenseExpiryStage.None;

    /// <summary>可直接顯示給使用者的一句话提醒；不需提醒時為 null。</summary>
    public string? Message => Stage switch
    {
        LicenseExpiryStage.Upcoming =>
            $"授權將於 {ExpiresUtc:yyyy-MM-dd} 到期（剩 {DaysRemaining} 天），屆時將停止新增錄影，既有錄影仍可回放。",
        LicenseExpiryStage.Urgent =>
            $"授權即將到期（{ExpiresUtc:yyyy-MM-dd}，剩 {DaysRemaining} 天），請立即匯入新授權以免中斷錄影。",
        LicenseExpiryStage.Expired =>
            $"授權已於 {ExpiresUtc:yyyy-MM-dd} 到期，新增錄影已停止；既有錄影仍可回放，請匯入新授權。",
        _ => null,
    };
}

/// <summary>
/// 到期提醒的計算規則（M211）。抽成獨立純函式是為了讓規則可測：
/// 「14 天」是規格數字，寫死在 XAML 或各視窗裡就會有第二份真相。
/// </summary>
public static class LicenseExpiry
{
    /// <summary>規格指定的提醒窗口（§19.4「到期前 14 天」）。</summary>
    public const int WarnDays = 14;

    /// <summary>緊急門檻：3 天內。此時提醒語氣要改。</summary>
    public const int UrgentDays = 3;

    /// <summary>由授權列與現在時間算出提醒等級。</summary>
    /// <param name="expiresUtc">授權到期時刻；null 表示永久授權，不提醒。</param>
    /// <param name="nowUtc">判斷基準時間（測試可注入）。</param>
    /// <param name="valid">
    /// 要傳「有效或已到期」。已到期仍需提醒——續期訊息正是那時候最需要的；
    /// 未匯入／作廢／時鐘回流／簽章無效傳 false，那四種是「授權無效」而非「快到期」，
    /// 由設定中心與閘門回饋處理即可，不該混在同一條黃色浮條裡。
    /// </param>
    public static LicenseExpiryNotice Evaluate(DateTime? expiresUtc, DateTime nowUtc, bool valid)
    {
        if (!valid || expiresUtc is null)
        {
            return new LicenseExpiryNotice(LicenseExpiryStage.None, null, expiresUtc);
        }

        var remaining = expiresUtc.Value - nowUtc;
        var days = (int)Math.Floor(remaining.TotalDays);

        // 用總秒數而非天數判斷邊界：到期當天（不足 24 小時）算 Urgent 而非 Upcoming，
        // 否則 13.5 天會被算成 14 天而漏掉「只剩今天」的緊迫感。
        var stage = remaining <= TimeSpan.Zero
            ? LicenseExpiryStage.Expired
            : remaining.TotalDays <= UrgentDays
                ? LicenseExpiryStage.Urgent
                : remaining.TotalDays <= WarnDays
                    ? LicenseExpiryStage.Upcoming
                    : LicenseExpiryStage.None;

        return new LicenseExpiryNotice(stage, days, expiresUtc);
    }
}
