using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 媒體檔自己的長度。索引的段長一律取這個數字（牆鐘時間只是後備）：
/// 現場實測 68 段有 15 段與檔案實際長度差超過 60 秒，最壞的兩段記著 6853／6849 秒，
/// 檔案其實只有 250／260 秒——Windows 睡眠與校時把行程的牆鐘時間拉長了。
/// </summary>
public class MediaDurationProbeTests
{
    [Theory]
    [InlineData("45.303220", 45.303220)]
    [InlineData("  45.303220\r\n", 45.303220)]
    [InlineData("0.5", 0.5)]
    public void 讀得懂ffprobe的單行長度(string output, double expected)
    {
        Assert.Equal(expected, MediaDurationProbe.Parse(output)!.Value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("garbage")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("inf")]
    [InlineData("NaN")]
    public void 讀不懂或明顯不合理的長度回空值(string? output)
    {
        // 回 null 是「請呼叫端用牆鐘時間」的訊號，不是錯誤：ffprobe 在精簡安裝裡可能不在，
        // 檔案也可能正被防毒鎖住，而這條路徑在錄影收尾的關鍵時刻執行。
        Assert.Null(MediaDurationProbe.Parse(output));
    }

    [Fact]
    public void 超過一天的長度視為讀錯檔案()
    {
        Assert.Null(MediaDurationProbe.Parse(
            (RecordedSegmentLength.MaxPlausibleSeconds + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void 檔案不存在時回空值而不是擲例外()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"helivms-missing-{Guid.NewGuid():N}.mp4");

        Assert.Null(MediaDurationProbe.ProbeSeconds(missing));
    }

    /// <summary>
    /// 端到端：錄影輸出用的正是 <c>frag_keyframe+empty_moov</c>（fMP4），
    /// 而 fMP4 的 <c>mvhd</c> 長度是 0——長度寫在檔案尾端的 fragment 裡。
    /// 這一條就是在確認「探測真的探得到」，否則整個段長修正會在正式環境靜默無效。
    /// </summary>
    [SkippableFact]
    public void 錄影用的fMP4檔仍探測得到長度()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過真實探測。");

        var file = Path.Combine(Path.GetTempPath(), $"helivms-duration-{Guid.NewGuid():N}.mp4");
        try
        {
            GenerateFragmentedClip(file, seconds: 5);

            var probed = MediaDurationProbe.ProbeSeconds(file);

            Assert.NotNull(probed);
            // 容器長度含最後一格的顯示時間，所以會略大於 5 秒（實測 5.0x）。
            Assert.InRange(probed!.Value, 4.9, 6.0);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void GenerateFragmentedClip(string file, int seconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=size=160x120:rate=10:duration={seconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "10",
            // 與 SegmentRecorder 完全相同的輸出設定：這才是探測要面對的真實容器。
            "-movflags", "frag_keyframe+empty_moov+default_base_moof+faststart",
            "-y", file,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg");

        // 同時抽乾兩條管線（管線填滿會讓 ffmpeg 卡住不結束）。
        var stderr = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("產生測試片段逾時。");
        }

        Skip.If(proc.ExitCode != 0 || !File.Exists(file),
            $"產生測試片段失敗（結束碼 {proc.ExitCode}）：{stderr.GetAwaiter().GetResult()}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理容錯：殘檔不該讓回合失敗。
        }
    }
}
