using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>看門狗對目前錄影心跳的判定結果。</summary>
public sealed record WatchdogVerdict(
    RecordingHeartbeatStatus Status,
    RecordingHeartbeatRecord? Last,
    TimeSpan Age)
{
    /// <summary>錄影行程活著且在錄。</summary>
    public bool IsHealthy => Status == RecordingHeartbeatStatus.Fresh;
}

/// <summary>
/// 錄影看門狗（M320，§11.1）。
///
/// <para>
/// 純判定：讀錄影心跳檔並回答「錄影還在跑嗎」。真正的告警／重啟動作在
/// <see cref="RecordingWatchdogMonitor"/>，把判定與副作用分開，判定就能用
/// 「寫一個心跳檔、把時鐘往前撥」直接測，不必等真的過期或真的叫起一個行程。
/// </para>
/// </summary>
public static class RecordingWatchdog
{
    /// <summary>判定目前心跳；<paramref name="staleAfter"/> 為 null 時用 <see cref="RecordingHeartbeat.DefaultStaleAfter"/>。</summary>
    public static WatchdogVerdict Evaluate(string dataRoot, DateTime nowUtc, TimeSpan? staleAfter = null)
    {
        var last = RecordingHeartbeat.TryRead(dataRoot);
        var threshold = staleAfter ?? RecordingHeartbeat.DefaultStaleAfter;
        var status = RecordingHeartbeat.Evaluate(last, nowUtc, threshold);
        // 沒有心跳時 Age 沒有意義，回零並由 Status=Missing 表達。
        var age = last is null ? TimeSpan.Zero : nowUtc - last.UpdatedUtc;
        return new WatchdogVerdict(status, last, age);
    }
}

/// <summary>
/// 看門狗的告警與重啟（M320，§11.1）。每輪餵它一個判定，它負責把「由正常轉為異常」
/// 與「由異常轉回正常」各留一筆稽核，並在異常轉折時（可選地）重新拉起錄影程式。
///
/// <para>
/// <b>只在狀態轉折時動作</b>：看門狗每分鐘跑一次，若每輪都寫一筆，一台真的掛掉的機器會
/// 用雜訊把稽核表灌滿，反而把這次故障淹掉；重啟也一樣只做一次，不然會變成不斷重複啟動。
/// </para>
/// <para>
/// 重啟對象是可選設定 <see cref="RestartExeKey"/>：未設定時只告警（很多部署是靠服務控制管理員
/// 或登入自啟恢復，硬去拉一個行程反而可能拉到錯的東西）。<c>launch</c> 未提供時
/// 也只告警。狀態保存在記憶體（看門狗行程自己的生命週期），重新啟動看門狗最壞就是重新告警一次。
/// </para>
/// </summary>
public sealed class RecordingWatchdogMonitor
{
    /// <summary>稽核動作：看門狗偵測到錄影停擺。</summary>
    public const string AuditAction = "recording.watchdog";

    /// <summary>稽核動作：錄影恢復心跳。</summary>
    public const string RecoveredAction = "recording.watchdog_recovered";

    /// <summary>設定鍵：偵測到停擺時要重新拉起的執行檔路徑（選填）。</summary>
    public const string RestartExeKey = "recording.watchdog.restart_exe";

    private readonly string _dataRoot;
    private readonly AuditLogRepository _audit;
    private readonly TimeSpan _staleAfter;
    private readonly Func<string?>? _restartExe;
    private readonly Action<string>? _launch;
    private bool _down;

    public RecordingWatchdogMonitor(
        string dataRoot,
        AuditLogRepository audit,
        TimeSpan? staleAfter = null,
        Func<string?>? restartExe = null,
        Action<string>? launch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(audit);
        _dataRoot = dataRoot;
        _audit = audit;
        _staleAfter = staleAfter ?? RecordingHeartbeat.DefaultStaleAfter;
        _restartExe = restartExe;
        _launch = launch;
    }

    /// <summary>目前是否處於「已知停擺」狀態。</summary>
    public bool IsDown => _down;

    /// <summary>
    /// 判定一次並在狀態轉折時寫稽核／觸發重啟。回傳本次判定供呼叫端記錄。
    /// </summary>
    public WatchdogVerdict Evaluate(DateTime nowUtc)
    {
        var verdict = RecordingWatchdog.Evaluate(_dataRoot, nowUtc, _staleAfter);

        if (verdict.IsHealthy)
        {
            if (_down)
            {
                _down = false;
                _audit.Record(
                    "system",
                    RecoveredAction,
                    AuditCategories.Recording,
                    detail: $"active={verdict.Last?.ActiveChannels ?? 0} pid={verdict.Last?.ProcessId ?? 0}");
            }

            return verdict;
        }

        if (!_down)
        {
            _down = true;
            _audit.Record(
                "system",
                AuditAction,
                AuditCategories.Recording,
                detail: $"status={verdict.Status.ToString().ToLowerInvariant()} age={verdict.Age.TotalSeconds:0}s "
                    + $"active={verdict.Last?.ActiveChannels ?? 0} pid={verdict.Last?.ProcessId ?? 0}");

            var exe = _restartExe?.Invoke();
            if (_launch is not null && !string.IsNullOrWhiteSpace(exe))
            {
                _launch(exe);
            }
        }

        return verdict;
    }
}
