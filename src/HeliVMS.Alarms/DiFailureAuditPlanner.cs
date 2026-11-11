namespace HeliVMS.Alarms;

/// <summary>
/// 攝影機 DI 監視失敗的稽核節流：<b>同一個失敗只寫一次，之後每小時最多一筆</b>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：<see cref="CameraDiMonitor"/> 的拉取迴圈每隔 <c>retryDelay</c>（預設 15 秒）
/// 重試一次，而舊版在 <c>catch</c> 裡無條件寫一筆 <c>camera.di.failed</c> 稽核。實測現場
/// （VIVOTEK IP9164-HT 的 event_service 一直回 HTTP 500）9 天寫了 <b>18,568 筆內容完全相同</b>
/// 的稽核列，佔整個 <c>audit_log</c> 錄影類別的 88%。稽核是證據鏈：操作員要查「誰改了設定」、
/// 「什麼時候開始停止錄影」時，得先翻過近兩萬筆一模一樣的字串——真正的訊號（
/// <c>recording.stalled</c> 271 筆、<c>recording.disk_full</c> 39 筆）就被埋在裡面。
/// </para>
/// <para>
/// 三條規則：
/// </para>
/// <list type="number">
/// <item><b>第一次失敗一定要寫</b>——那是「從正常變成不正常」的那一刻，是稽核最該留下的資訊。</item>
/// <item><b>原因改變立刻寫</b>（例如由「HTTP 500」變成「連線被拒」）：那是新的資訊，
/// 而且現場排查時「什麼時候換了症狀」比「一直壞」更關鍵。</item>
/// <item><b>同一個原因每小時最多再寫一筆</b>（心跳，附帶連續失敗次數）：稽核不能完全靜音——
/// 一條規則連續壞一個月卻沒有任何一列，等於這個故障從未發生過。</item>
/// </list>
/// <para>
/// 與 <c>DiskHealthCoordinator</c>「只在等級改變時寫稽核」是同一個原則；差別是這裡多一個
/// 有界心跳，因為磁碟健康的等級會存在 <c>app_settings</c> 讓畫面讀，而 DI 的失敗原因只有
/// 稽核與畫面兩處看得到。
/// </para>
/// <para>
/// 純狀態機（時間以委派注入），所以「一小時後會再寫一筆」不必真的等一小時。
/// </para>
/// </remarks>
public sealed class DiFailureAuditPlanner
{
    /// <summary>同一個原因的最長靜音時間；超過就再寫一筆（心跳）。</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 兩筆稽核之間的最短間隔。原因在兩個值之間來回跳（逾時／500）時，
    /// 沒有這道閘門就會變成「每一輪都寫一筆」，回到原本的洪水。
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _repeatInterval;
    private readonly TimeSpan _minimumInterval;

    /// <summary>最後一次<b>寫進稽核</b>的原因；<c>null</c> 表示從上次成功以來還沒寫過。</summary>
    private string? _lastAudited;

    private DateTime _lastAuditedAtUtc;

    /// <summary>自上次成功以來連續失敗的次數（不論有沒有寫稽核）。</summary>
    private long _failures;

    /// <param name="clock">現在時刻（測試注入）。</param>
    /// <param name="repeatInterval">同一原因的靜音上限，預設 <see cref="RepeatInterval"/>。</param>
    /// <param name="minimumInterval">兩筆稽核的最短間隔，預設 <see cref="MinimumInterval"/>。</param>
    public DiFailureAuditPlanner(
        Func<DateTime>? clock = null,
        TimeSpan? repeatInterval = null,
        TimeSpan? minimumInterval = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _repeatInterval = repeatInterval ?? RepeatInterval;
        _minimumInterval = minimumInterval ?? MinimumInterval;
    }

    /// <summary>自上次成功以來連續失敗的次數。</summary>
    public long ConsecutiveFailures => _failures;

    /// <summary>
    /// 這一輪的失敗要不要寫稽核。
    /// </summary>
    /// <param name="failure">失敗原因（例外訊息即可；只當作「是不是同一個原因」的字串比對）。</param>
    /// <param name="consecutiveFailures">連續失敗次數，寫進稽核說明。</param>
    /// <returns>要寫入時回 <c>true</c>。</returns>
    public bool ShouldAudit(string? failure, out long consecutiveFailures)
    {
        var reason = string.IsNullOrWhiteSpace(failure) ? "(未知)" : failure;
        var now = _clock();
        _failures++;
        consecutiveFailures = _failures;

        // 第一次失敗：從正常變成不正常，一定要留下記錄。
        if (_lastAudited is null)
        {
            return Record(reason, now);
        }

        var elapsed = now - _lastAuditedAtUtc;

        // 同一個原因：等到心跳時間到才再寫（稽核不能對一個持續中的故障完全靜音）。
        if (string.Equals(reason, _lastAudited, StringComparison.Ordinal))
        {
            return elapsed >= _repeatInterval && Record(reason, now);
        }

        // 原因換了：那是有用的新資訊，但仍在最短間隔之後才寫，
        // 否則「逾時／HTTP 500」輪流出現時每一輪都是一筆。
        return elapsed >= _minimumInterval && Record(reason, now);
    }

    /// <summary>成功一輪（拉到事件或逾時返回）：下一次失敗要重新寫一筆稽核。</summary>
    public void Reset()
    {
        _lastAudited = null;
        _failures = 0;
    }

    private bool Record(string reason, DateTime nowUtc)
    {
        _lastAudited = reason;
        _lastAuditedAtUtc = nowUtc;
        return true;
    }
}
