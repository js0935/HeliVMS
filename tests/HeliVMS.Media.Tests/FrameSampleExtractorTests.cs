using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 抽樣幀管線（以圖搜圖的取樣端）：參數形狀是純函式測試，實跑用 lavfi 合成來源。
/// </summary>
/// <remarks>
/// 這一層壞掉的症狀全是「搜得到但內容不對」：<c>-ss</c> 放到 <c>-i</c> 之後
/// （每次都從檔案頭解碼，一天的回放檔等到天荒地老）、樣本尺寸不是 1024、
/// 或查詢圖與掃描樣本用了不同的縮放濾鏡（幾何不同 → 指紋對不上 →
/// 「明明有錄到卻搜不到」）。
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class FrameSampleExtractorTests
{
    [Fact]
    public void 抽樣參數用input_seek且間隔與尺寸都固定()
    {
        var args = FrameSampleExtractor.BuildSampleArguments(
            @"C:\rec\seg.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), 15, 10);

        var ssIndex = args.IndexOf("-ss");
        var iIndex = args.IndexOf("-i");
        Assert.True(ssIndex >= 0 && iIndex >= 0 && ssIndex < iIndex, "-ss 必須在 -i 之前（input seek）");
        Assert.Equal("5", args[ssIndex + 1]);
        Assert.Equal(@"C:\rec\seg.mp4", args[iIndex + 1]);
        Assert.Equal("60", args[args.IndexOf("-t") + 1]);
        Assert.Equal(
            $"fps=1/15,{FrameSampleExtractor.ScaleFilter}",
            args[args.IndexOf("-vf") + 1]);
        Assert.Equal("rawvideo", args[args.IndexOf("-f") + 1]);
        Assert.Equal("gray", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("10", args[args.IndexOf("-frames:v") + 1]);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void 單幀與抽樣共用同一條縮放濾鏡()
    {
        // 查詢圖與掃描樣本幾何不一致 = 指紋對不上（「有錄到卻搜不到」）。這條斷言把
        // 「兩邊共用 ScaleFilter 常數」釘住——任何一邊自己寫一份就會紅。
        var single = FrameSampleExtractor.BuildSingleFrameArguments("query.jpg");
        var sample = FrameSampleExtractor.BuildSampleArguments("in.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), 5, 3);

        Assert.Contains(FrameSampleExtractor.ScaleFilter, single[single.IndexOf("-vf") + 1]);
        Assert.EndsWith(FrameSampleExtractor.ScaleFilter, sample[sample.IndexOf("-vf") + 1]);
        Assert.Equal("-", single[^1]);
    }

    [Fact]
    public void 不合法參數會擋下來()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameSampleExtractor.BuildSampleArguments("in.mp4", TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10), 5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameSampleExtractor.BuildSampleArguments("in.mp4", TimeSpan.Zero, TimeSpan.Zero, 5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameSampleExtractor.BuildSampleArguments("in.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameSampleExtractor.BuildSampleArguments("in.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), 5, 0));
        Assert.Throws<ArgumentException>(() =>
            FrameSampleExtractor.BuildSingleFrameArguments(""));
    }

    private static string MakeSourceVideo(string path, int seconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-y", "-f", "lavfi",
                     "-i", $"testsrc=duration={seconds}:size=320x240:rate=10",
                     "-c:v", "mpeg4", "-q:v", "5",
                     path,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        Assert.True(proc.WaitForExit(30000), "合成來源逾時");
        Assert.True(File.Exists(path), "合成來源沒有產出：" + path);
        return path;
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [SkippableFact]
    public void 對合成影片等間隔抽樣且偏移是間隔倍數()
    {
        Skip.If(FfmpegInstall.Help(FfmpegLocator.Ffmpeg) is null, "PATH 上沒有可啟動的 ffmpeg。");

        var dir = MakeTempDir();
        try
        {
            var source = MakeSourceVideo(Path.Combine(dir, "src.mp4"), seconds: 20);
            var samples = new List<GraySample>();
            var count = FrameSampleExtractor.ExtractSamples(
                source,
                from: TimeSpan.FromSeconds(4),
                span: TimeSpan.FromSeconds(16),
                intervalSeconds: 5,
                maxSamples: 10,
                samples.Add,
                timeout: TimeSpan.FromSeconds(60));

            // 16 秒、每 5 秒一個樣本：t=4,9,14 → 3 個（第四個 19 落在 span 之外）。
            Assert.Equal(3, count);
            Assert.Equal(3, samples.Count);
            Assert.All(samples, s => Assert.Equal(FrameSampleExtractor.SampleBytes, s.Gray32x32.Length));
            Assert.Equal(TimeSpan.FromSeconds(4), samples[0].Offset);
            Assert.Equal(TimeSpan.FromSeconds(9), samples[1].Offset);
            Assert.Equal(TimeSpan.FromSeconds(14), samples[2].Offset);

            // testsrc 是動態訊號：兩個不同時刻的樣本指紋不該全都一樣。
            var hashes = samples
                .Select(s => PerceptualHash.FromGrayImage(s.Gray32x32, FrameSampleExtractor.SampleSize, FrameSampleExtractor.SampleSize))
                .Distinct()
                .Count();
            Assert.True(hashes >= 2, "動態來源的不同樣本得到了相同指紋——抽樣可能每次都拿到同一幀");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void 對影片抽單幀得到有內容的灰階()
    {
        Skip.If(FfmpegInstall.Help(FfmpegLocator.Ffmpeg) is null, "PATH 上沒有可啟動的 ffmpeg。");

        var dir = MakeTempDir();
        try
        {
            var source = MakeSourceVideo(Path.Combine(dir, "src.mp4"), seconds: 5);
            var gray = FrameSampleExtractor.ExtractSingleFrame(source, timeout: TimeSpan.FromSeconds(30));

            Assert.Equal(FrameSampleExtractor.SampleBytes, gray.Length);
            Assert.Contains(gray, b => b > 0); // testsrc 不是全黑
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void 已取消的請求在啟動後很快回來()
    {
        Skip.If(FfmpegInstall.Help(FfmpegLocator.Ffmpeg) is null, "PATH 上沒有可啟動的 ffmpeg。");

        var dir = MakeTempDir();
        try
        {
            var source = MakeSourceVideo(Path.Combine(dir, "src.mp4"), seconds: 20);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var started = DateTime.UtcNow;
            Assert.ThrowsAny<OperationCanceledException>(() => FrameSampleExtractor.ExtractSamples(
                source,
                from: TimeSpan.Zero,
                span: TimeSpan.FromSeconds(20),
                intervalSeconds: 0.5,
                maxSamples: 64,
                _ => { },
                cancellationToken: cts.Token));
            Assert.True((DateTime.UtcNow - started).TotalSeconds < 10, "取消沒有讓抽樣及時結束");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void 不存在的檔案會拋出而不是回報零樣本()
    {
        Skip.If(FfmpegInstall.Help(FfmpegLocator.Ffmpeg) is null, "PATH 上沒有可啟動的 ffmpeg。");

        var dir = MakeTempDir();
        try
        {
            var missing = Path.Combine(dir, "never.mp4");
            Assert.Throws<InvalidOperationException>(() => FrameSampleExtractor.ExtractSamples(
                missing,
                from: TimeSpan.Zero,
                span: TimeSpan.FromSeconds(10),
                intervalSeconds: 5,
                maxSamples: 3,
                _ => { },
                timeout: TimeSpan.FromSeconds(20)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
