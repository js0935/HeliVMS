using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 匯出範圍的裁切。現場症狀：匯出精靈設 11:04:07.25～11:04:07.30（5 秒），拿到的檔案卻有 50 分鐘
/// ——先是不合法的時刻被默默當成一整天（見 <c>ExportWindow</c> 與 <c>ClockTime</c>），
/// 再來是匯出本身從不裁切、整段整段地併。這裡釘住第二半：**要多少就只給多少**。
/// </summary>
public class ExportTrimTests
{
    private static SegmentRecord Segment(string file, DateTime startUtc, double? durationSeconds, DateTime? endUtc = null)
        => new()
        {
            Id = 1,
            ChannelId = 1,
            Stream = "main",
            FilePath = file,
            StartUtc = startUtc,
            EndUtc = endUtc ?? (durationSeconds is { } d ? startUtc.AddSeconds(d) : null),
            SizeBytes = 1024,
            DurationSec = durationSeconds,
            Sha256 = "x",
            Status = SegmentStatus.Final,
        };

    private static readonly DateTime Base = new(2026, 11, 4, 11, 4, 0, DateTimeKind.Utc);

    [Fact]
    public void 範圍落在段落中間時前後都要裁()
    {
        // 一段 11:04:00–11:05:00，要 11:04:07.25–11:04:12.30。
        var segments = new[] { Segment(@"C:\rec\seg-110400000.mp4", Base, 60) };

        var content = ExportService.BuildConcatContent(
            segments,
            Base.AddSeconds(7.25),
            Base.AddSeconds(12.30));

        Assert.Contains("inpoint 7.25", content);
        Assert.Contains("outpoint 12.3", content);
    }

    [Fact]
    public void 整段都在範圍內時不做多餘裁切()
    {
        var segments = new[] { Segment(@"C:\rec\seg-110400000.mp4", Base, 60) };

        var content = ExportService.BuildConcatContent(
            segments,
            Base.AddSeconds(-30),
            Base.AddSeconds(120));

        // 沒有 inpoint／outpoint：整段就是要的內容，叫 ffmpeg 精準 seek 只是白花時間。
        Assert.DoesNotContain("inpoint", content);
        Assert.DoesNotContain("outpoint", content);
    }

    [Fact]
    public void 多段之間有空隙時每段各自裁()
    {
        // 11:04:00–11:05:00（有錄）、11:10:00–11:11:00（中間 11:05–11:10 沒錄）。
        // 整體 -ss/-t 會把缺口算進去，逐段裁才精準。
        var segments = new[]
        {
            Segment(@"C:\rec\a.mp4", Base, 60),
            Segment(@"C:\rec\b.mp4", Base.AddMinutes(6), 60),
        };

        var content = ExportService.BuildConcatContent(
            segments,
            Base.AddSeconds(30),
            Base.AddMinutes(6).AddSeconds(30));

        // 第一段只裁頭（11:04:30 才開始要，尾端整段都要），第二段只裁尾（11:10:30 之後不要）。
        Assert.Contains("inpoint 30", content);
        Assert.Contains("outpoint 30", content);
        // 回報長度是兩段交集的和，不是兩整段。
        var seconds = ExportService.TrimmedDurationSeconds(
            segments,
            Base.AddSeconds(30),
            Base.AddMinutes(6).AddSeconds(30));
        Assert.Equal(60, seconds, 3);
    }

    [Fact]
    public void 末段沒有結束時間時仍裁到要求的結束點()
    {
        // 正在錄的那一段：索引沒有結束時間，但檔案內容比索引知道的長；
        // 用 inpoint + outpoint（相對檔案起點）仍然精準，比檔案短則停在 EOF。
        var segments = new[] { Segment(@"C:\rec\live.mp4", Base, durationSeconds: null, endUtc: null) };

        var content = ExportService.BuildConcatContent(
            segments,
            Base.AddSeconds(5),
            Base.AddSeconds(9));

        Assert.Contains("inpoint 5", content);
        Assert.Contains("outpoint 9", content);
        Assert.Equal(4, ExportService.TrimmedDurationSeconds(segments, Base.AddSeconds(5), Base.AddSeconds(9)), 3);
    }

    [Fact]
    public void 回報長度是要的範圍而不是整段長度()
    {
        var segments = new[]
        {
            Segment(@"C:\rec\a.mp4", Base, 60),
            Segment(@"C:\rec\b.mp4", Base.AddSeconds(60), 60),
        };

        // 5 秒的要求：舊版回報 120 秒（兩整段），收據也照抄，等於把 5 秒的證據標成 2 分鐘。
        var seconds = ExportService.TrimmedDurationSeconds(
            segments,
            Base.AddSeconds(7.25),
            Base.AddSeconds(12.30));

        Assert.Equal(5.05, seconds, 3);
    }

    /// <summary>
    /// 匯出流程真的把「要求的範圍」交給裁切（而不是整段）。裁切本身在上面幾支測過了，
    /// 這裡釘的是接線：兩個純函式若被餵 DateTime.MinValue/MaxValue 或整段長度總和，
    /// 上面那些測試照樣全綠，成品卻還是一整天。
    /// </summary>
    /// <remarks>
    /// 這條是原始碼契約（匯出真的跑 ffmpeg，單元測試不開行程）；比對整句呼叫，
    /// 才不會被同名的參數或註解滿足。
    /// </remarks>
    [Fact]
    public void 匯出流程把要求範圍交給裁切()
    {
        var source = File.ReadAllText(RepoRelative("src/HeliVMS.Recording/ExportService.cs"));

        Assert.Contains("BuildConcatContent(segments, request.StartUtc, request.EndUtc)", source);
        Assert.Contains("TrimmedDurationSeconds(segments, request.StartUtc, request.EndUtc)", source);
        // 回報與收據都要用裁切後的長度，不得回到整段長度總和。
        Assert.DoesNotContain("segments.Sum(s => s.DurationSec ?? 0)", source);
    }

    private static string RepoRelative(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄"),
            relativePath);
    }

    [Fact]
    public void 路徑含單引號仍可安全跳脫()
    {
        var segments = new[] { Segment(@"C:\rec\cam's\a.mp4", Base, 10) };

        var content = ExportService.BuildConcatContent(segments, Base, Base.AddSeconds(10));

        Assert.Contains("file 'C:\\rec\\cam'\\''s\\a.mp4'", content);
    }
}
