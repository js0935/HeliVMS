using HeliVMS.Shared;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 排程任務的「編輯」（Update）。除了欄位覆寫，這裡釘住一條安全規則：
/// <b>Update 不得重置 last_run_utc</b>——把「每天 03:00 重開機」改名或改時刻時，
/// 執行狀態要原樣保留；重置了就等於「編輯＝今天已跑歸零」，下一輪輪詢又判定到點，
/// 已經跑過的重開機任務會再重開機一次。
/// </summary>
public class ScheduledTaskUpdateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly ScheduledTaskRepository _repo;

    public ScheduledTaskUpdateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-taskupd-{Guid.NewGuid():N}.db");
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
    public void 編輯任務會覆寫內容但保留last_run()
    {
        var id = _repo.Add("夜間重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 300);
        var original = Assert.Single(_repo.List());

        // 模擬「已經跑過」：先記一次執行。
        var ranAt = new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
        _repo.MarkRunStarted(id, ranAt);
        _repo.SetLastResult(id, "requested (+90s)");

        // 編輯：改名＋改時刻。
        Assert.True(_repo.Update(id, "保養重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 400));

        var task = Assert.Single(_repo.List());
        Assert.Equal("保養重開機", task.Name);
        Assert.Equal(400, task.TimeOfDay);
        Assert.Equal(ranAt, task.LastRunUtc);
        Assert.Equal("requested (+90s)", task.LastResult);
        Assert.NotEqual(original.Name, task.Name);
    }

    [Fact]
    public void 編輯不存在的任務回傳false()
    {
        Assert.False(_repo.Update(9999, "幽靈", MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Daily, timeOfDay: 800));
    }

    [Fact]
    public void 編輯仍會擋下重開機加每隔週期的組合()
    {
        var id = _repo.Add("重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 300);

        Assert.ThrowsAny<ArgumentException>(() => _repo.Update(
            id, "重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Interval, intervalMinutes: 30));
    }
}
