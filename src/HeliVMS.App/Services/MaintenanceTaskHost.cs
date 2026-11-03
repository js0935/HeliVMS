using System.Diagnostics;
using System.IO;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 排程任務宿主（§14）：每 30 秒輪詢一次 <c>scheduled_tasks</c>，到點就執行
/// 重開機／定時截圖／重連攝影機，並把結果寫回任務與稽核。
/// </summary>
/// <remarks>
/// <para>
/// 「該不該跑」的判定在 <see cref="MaintenanceTaskPlanner"/>（純函式，Shared）；
/// 本類別只負責輪詢與執行——對照 <see cref="HeliVMS.Recording.RecordingScheduler"/> 的分工。
/// </para>
/// <para>
/// 兩條鐵律，錯一條就是現場事故：
/// <b>先記後跑</b>——<see cref="ScheduledTaskRepository.MarkRunStarted"/> 必須在動手前寫入，
/// 否則宿主在執行途中死掉（斷電、當機），下一個 30 秒輪詢又判定到點，重開機任務就會
/// 把機器拖進 30 秒一次的開機循環；
/// <b>稽核先於動作</b>——重開機會讓行程消失，稽核如果排在動作之後就永遠不會被寫下來，
/// 「機器為什麼自己重開機」就查不到。
/// </para>
/// <para>
/// 重開機的執行分兩步：先請 Windows 排定 <c>shutdown /r /t 90</c>（行程死了它仍然有效，
/// 是最後的兜底），再觸發 <see cref="RebootRequested"/> 讓主視窗走既有的收尾序列
/// （ffmpeg 先收到 q，正在錄的段才收得了尾）。收尾慢於 90 秒時 Windows 會強制關機，
/// 與沒有這條路徑時的下場相同，不會更糟。
/// </para>
/// </remarks>
public sealed class MaintenanceTaskHost : IDisposable
{
    /// <summary>輪詢間隔。與錄影排程器同級：任務的最小單位是分鐘，30 秒的粒度足夠。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>重開機前留給本程式收尾的時間（shutdown /t）。要涵蓋「請 ffmpeg 收尾＋關資料庫」的預算。</summary>
    internal const int RebootGraceSeconds = 90;

    private readonly SqliteStore _store;
    private readonly ScheduledTaskRepository _tasks;
    private readonly ChannelRepository _channels;
    private readonly AuditLogRepository _audit;
    private readonly string _snapshotsRoot;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>進度提示（寫到主視窗的提示列；與巡航／通知共用同一個出口）。</summary>
    public event EventHandler<string>? Activity;

    /// <summary>重開機任務到點：主視窗應立刻走既有收尾序列並結束行程。</summary>
    public event EventHandler? RebootRequested;

    /// <summary>重連任務到點：回傳重連成功的工作階段數（宿主會把數字寫進任務結果）。</summary>
    public Func<Task<int>>? ReconnectHandler { get; set; }

    public MaintenanceTaskHost(SqliteStore store, string snapshotsRoot)
    {
        _store = store;
        _tasks = new ScheduledTaskRepository(store);
        _channels = new ChannelRepository(store);
        _audit = new AuditLogRepository(store);
        _snapshotsRoot = snapshotsRoot;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        _timer.Change(TickInterval, TickInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _gate.Dispose();
    }

    /// <summary>一輪輪詢。上一輪還沒跑完就跳過本輪（任務以分鐘為單位，追趕沒有意義）。</summary>
    internal async Task TickAsync()
    {
        if (_disposed)
        {
            return;
        }

        // TryWait 而不是 WaitAsync：排程任務不怕晚一輪，怕的是兩輪交錯改同一列。
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            await EvaluateAsync();
        }
        catch (Exception)
        {
            // 單輪失敗（磁碟忙、資料庫鎖）不必中止排程：下一輪照常評估。
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 手動立刻執行一次排程任務（警報中心的「執行」按鈕）。回傳任務的執行結果字串，
    /// 任務不存在或宿主已停止時回 null。
    /// </summary>
    /// <remarks>
    /// 與輪詢共用同一條 <see cref="RunTaskAsync"/>：先記後跑、稽核先於動作、
    /// 重開機同樣先排 shutdown 再請主視窗收尾。與輪詢相反，這裡搶不到閘門時
    /// <b>排隊等待</b>而不是跳過——操作員按了按鈕就該執行，晚一輪是背景輪詢的取捨，
    /// 不是手動觸發的。停用中的任務也允許手動跑：按鈕是操作員的明確意圖，
    /// 先試跑再啟用是合理的驗證流程。
    /// </remarks>
    public async Task<string?> RunNowAsync(long id)
    {
        if (_disposed)
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            var task = _tasks.List().FirstOrDefault(t => t.Id == id);
            if (task is null)
            {
                return null;
            }

            await RunTaskAsync(task);
            return _tasks.List().FirstOrDefault(t => t.Id == id)?.LastResult;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EvaluateAsync()
    {
        var tasks = _tasks.ListEnabled();
        var nowLocal = DateTime.Now;

        foreach (var task in tasks)
        {
            var lastRunLocal = task.LastRunUtc?.ToLocalTime();
            var due = MaintenanceTaskPlanner.IsDue(
                task.Kind, task.ScheduleKind, task.TimeOfDay, task.WeekdaysMask, task.IntervalMinutes,
                nowLocal, lastRunLocal);
            if (!due)
            {
                continue;
            }

            await RunTaskAsync(task);
        }
    }

    private async Task RunTaskAsync(ScheduledTaskRecord task)
    {
        // 先記後跑（見型別註解）：這一列寫進資料庫之後，就算行程立刻死掉，
        // 下一輪也不會再把同一顆任務判定為到點。
        _tasks.MarkRunStarted(task.Id, DateTime.UtcNow);

        switch (task.Kind)
        {
            case MaintenanceTaskKind.Reboot:
                RunReboot(task);
                break;
            case MaintenanceTaskKind.Snapshot:
                await RunSnapshotAsync(task);
                break;
            case MaintenanceTaskKind.ReconnectChannels:
                await RunReconnectAsync(task);
                break;
        }
    }

    /// <summary>重開機：稽核 → 排定 Windows 重開機（90 秒兜底）→ 請主視窗優雅收尾。</summary>
    private void RunReboot(ScheduledTaskRecord task)
    {
        // 稽核先於動作：重開機之後這個行程就不在了，動作之後才寫的稽核永遠寫不到。
        _audit.Record(
            "system",
            "task.reboot",
            "maintenance",
            targetType: "scheduled_task",
            targetId: task.Id,
            detail: task.Name);

        var psi = new ProcessStartInfo
        {
            FileName = "shutdown",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add(RebootGraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("HeliVMS scheduled reboot");

        try
        {
            using var proc = Process.Start(psi);
            _tasks.SetLastResult(task.Id, $"requested (+{RebootGraceSeconds}s)");
            Activity?.Invoke(this, $"[task #{task.Id}] reboot requested (+{RebootGraceSeconds}s)");
        }
        catch (Exception ex)
        {
            _tasks.SetLastResult(task.Id, $"shutdown.exe failed: {ex.Message}");
            return;
        }

        RebootRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>定時截圖：對目標頻道逐一單發取影；離線攝影機計入失敗但不中斷其他台。</summary>
    private async Task RunSnapshotAsync(ScheduledTaskRecord task)
    {
        var all = _channels.List();
        var targets = task.ChannelId is int id ? all.Where(c => c.Id == id) : all;
        var dir = Path.Combine(_snapshotsRoot, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);

        var ok = 0;
        var fail = 0;
        foreach (var channel in targets)
        {
            if (string.IsNullOrWhiteSpace(channel.MainStreamUrl))
            {
                fail++;
                continue;
            }

            var file = Path.Combine(dir, $"ch{channel.Id:000}-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            try
            {
                await Task.Run(() => SnapshotGrabber.Grab(
                    channel.MainStreamUrl, file, timeout: SnapshotGrabber.DefaultTimeout));
                ok++;
            }
            catch (Exception)
            {
                fail++;
            }
        }

        var result = $"ok {ok}, fail {fail}";
        _tasks.SetLastResult(task.Id, result);
        _audit.Record(
            "system",
            "task.snapshot",
            "maintenance",
            targetType: "scheduled_task",
            targetId: task.Id,
            detail: result);
        Activity?.Invoke(this, $"[task #{task.Id}] snapshot: {result}");
    }

    /// <summary>重連攝影機：交給主視窗的管理員（工作階段清單在那邊），回來補記成功數。</summary>
    private async Task RunReconnectAsync(ScheduledTaskRecord task)
    {
        var handler = ReconnectHandler;
        if (handler is null)
        {
            _tasks.SetLastResult(task.Id, "no handler");
            return;
        }

        try
        {
            var restarted = await handler();
            var result = $"restarted {restarted}";
            _tasks.SetLastResult(task.Id, result);
            _audit.Record(
                "system",
                "task.reconnect",
                "maintenance",
                targetType: "scheduled_task",
                targetId: task.Id,
                detail: result);
            Activity?.Invoke(this, $"[task #{task.Id}] reconnect: {result}");
        }
        catch (Exception ex)
        {
            _tasks.SetLastResult(task.Id, $"failed: {ex.Message}");
        }
    }
}
