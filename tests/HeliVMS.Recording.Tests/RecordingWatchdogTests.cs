using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影看門狗（M320，§11.1）。
///
/// <para>
/// 看門狗存在的理由是「錄影停了卻沒人知道」。這裡測兩件事：判定本身（新鮮／過期／從未啟動），
/// 以及告警的節制——每分鐘一輪，若每輪都寫稽核或每輪都重啟，一台真的掛掉的機器會把稽核灌滿、
/// 把平靜的告警變成開機風暴，反而更難看出故障。故驗證「只在狀態轉折時動作一次」。
/// </para>
/// </summary>
public sealed class RecordingWatchdogTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public RecordingWatchdogTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"helivms-wd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-wd-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
    }

    /// <summary>心跳在容許時距內就是健康——錄影行程正常運作不該產生任何告警。</summary>
    [Fact]
    public void 心跳新鮮時判定為健康()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        RecordingHeartbeat.Write(_dataRoot, now.AddSeconds(-10), 3, 100);

        var verdict = RecordingWatchdog.Evaluate(_dataRoot, now);

        Assert.True(verdict.IsHealthy);
    }

    /// <summary>心跳超過時距仍未更新就是停擺——行程卡住或已崩潰。</summary>
    [Fact]
    public void 心跳過期時判定為停擺()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        RecordingHeartbeat.Write(_dataRoot, now.AddMinutes(-5), 3, 100);

        var verdict = RecordingWatchdog.Evaluate(_dataRoot, now);

        Assert.Equal(RecordingHeartbeatStatus.Stale, verdict.Status);
        Assert.False(verdict.IsHealthy);
    }

    /// <summary>沒有任何心跳檔就是「從未啟動」，要與「曾啟動後停住」區分開。</summary>
    [Fact]
    public void 沒有心跳檔時判定為缺少()
    {
        var verdict = RecordingWatchdog.Evaluate(_dataRoot, DateTime.UtcNow);

        Assert.Equal(RecordingHeartbeatStatus.Missing, verdict.Status);
    }

    /// <summary>健康時不寫任何稽核——正常運作不產生事件。</summary>
    [Fact]
    public void 健康時不寫稽核()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        RecordingHeartbeat.Write(_dataRoot, now, 1, 1);
        var monitor = new RecordingWatchdogMonitor(_dataRoot, _audit);

        monitor.Evaluate(now);

        Assert.Equal(0, AlarmCount());
        Assert.Equal(0, RecoveredCount());
    }

    /// <summary>停擺第一次被發現時寫一筆稽核。</summary>
    [Fact]
    public void 停擺時寫下一筆稽核()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var monitor = new RecordingWatchdogMonitor(_dataRoot, _audit);

        monitor.Evaluate(now);

        Assert.Equal(1, AlarmCount());
        Assert.True(monitor.IsDown);
    }

    /// <summary>停擺持續期間不重複寫稽核——否則一台掛掉的機器會每分鐘灌一筆，把故障淹掉。</summary>
    [Fact]
    public void 停擺持續期間不重複寫稽核()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var monitor = new RecordingWatchdogMonitor(_dataRoot, _audit);

        monitor.Evaluate(now);
        monitor.Evaluate(now.AddMinutes(1));
        monitor.Evaluate(now.AddMinutes(2));

        Assert.Equal(1, AlarmCount());
    }

    /// <summary>恢復心跳時寫一筆復原稽核，讓「已恢復」留下時間點。</summary>
    [Fact]
    public void 恢復時寫下一筆復原稽核()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var monitor = new RecordingWatchdogMonitor(_dataRoot, _audit);
        monitor.Evaluate(now);

        RecordingHeartbeat.Write(_dataRoot, now.AddMinutes(1), 2, 5);
        monitor.Evaluate(now.AddMinutes(1));

        Assert.Equal(1, RecoveredCount());
        Assert.False(monitor.IsDown);
    }

    /// <summary>由停擺轉折時呼叫重啟，且只呼叫一次——否則會變成不斷重複啟動。</summary>
    [Fact]
    public void 停擺轉折時只重啟一次()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var launched = new List<string>();
        var monitor = new RecordingWatchdogMonitor(
            _dataRoot, _audit, restartExe: () => @"C:\HeliVMS\HeliVMS.App.exe", launch: launched.Add);

        monitor.Evaluate(now);
        monitor.Evaluate(now.AddMinutes(1));
        monitor.Evaluate(now.AddMinutes(2));

        Assert.Equal(new[] { @"C:\HeliVMS\HeliVMS.App.exe" }, launched);
    }

    /// <summary>未設定重啟路徑時只告警，不去拉起任何行程（很多部署靠 SCM／登入自啟恢復）。</summary>
    [Fact]
    public void 未設定重啟路徑時不重啟()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var launched = new List<string>();
        var monitor = new RecordingWatchdogMonitor(_dataRoot, _audit, launch: launched.Add);

        monitor.Evaluate(now);

        Assert.Empty(launched);
        Assert.Equal(1, AlarmCount());
    }

    private int AlarmCount() =>
        _audit.Count(new AuditLogQuery { Action = RecordingWatchdogMonitor.AuditAction });

    private int RecoveredCount() =>
        _audit.Count(new AuditLogQuery { Action = RecordingWatchdogMonitor.RecoveredAction });

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
