using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 播放讀數的時刻格式。現場要求是「秒數後面再加上張數（18:25:32:015）」：
/// 同一秒內有幾十張影格，只到秒的讀數沒辦法指出操作員看到的是哪一張。
/// </summary>
public class PlaybackClockTests
{
    [Fact]
    public void 讀數到毫秒且補零()
    {
        Assert.Equal(
            "18:25:32:015",
            PlaybackClock.FormatInstant(new DateTime(2026, 11, 4, 18, 25, 32, 15)));
    }

    [Fact]
    public void 整秒時毫秒欄位是三個零而不是被省略()
    {
        // 欄位固定三位：少印會讓「18:25:32」看起來像舊格式，抄進報告後無法分辨有沒有毫秒。
        Assert.Equal(
            "09:05:00:000",
            PlaybackClock.FormatInstant(new DateTime(2026, 11, 4, 9, 5, 0, 0)));
    }

    [Fact]
    public void 毫秒不足三位與接近整秒都正確()
    {
        Assert.Equal("23:59:59:007", PlaybackClock.FormatInstant(new DateTime(2026, 11, 4, 23, 59, 59, 7)));
        Assert.Equal("10:00:00:999", PlaybackClock.FormatInstant(new DateTime(2026, 11, 4, 10, 0, 0, 999)));
    }

    [Fact]
    public void 兩位數以下都補零()
    {
        Assert.Equal("06:07:08:009", PlaybackClock.FormatInstant(new DateTime(2026, 11, 4, 6, 7, 8, 9)));
    }
}
