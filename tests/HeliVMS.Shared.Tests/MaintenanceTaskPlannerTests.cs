using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 排程任務的到點判定（§14）。這是「凌晨三點重開機」的唯一守門員：
/// 判得太鬆（早跑、補跑、同日重跑）會讓機器在操作員面前隨機重開機；
/// 判得太緊（永遠不到點）則保養形同虛設，問題要到攝影機集體掉線那天才被發現。
/// </summary>
public class MaintenanceTaskPlannerTests
{
    [Fact]
    public void DEBUG_enum_members()
    {
        var names = Enum.GetNames<MaintenanceTaskKind>();
        Assert.True(names.Length == 4, $"enum members: {string.Join(",", names)}");
    }

    private static readonly DateTime Now = new(2026, 11, 2, 3, 5, 0, DateTimeKind.Local); // 週一 03:05

    [Fact]
    public void 每天任務過了到點時刻且上次執行在它之前就判定到點()
    {
        // 昨天 03:00 跑過，今天 03:00 的時刻已過 → 該跑。
        var last = Now.Date.AddDays(-1).AddHours(3);
        Assert.True(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, 300, 0, 0, Now, last));
    }

    [Fact]
    public void 每天任務還沒到到點時刻不會判定到點()
    {
        var last = Now.Date.AddDays(-1).AddHours(3);
        var before = Now.Date.AddHours(2).AddMinutes(55);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, 300, 0, 0, before, last));
    }

    [Fact]
    public void 同一天已經跑過不會再跑到點()
    {
        // 今天 03:00 已跑，03:05 的輪詢不能再觸發（輪詢每 30 秒一次，沒有這條就是連環重開機）。
        var last = Now.Date.AddHours(3);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, 300, 0, 0, Now, last));
    }

    [Fact]
    public void 程式沒開著而錯過的到點不會在隔天補跑()
    {
        // 每天 03:00；上次執行是上週。今天不是到點後的第一次輪詢嗎——不，
        // 判定只看「今天的到點時刻是否已過且沒跑過」：今天 03:00 沒開機、10:00 才開，
        // 補跑等於大白天隨機重開機，所以錯過就跳過，等明天。
        var nowLate = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Local);
        var last = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Local);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, 300, 0, 0, nowLate, last));
    }

    [Fact]
    public void 每週任務只在排定的星期判定到點()
    {
        var lastWeek = Now.Date.AddDays(-7).AddHours(3);
        // 只排週一（bit1）；今天是週一 → 到點。
        Assert.True(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Weekly, 300, 0b0000_0010, 0, Now, lastWeek));
        // 同樣的任務，改成今天只排週三（bit3）→ 不跑。
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Weekly, 300, 0b0000_1000, 0, Now, lastWeek));
    }

    [Fact]
    public void 每週任務在同一天跑過之後不會重複到點()
    {
        var last = Now.Date.AddHours(3);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Weekly, 300, 0b0000_0010, 0, Now, last));
    }

    [Fact]
    public void 每週任務錯過上一個排定日不會補跑()
    {
        // 只排週一；上週一機器沒開（lastRun 是兩週前）。今天週一 03:05 不補跑，
        // 否則又是「開機後自己重開機」的戲碼；等下週一。
        var now = new DateTime(2026, 11, 2, 3, 5, 0, DateTimeKind.Local); // 週一
        var twoWeeksAgo = now.Date.AddDays(-14).AddHours(3);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Weekly, 300, 0b0000_0010, 0, now, twoWeeksAgo));
    }

    [Fact]
    public void 每隔任務滿了間隔分鐘數才判定到點()
    {
        var last = Now.AddMinutes(-30);
        Assert.False(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Interval, 0, 0, 30, Now.AddMinutes(-29), last));
        Assert.True(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Interval, 0, 0, 30, Now, last));
    }

    [Fact]
    public void 從未執行的每隔任務視為立刻到點()
    {
        Assert.True(MaintenanceTaskPlanner.IsDue(
            MaintenanceTaskKind.Snapshot, MaintenanceScheduleKind.Interval, 0, 0, 30, Now, null));
    }

    [Fact]
    public void 星期遮罩與DayOfWeek序數一致且能判斷成員()
    {
        Assert.Equal(1, MaintenanceWeekdays.MaskFor(DayOfWeek.Sunday));
        Assert.Equal(0b0100_0000, MaintenanceWeekdays.MaskFor(DayOfWeek.Saturday));
        Assert.True(MaintenanceWeekdays.Has(MaintenanceWeekdays.All, DayOfWeek.Wednesday));
        Assert.False(MaintenanceWeekdays.Has(0b0000_0010, DayOfWeek.Wednesday));
    }

    [Fact]
    public void 到點時刻是今天的本地日期加上HHMM()
    {
        var at = MaintenanceTaskPlanner.AtToday(330, Now);
        Assert.Equal(Now.Date.AddHours(3).AddMinutes(30), at);
    }
}
