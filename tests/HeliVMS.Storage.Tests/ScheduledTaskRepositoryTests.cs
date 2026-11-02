using HeliVMS.Shared;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 排程任務倉儲（§14）。除了 CRUD，這裡釘住兩條安全規則：
/// 建立即寫 last_run（否則「15:00 建一顆每天 03:00 重開機」會在建立當下判定到點，立刻重開機）、
/// 重開機不能用每隔週期（每隔 N 分鐘重開機不是保養，是災難）。
/// </summary>
public class ScheduledTaskRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly ScheduledTaskRepository _repo;

    public ScheduledTaskRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-tasks-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new ScheduledTaskRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void 新增任務會立刻寫入last_run而不是留空()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        var id = _repo.Add("夜間重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 300);
        var after = DateTime.UtcNow.AddSeconds(5);

        var task = Assert.Single(_repo.List());
        Assert.Equal(id, task.Id);
        Assert.NotNull(task.LastRunUtc);
        Assert.InRange(task.LastRunUtc!.Value, before, after);
    }

    [Fact]
    public void 任務欄位往返完整()
    {
        var id = _repo.Add(
            "每小時截圖",
            MaintenanceTaskKind.Snapshot,
            MaintenanceScheduleKind.Weekly,
            timeOfDay: 930,
            weekdaysMask: 0b0111_1100, // 一到五
            intervalMinutes: 0,
            channelId: 7,
            enabled: false);

        var task = Assert.Single(_repo.List());
        Assert.Equal(id, task.Id);
        Assert.Equal("每小時截圖", task.Name);
        Assert.Equal(MaintenanceTaskKind.Snapshot, task.Kind);
        Assert.Equal(MaintenanceScheduleKind.Weekly, task.ScheduleKind);
        Assert.Equal(930, task.TimeOfDay);
        Assert.Equal(0b0111_1100, task.WeekdaysMask);
        Assert.Equal(7, task.ChannelId);
        Assert.False(task.Enabled);
    }

    [Fact]
    public void ListEnabled只回啟用中的任務()
    {
        var on = _repo.Add("啟用中", MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Daily, timeOfDay: 800);
        _repo.Add("已停用", MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Daily, timeOfDay: 900, enabled: false);

        var rows = _repo.ListEnabled();

        var task = Assert.Single(rows);
        Assert.Equal(on, task.Id);
    }

    [Fact]
    public void 執行前先記last_run執行後再補結果()
    {
        var id = _repo.Add("重連", MaintenanceTaskKind.ReconnectChannels, MaintenanceScheduleKind.Daily, timeOfDay: 400);
        var startedAt = new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);

        Assert.True(_repo.MarkRunStarted(id, startedAt));
        Assert.Equal(startedAt, Assert.Single(_repo.List()).LastRunUtc);

        Assert.True(_repo.SetLastResult(id, "restarted 8"));
        var task = Assert.Single(_repo.List());
        Assert.Equal(startedAt, task.LastRunUtc);
        Assert.Equal("restarted 8", task.LastResult);
    }

    [Fact]
    public void SetEnabled與Delete生效()
    {
        var id = _repo.Add("暫時", MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Daily, timeOfDay: 800);

        _repo.SetEnabled(id, false);
        Assert.Empty(_repo.ListEnabled());

        _repo.Delete(id);
        Assert.Empty(_repo.List());
    }

    [Fact]
    public void 重開機不能用每隔週期()
    {
        Assert.ThrowsAny<ArgumentException>(() => _repo.Add(
            "每三十分鐘重開機",
            MaintenanceTaskKind.Reboot,
            MaintenanceScheduleKind.Interval,
            intervalMinutes: 30));
    }

    [Fact]
    public void 時刻必須是合法的HHMM()
    {
        Assert.ThrowsAny<ArgumentException>(() => _repo.Add(
            "不存在的時刻", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 2461));
        Assert.ThrowsAny<ArgumentException>(() => _repo.Add(
            "非法時刻", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 77));
    }

    [Fact]
    public void 每週任務至少要選一天()
    {
        Assert.ThrowsAny<ArgumentException>(() => _repo.Add(
            "沒選星期", MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Weekly, timeOfDay: 800, weekdaysMask: 0));
    }
}
