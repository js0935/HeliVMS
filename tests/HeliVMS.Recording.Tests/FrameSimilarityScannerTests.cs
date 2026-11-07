using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 以圖搜圖（QCV8001 QCJpegSearch 對應功能）的行為測試。
/// </summary>
/// <remarks>
/// <para>
/// 整合測試刻意用「同一段 testsrc、其中一段水平鏡像」當兩段錄影：鏡像會把 dHash
/// 的位元幾乎全部翻轉（水平相鄰比較方向相反），所以「命中全部落在未鏡像的那一段」
/// 是強判別——如果改壞成「任何兩張圖都說相似」，命中會同時出現在兩段，當場變紅。
/// </para>
/// <para>
/// 純函式區（<see cref="FrameSimilarityPlanner.IntersectWindow"/>）釘住的是段的
/// 時間語意：<c>start_time</c> 是 ffmpeg 啟動時刻、<c>end_time</c> 可能還沒寫入——
/// 兩者寫錯的症狀都是「搜得到但跳轉到錯的時間」，畫面上分不出來。
/// </para>
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class FrameSimilarityScannerTests
{
    // ---------- 純函式：交集計算 ----------

    [Fact]
    public void 查詢窗大於段時取整段()
    {
        var segStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var segEnd = segStart.AddSeconds(60);

        var window = FrameSimilarityPlanner.IntersectWindow(
            segStart, segEnd, 60, segStart.AddHours(-1), segStart.AddHours(1));

        Assert.NotNull(window);
        Assert.Equal(TimeSpan.Zero, window!.Value.From);
        Assert.Equal(TimeSpan.FromSeconds(60), window.Value.Span);
    }

    [Fact]
    public void 查詢窗切在段中間時偏移與長度都正確()
    {
        var segStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var segEnd = segStart.AddSeconds(60);

        var window = FrameSimilarityPlanner.IntersectWindow(
            segStart, segEnd, 60, segStart.AddSeconds(30), segStart.AddSeconds(45));

        Assert.NotNull(window);
        Assert.Equal(TimeSpan.FromSeconds(30), window!.Value.From);
        Assert.Equal(TimeSpan.FromSeconds(15), window.Value.Span);
    }

    [Fact]
    public void 查詢窗切在段尾巴時只取尾段且半開區間不跨段()
    {
        var segStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var segEnd = segStart.AddSeconds(60);

        var window = FrameSimilarityPlanner.IntersectWindow(
            segStart, segEnd, 60, segStart.AddSeconds(50), segStart.AddHours(1));

        Assert.NotNull(window);
        Assert.Equal(TimeSpan.FromSeconds(50), window!.Value.From);
        Assert.Equal(TimeSpan.FromSeconds(10), window.Value.Span);
    }

    [Fact]
    public void 無交集的段回null()
    {
        var segStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var segEnd = segStart.AddSeconds(60);

        // 查詢窗在段之後。
        Assert.Null(FrameSimilarityPlanner.IntersectWindow(
            segStart, segEnd, 60, segStart.AddHours(1), segStart.AddHours(2)));

        // 查詢窗正好從段結束開始（半開區間——不屬於這一段）。
        Assert.Null(FrameSimilarityPlanner.IntersectWindow(
            segStart, segEnd, 60, segEnd, segEnd.AddMinutes(5)));
    }

    [Fact]
    public void 段結束未知時用時長或查詢上界()
    {
        var segStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

        // end_time 未寫入但有 duration：用 start + duration 當有效結束。
        var byDuration = FrameSimilarityPlanner.IntersectWindow(
            segStart, null, 60, segStart.AddSeconds(20), segStart.AddSeconds(50));
        Assert.NotNull(byDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), byDuration!.Value.From);
        Assert.Equal(TimeSpan.FromSeconds(30), byDuration.Value.Span);

        // 兩者都沒有：查詢上界當結束（保守取到查詢尾）。
        var byQuery = FrameSimilarityPlanner.IntersectWindow(
            segStart, null, null, segStart.AddSeconds(20), segStart.AddSeconds(50));
        Assert.NotNull(byQuery);
        Assert.Equal(TimeSpan.FromSeconds(30), byQuery!.Value.Span);
    }

    // ---------- 整合：真實 ffmpeg ----------

    private static string MakeVideo(string path, int seconds, bool hflip)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-y",
                     "-f", "lavfi",
                     "-i", $"testsrc=duration={seconds}:size=320x240:rate=10",
                 })
        {
            psi.ArgumentList.Add(a);
        }

        if (hflip)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("hflip");
        }

        foreach (var a in new[] { "-c:v", "mpeg4", "-q:v", "5", path })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(60000), "合成來源逾時");
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(File.Exists(path), $"合成來源沒有產出（exit={proc.ExitCode}）：{stderr}");
        return path;
    }

    private static string ExtractFrame(string videoPath, double atSeconds, string jpgPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-y", "-ss", atSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-i", videoPath, "-frames:v", "1", "-q:v", "2", jpgPath,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(30000), "抽查詢幀逾時");
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(File.Exists(jpgPath), $"查詢幀沒有產出（exit={proc.ExitCode}）：{stderr}");
        return jpgPath;
    }

    [SkippableFact]
    public void 以圖搜圖只命中相似內容的那一段()
    {
        Skip.IfNot(RecordingVideoProbe.FfmpegAvailable, "PATH 上沒有可啟動的 ffmpeg。");

        var dir = Path.Combine(Path.GetTempPath(), $"helivms-imgsearch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        SqliteStore? store = null;
        try
        {
            // 兩段「同時長、不同內容」的影片：A 正常、B 水平鏡像。
            var fileA = MakeVideo(Path.Combine(dir, "a.mp4"), 10, hflip: false);
            var fileB = MakeVideo(Path.Combine(dir, "b.mp4"), 10, hflip: true);

            var dbPath = Path.Combine(dir, "index.db");
            store = new SqliteStore(dbPath);
            store.Initialize();
            store.Execute(
                "INSERT INTO channels (id, name, main_rtsp, recording_mode) VALUES (1, 'ch', 'rtsp://x/1', 0);");
            var segments = new SegmentRepository(store);

            var aStart = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
            var bStart = new DateTime(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc);

            void Seed(string file, DateTime start)
            {
                var id = segments.BeginSegment(1, "main", file, start);
                segments.CompleteSegment(id, start.AddSeconds(10), new FileInfo(file).Length, 10, "sha-test");
            }

            Seed(fileA, aStart);
            Seed(fileB, bStart);

            // 查詢幀取自 B 的 4 秒處——掃描樣本的間隔是 2 秒，會落在同一個時刻上。
            var queryPath = ExtractFrame(fileB, 4, Path.Combine(dir, "query.jpg"));
            var queryHash = FrameSimilarityScanner.HashImage(queryPath);

            var scanner = new FrameSimilarityScanner(segments);
            var hits = scanner.Scan(new FrameSimilarityQuery(
                1, "main", aStart.AddMinutes(-5), bStart.AddSeconds(30), queryHash)
            {
                Options = new FrameSimilarityOptions
                {
                    SampleInterval = TimeSpan.FromSeconds(2),
                    MaxHammingDistance = 10,
                    MaxHits = 100,
                },
            });

            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.InRange(h.OccurredAtUtc, bStart, bStart.AddSeconds(10)));
            Assert.True(hits[0].Distance <= 10, $"最近命中距離 {hits[0].Distance} 超出門檻");
        }
        finally
        {
            store?.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
