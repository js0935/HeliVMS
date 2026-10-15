using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監控中心佈局記憶（<see cref="MonitorLayoutPrefs"/>）的行為契約。
/// </summary>
/// <remarks>
/// 這裡鎖的是升級與切頁簽才會遇到的狀況：舊版只有單一格數、每頁簽互不干擾、以及損毀的偏好檔
/// 不能讓主視窗開不起來。桌面端沒有 WPF 執行期測試，這些純函式是唯一能守住的地方。
/// </remarks>
public sealed class MonitorLayoutPrefsTests
{
    [Fact]
    public void 舊格式的單一格數視為全部頁簽()
    {
        // 升級前的 ui.json：只有一個 cells 整數，代表「全部」頁簽的格數。
        var prefs = MonitorLayoutPrefs.Parse("""{ "layout": 2, "cells": 4 }""");

        Assert.Equal(MonitorLayoutPrefs.AllTab, prefs.ActiveTab);
        Assert.Null(prefs.ActiveGroupId);
        Assert.Equal(4, prefs.CellsFor(null));
    }

    [Fact]
    public void 新格式可分辨各頁簽的格數與停留頁簽()
    {
        var prefs = MonitorLayoutPrefs.Parse(
            """{ "layout": 5, "activeTab": "7", "cells": { "all": 16, "7": 4 } }""");

        Assert.Equal(7, prefs.ActiveGroupId);
        Assert.Equal(16, prefs.CellsFor(null));
        Assert.Equal(4, prefs.CellsFor(7));
    }

    [Fact]
    public void 未記錄的頁簽回傳空值()
    {
        var prefs = MonitorLayoutPrefs.Parse("""{ "activeTab": "all", "cells": { "all": 9 } }""");

        Assert.Null(prefs.CellsFor(3));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void 損毀或非物件的內容退回預設(string json)
    {
        var prefs = MonitorLayoutPrefs.Parse(json);

        Assert.Equal(MonitorLayoutPrefs.AllTab, prefs.ActiveTab);
        Assert.Null(prefs.ActiveGroupId);
        Assert.Empty(prefs.Cells);
    }

    [Fact]
    public void 序列化後再解析可還原各頁簽格數()
    {
        var prefs = MonitorLayoutPrefs.Parse("""{ "layout": 0, "cells": { "all": 12 } }""");
        prefs.Remember(3, 6);
        prefs.ActiveTab = MonitorLayoutPrefs.TabKey(3);

        var again = MonitorLayoutPrefs.Parse(prefs.ToJson(layoutIndex: 4));

        Assert.Equal(3, again.ActiveGroupId);
        Assert.Equal(12, again.CellsFor(null));
        Assert.Equal(6, again.CellsFor(3));
        Assert.Equal(4, again.LayoutIndex);
    }

    [Fact]
    public void 記住非正數格數會被忽略()
    {
        // 寫進 0 格會讓該頁簽的監看牆一片空白，比沿用上一頁簽還糟。
        var prefs = new MonitorLayoutPrefs();

        prefs.Remember(3, 0);

        Assert.Null(prefs.CellsFor(3));
    }

    [Fact]
    public void 全部頁簽的鍵固定為all()
    {
        Assert.Equal("all", MonitorLayoutPrefs.TabKey(null));
        Assert.Equal("5", MonitorLayoutPrefs.TabKey(5));
    }
}
