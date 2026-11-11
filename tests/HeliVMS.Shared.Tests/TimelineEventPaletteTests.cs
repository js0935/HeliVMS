using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 時間軸事件刻度的色票（<see cref="TimelineEventPalette"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這一支的價值全在「分組」：11 個事件類別收成 6 色，操作員才背得起來；而分組必須沿用
/// <see cref="EventTaxonomy.CategoryOf"/> 的分類——兩套分類遲早會對同一個事件給出不同答案，
/// 而時間軸上只會看到兩個顏色不一樣的點，沒有任何日誌。
/// </para>
/// </remarks>
public class TimelineEventPaletteTests
{
    [Theory]
    [InlineData("sensor", TimelineEventPalette.Alarm)]
    [InlineData("tripwire", TimelineEventPalette.Alarm)]
    [InlineData("perimeter", TimelineEventPalette.Alarm)]
    [InlineData("io_di1", TimelineEventPalette.Alarm)]
    [InlineData("line_cross", TimelineEventPalette.Alarm)]
    [InlineData("motion", TimelineEventPalette.Motion)]
    [InlineData("smart_motion", TimelineEventPalette.Motion)]
    [InlineData("ai_person", TimelineEventPalette.Motion)]
    [InlineData("shelter", TimelineEventPalette.Shelter)]
    [InlineData("tamper", TimelineEventPalette.Shelter)]
    [InlineData("offline", TimelineEventPalette.Network)]
    [InlineData("disconnected", TimelineEventPalette.Network)]
    [InlineData("online", TimelineEventPalette.Network)]
    [InlineData("manual", TimelineEventPalette.ManualOrSchedule)]
    [InlineData("schedule_start", TimelineEventPalette.ManualOrSchedule)]
    [InlineData("system", TimelineEventPalette.System)]
    [InlineData("something_new", TimelineEventPalette.System)]
    [InlineData(null, TimelineEventPalette.System)]
    [InlineData("", TimelineEventPalette.System)]
    public void 事件型別對應到類別顏色(string? eventType, string expected)
    {
        Assert.Equal(expected, TimelineEventPalette.For(eventType));
    }

    [Fact]
    public void 六個色值互不相同()
    {
        // 兩類共用一色，就等於這兩類在時間軸上分不出來（而分色是唯一的目的一樣）。
        Assert.Equal(TimelineEventPalette.All.Count, TimelineEventPalette.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(6, TimelineEventPalette.All.Count);
    }

    [Fact]
    public void 色值都是合法的十六進位色碼()
    {
        foreach (var hex in TimelineEventPalette.All)
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        }
    }

    [Fact]
    public void 未知分類回系統灰而不是隨便一色()
    {
        // 未知不可以回「警報紅」（會把沒見過的事件變成假警報），也不可以回空白。
        Assert.Equal(TimelineEventPalette.System, TimelineEventPalette.GroupOf("brand_new_category"));
        Assert.NotEqual(TimelineEventPalette.Alarm, TimelineEventPalette.GroupOf(null));
    }

    [Fact]
    public void 分類判斷沿用EventTaxonomy而不是自己再寫一套()
    {
        // 用幾個只有 EventTaxonomy 認得的別名驗證：改了那裡、這裡就跟著改。
        Assert.Equal(EventTaxonomy.Sensor, EventTaxonomy.CategoryOf("io_do2"));
        Assert.Equal(TimelineEventPalette.For("io_do2"), TimelineEventPalette.For("sensor"));

        Assert.Equal(EventTaxonomy.Shelter, EventTaxonomy.CategoryOf("signal_shelter"));
        Assert.Equal(TimelineEventPalette.For("signal_shelter"), TimelineEventPalette.For("shelter"));
    }
}
