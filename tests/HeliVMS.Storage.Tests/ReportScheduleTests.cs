using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 報表排程判斷（§14.1 #9）。排程語意是「本地日曆日／週的固定時刻」，不是固定間隔；
/// 這裡把邊界（未到時刻、同日重複、跨日、跨週）逐一釘住，避免計時器輪詢造成重複寄送或漏寄。
/// </summary>
public sealed class ReportScheduleTests
{
    private static readonly TimeOnly At8 = new(8, 0);

    [Fact]
    public void 未到設定時刻不寄送()
    {
        var now = new DateTime(2026, 3, 2, 7, 59, 0); // 週一
        Assert.False(ReportSchedule.IsDue(now, null, ReportCadence.Daily, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 每日到達時刻且當日尚未寄送時應寄送()
    {
        var now = new DateTime(2026, 3, 2, 8, 0, 0);
        Assert.True(ReportSchedule.IsDue(now, null, ReportCadence.Daily, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 每日同日已寄送不再重複()
    {
        var now = new DateTime(2026, 3, 2, 9, 0, 0);
        var last = new DateTime(2026, 3, 2, 8, 0, 30);
        Assert.False(ReportSchedule.IsDue(now, last, ReportCadence.Daily, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 每日隔日到達時刻後再次寄送()
    {
        var now = new DateTime(2026, 3, 3, 8, 0, 0);
        var last = new DateTime(2026, 3, 2, 8, 0, 30);
        Assert.True(ReportSchedule.IsDue(now, last, ReportCadence.Daily, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 每週同一週已寄送不重複且跨週才寄()
    {
        // 2026-03-02 為週一。
        var monday = new DateTime(2026, 3, 2, 8, 0, 0);
        Assert.True(ReportSchedule.IsDue(monday, null, ReportCadence.Weekly, At8, DayOfWeek.Monday));

        var wednesday = new DateTime(2026, 3, 4, 8, 0, 0);
        Assert.False(ReportSchedule.IsDue(wednesday, monday, ReportCadence.Weekly, At8, DayOfWeek.Monday));

        var nextMonday = new DateTime(2026, 3, 9, 8, 0, 0);
        Assert.True(ReportSchedule.IsDue(nextMonday, monday, ReportCadence.Weekly, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 關閉週期永不寄送()
    {
        var now = new DateTime(2026, 3, 2, 9, 0, 0);
        Assert.False(ReportSchedule.IsDue(now, null, ReportCadence.Off, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 下次每日寄送時間未到時刻時為今日()
    {
        var now = new DateTime(2026, 3, 2, 7, 0, 0);
        Assert.Equal(
            new DateTime(2026, 3, 2, 8, 0, 0),
            ReportSchedule.NextDueLocal(now, ReportCadence.Daily, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 下次每週寄送時間對齊設定星期()
    {
        var now = new DateTime(2026, 3, 2, 9, 0, 0); // 週一且已過 08:00
        Assert.Equal(
            new DateTime(2026, 3, 9, 8, 0, 0),
            ReportSchedule.NextDueLocal(now, ReportCadence.Weekly, At8, DayOfWeek.Monday));
    }

    [Fact]
    public void 週期與時間字串解析具容錯()
    {
        Assert.Equal(ReportCadence.Daily, ReportSchedule.ParseCadence("daily"));
        Assert.Equal(ReportCadence.Weekly, ReportSchedule.ParseCadence("WEEKLY"));
        Assert.Equal(ReportCadence.Off, ReportSchedule.ParseCadence("nonsense"));
        Assert.Equal("weekly", ReportSchedule.ToKey(ReportCadence.Weekly));

        Assert.True(ReportSchedule.TryParseTime("08:00", out var at));
        Assert.Equal(At8, at);
        Assert.False(ReportSchedule.TryParseTime("25:99", out _));
        Assert.True(ReportSchedule.TryParseDay("Monday", out var day));
        Assert.Equal(DayOfWeek.Monday, day);
        Assert.False(ReportSchedule.TryParseDay("Someday", out _));

        Assert.Equal(1, ReportSchedule.WindowDays(ReportCadence.Daily));
        Assert.Equal(7, ReportSchedule.WindowDays(ReportCadence.Weekly));
    }
}
