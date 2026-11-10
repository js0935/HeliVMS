using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 即時監看診斷行的格式。現場只說得出「畫面會延遲」，這行字是事後唯一能分辨
/// 「攝影機／網路慢」「本機管線積影格」「UI 佇列積影格」的證據，欄位少一個就等於沒有。
/// </summary>
public class LiveViewDiagnosticLineTests
{
    private static LiveViewDiagnosticLine.CellSample Cell(
        int channelId = 103,
        double shown = 14.8,
        double read = 29.9,
        double emitted = 14.9,
        double dropped = 15.0,
        TimeSpan? backlog = null,
        TimeSpan? age = null,
        string state = "Streaming",
        int width = 640,
        int height = 360,
        string stream = "sub",
        double cap = 0,
        string? hw = null)
        => new(
            channelId,
            stream,
            width,
            height,
            shown,
            read,
            emitted,
            dropped,
            backlog,
            age ?? TimeSpan.FromMilliseconds(45),
            state,
            cap,
            hw);

    [Fact]
    public void 每個欄位都在同一行裡()
    {
        var line = LiveViewDiagnosticLine.Format(
            new DateTime(2026, 11, 5, 8, 12, 34, 567, DateTimeKind.Utc),
            [Cell(backlog: TimeSpan.FromMilliseconds(780), cap: 15, hw: "d3d11va")]);

        Assert.Equal(
            "2026-11-05T08:12:34.567Z | cells=1 | ch103 sub 640x360 hw=d3d11va shown=14.8/s read=29.9/s emit=14.9/s"
            + " drop=15/s cap=15/s backlog=780ms age=45ms Streaming",
            line);
    }

    /// <summary>
    /// 硬解一直寫（軟解寫 <c>hw=off</c>）：現場「明明設了硬解，CPU 還是滿載」時，
    /// 這一欄是唯一能分辨「政策沒啟用（例如原生尺寸或冷卻期）」與「真的用了硬解卻還是慢」的線索。
    /// </summary>
    [Fact]
    public void 實際生效的硬體解碼會寫在同一行裡()
    {
        Assert.Contains("hw=d3d11va", LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell(hw: "d3d11va")]));

        // 軟解不省略整欄：省略與「舊版沒有這一欄」在日誌上長得一樣。
        Assert.Contains("hw=off", LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell()]));
    }

    /// <summary>
    /// 上限也要寫出來：`read=30/s drop=0/s cap=15` 這種自相矛盾的組合，唯一線索就是
    /// 「上限寫著 15 卻什麼都沒丟」——日誌裡有這一欄，才抓得到「上限沒生效」。
    /// </summary>
    [Fact]
    public void 顯示上限會寫在同一行裡()
    {
        var withCap = LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell(cap: 15)]);
        Assert.Contains("cap=15/s", withCap);

        // 不限流（0）時不寫，免得與「上限 0」混淆。
        var unlimited = LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell(cap: 0)]);
        Assert.DoesNotContain("cap=", unlimited);
    }

    [Fact]
    public void 沒有落後值時整欄不寫()
    {
        // 進度回報還沒來（或中斷）時不可以寫 0ms：那讀起來像「完全沒有落後」，
        // 而實情是「不知道」。診斷日誌撒謊比沒有日誌更糟。
        var line = LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell()]);

        Assert.DoesNotContain("backlog=", line);
        Assert.Contains("age=45ms", line);
    }

    [Fact]
    public void 停滯的格也會被寫出來()
    {
        // 卡住的那一格最需要被記錄：fps 0、狀態不是 Streaming、年齡很大。
        var line = LiveViewDiagnosticLine.Format(
            DateTime.UnixEpoch,
            [
                Cell(channelId: 105, shown: 0, read: 0, emitted: 0, dropped: 0,
                    age: TimeSpan.FromSeconds(12.3), state: "Reconnecting"),
            ]);

        Assert.Contains("ch105 sub 640x360", line);
        Assert.Contains("shown=0/s", line);
        Assert.Contains("age=12300ms", line);
        Assert.Contains("Reconnecting", line);
    }

    [Fact]
    public void 主碼流與多格一起寫()
    {
        var line = LiveViewDiagnosticLine.Format(
            DateTime.UnixEpoch,
            [Cell(channelId: 103, stream: "main", width: 1920, height: 1080), Cell(channelId: 104)]);

        Assert.Contains("cells=2", line);
        Assert.Contains("ch103 main 1920x1080", line);
        Assert.Contains("ch104 sub 640x360", line);
    }

    [Fact]
    public void 沒有影格時不寫尺寸()
    {
        // 還沒出過畫的連線（解析度未知）不該寫 0x0：那看起來像一個真的尺寸。
        // 但 hw 仍要寫——「連線中，軟解」正是這一格當下的解碼方式。
        var line = LiveViewDiagnosticLine.Format(DateTime.UnixEpoch, [Cell(width: 0, height: 0)]);

        Assert.Contains("ch103 sub hw=off shown=", line);
        Assert.DoesNotContain("0x0", line);
    }
}
