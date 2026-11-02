using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 單發取影（§14 排程截圖）對真的 ffmpeg 跑一遍：參數被接受、檔案真的生出來。
/// 來源用 lavfi 合成訊號，不依賴攝影機；PATH 上沒有 ffmpeg 時依設計略過。
/// </summary>
[Collection(MediaIoCollection.Name)]
public sealed class SnapshotGrabberTests
{
    private static string MakeSourceVideo(string path)
    {
        // 1 秒 320x240 測試訊號；截圖只需要「有畫面可抓」。
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("testsrc=duration=1:size=320x240:rate=10");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");
        proc.WaitForExit(15000);
        Assert.True(File.Exists(path), "合成來源沒有產出：" + path);
        return path;
    }

    [SkippableFact]
    public void 對影片來源能抓出一張JPEG()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過取影的實跑驗證。");

        var dir = Path.Combine(Path.GetTempPath(), $"helivms-grab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = MakeSourceVideo(Path.Combine(dir, "src.mp4"));
            var target = Path.Combine(dir, "shot.jpg");

            SnapshotGrabber.Grab(source, target, timeout: TimeSpan.FromSeconds(20));

            Assert.True(File.Exists(target), "取影完成但沒有輸出檔");
            Assert.True(new FileInfo(target).Length > 0, "輸出檔是空的");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void 抓不到的來源會在逾時內丟例外而不是掛住()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過取影逾時的實跑驗證。");

        var dir = Path.Combine(Path.GetTempPath(), $"helivms-grab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // 不存在的檔案：ffmpeg 立刻失敗（非零結束）→ 例外；重點是「會回來」，不會卡死排程。
            var target = Path.Combine(dir, "never.jpg");
            var started = DateTime.UtcNow;
            Assert.ThrowsAny<Exception>(() => SnapshotGrabber.Grab(
                Path.Combine(dir, "missing.mp4"), target, timeout: TimeSpan.FromSeconds(10)));
            Assert.True((DateTime.UtcNow - started).TotalSeconds < 20, "取影沒有在逾時內返回");
            Assert.False(File.Exists(target), "失敗的取影不該留下輸出檔");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
