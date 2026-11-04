using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 匯出範圍的裁切。現場症狀：匯出精靈設 11:04:07.25～11:04:07.30（5 秒），拿到的檔案卻有 50 分鐘
/// ——先是不合法的時刻被默默當成一整天（見 <c>ExportWindow</c> 與 <c>ClockTime</c>），
/// 再來是匯出本身從不裁切、整段整段地併。這裡釘住第二半：**要多少就只給多少**。
///
/// <para>
/// 範圍是用輸入端的 <c>-ss</c>／<c>-t</c> 套的，不是 concat demuxer 的 <c>inpoint</c>：
/// 實測（真實錄影檔）<c>inpoint</c> 在這個 ffmpeg 版本不生效、<c>outpoint</c> 才有，
/// 所以單元測試若只比對字串，會出現「測試全綠、匯出仍是整段」的假象。
/// </para>
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
    public void concat清單只有檔案範圍另外套()
    {
        var segments = new[]
        {
            Segment(@"C:\rec\a.mp4", Base, 60),
            Segment(@"C:\rec\b.mp4", Base.AddSeconds(60), 60),
        };

        var content = ExportService.BuildConcatContent(segments);

        Assert.Contains("file 'C:\\rec\\a.mp4'", content);
        Assert.Contains("file 'C:\\rec\\b.mp4'", content);
        // 範圍不放進清單：inpoint 在這個 ffmpeg 版本不生效（實測），寫了只會讓人以為裁過了。
        Assert.DoesNotContain("inpoint", content);
        Assert.DoesNotContain("outpoint", content);
    }

    [Fact]
    public void 起點落在第一段中間時要跳過前面那段()
    {
        var segments = new[] { Segment(@"C:\rec\a.mp4", Base, 60) };

        var skip = ExportService.TrimStartSeconds(segments, Base.AddSeconds(7.25));

        Assert.Equal(7.25, skip, 3);
    }

    [Fact]
    public void 起點在第一段之前或缺口裡時不跳過()
    {
        var segments = new[] { Segment(@"C:\rec\a.mp4", Base, 60) };

        // 範圍比錄影更早開始（或起點正好落在缺口）＝第一段整段都要，不需 seek。
        Assert.Equal(0, ExportService.TrimStartSeconds(segments, Base.AddSeconds(-30)), 3);
        Assert.Equal(0, ExportService.TrimStartSeconds(segments, Base), 3);
        Assert.Equal(0, ExportService.TrimStartSeconds([], Base), 3);
    }

    [Fact]
    public void 差距小於一個畫格時不做多餘seek()
    {
        var segments = new[] { Segment(@"C:\rec\a.mp4", Base, 60) };

        // 20fps 一畫格 50ms：比這更小的差距不叫 ffmpeg seek（省一次精準 seek 的成本）。
        Assert.Equal(0, ExportService.TrimStartSeconds(segments, Base.AddMilliseconds(30)), 3);
    }

    [Fact]
    public void 回報長度是各段交集的總和而不是整段()
    {
        // 11:04:00–11:05:00（有錄）、11:10:00–11:11:00（中間 11:05–11:10 沒錄）。
        // 缺口不會被算進長度：-t 用的是「實際內容的秒數」。
        var segments = new[]
        {
            Segment(@"C:\rec\a.mp4", Base, 60),
            Segment(@"C:\rec\b.mp4", Base.AddMinutes(6), 60),
        };

        var seconds = ExportService.TrimmedDurationSeconds(
            segments,
            Base.AddSeconds(30),
            Base.AddMinutes(6).AddSeconds(30));

        Assert.Equal(60, seconds, 3);
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

    [Fact]
    public void 末段沒有結束時間時仍裁到要求的結束點()
    {
        // 正在錄的那一段：索引沒有結束時間。交集到要求結束點為止，-t 讓 ffmpeg 停在該處；
        // 檔案比要求短時自然停在 EOF，兩種情況都不會多收別段的畫面。
        var segments = new[] { Segment(@"C:\rec\live.mp4", Base, durationSeconds: null, endUtc: null) };

        Assert.Equal(4, ExportService.TrimmedDurationSeconds(segments, Base.AddSeconds(5), Base.AddSeconds(9)), 3);
        Assert.Equal(5, ExportService.TrimStartSeconds(segments, Base.AddSeconds(5)), 3);
    }

    /// <summary>
    /// 匯出流程真的把「要求的範圍」交給裁切，而且 <c>-ss</c> 在 <c>-i</c> 之前。
    /// </summary>
    /// <remarks>
    /// 擺在 <c>-i</c> 之後就是解碼後才跳（成本不同）而且參數意義會跟著 filter 圖走；
    /// 裁切函式本身有行為測試，但「ExportAsync 餵什麼給它」沒有測試碰得到（匯出真的跑 ffmpeg）。
    /// 這條是原始碼契約，比對整句呼叫才不會被同名參數或註解滿足。
    /// </remarks>
    [Fact]
    public void 匯出流程把要求範圍交給裁切且ss在i之前()
    {
        var source = File.ReadAllText(RepoRelative("src/HeliVMS.Recording/ExportService.cs"));

        Assert.Contains("TrimStartSeconds(segments, request.StartUtc)", source);
        Assert.Contains("TrimmedDurationSeconds(segments, request.StartUtc, request.EndUtc)", source);
        Assert.Contains("BuildConcatContent(segments)", source);

        // 條件本身也要在：只比對參數行的話，把條件寫成永遠不成立也照樣通過。
        Assert.Contains("if (trimStart > TrimEpsilonSeconds)", source);

        var ssIndex = source.IndexOf("psi.ArgumentList.Add(\"-ss\")", StringComparison.Ordinal);
        var inputIndex = source.IndexOf("psi.ArgumentList.Add(concatFile)", StringComparison.Ordinal);
        Assert.True(ssIndex > 0 && inputIndex > ssIndex, "-ss 必須在 -i 之前（否則 seek 的是輸出而不是輸入）");
        Assert.Contains("psi.ArgumentList.Add(\"-t\")", source);
        // 回報與收據都要用裁切後的長度，不得回到整段長度總和。
        Assert.DoesNotContain("segments.Sum(s => s.DurationSec ?? 0)", source);
    }

    [Fact]
    public void 路徑含單引號仍可安全跳脫()
    {
        var segments = new[] { Segment(@"C:\rec\cam's\a.mp4", Base, 10) };

        var content = ExportService.BuildConcatContent(segments);

        Assert.Contains("file 'C:\\rec\\cam'\\''s\\a.mp4'", content);
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
}
