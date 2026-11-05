using System.Diagnostics;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放的「時間基準」：每一格的內容時刻必須來自檔案自己的時間戳。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報「多路回放各播各的」的根因：舊算法假設每一格恰好間隔 1/名目幀率 秒，於是
/// 送格速率低於名目的相機每一格都比實際早到——一台 1.2×、另一台 2.3×，各台以不同速率
/// 前進，OSD 的時刻也跟著失真（實測名目 15fps 實際 12.5fps、名目 30fps 實際 13fps）。
/// </para>
/// <para>
/// 這條測試刻意做一個「相機斷流 3 秒」的檔案：時間戳法會回報 12.9 秒的內容跨度與一個
/// 3 秒的跳躍，名目幀率法只會回報 10 秒且間距固定 100ms。斷言因此同時守住節奏與時刻。
/// </para>
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class PlaybackTimestampBaseTests
{
    [SkippableFact]
    public async Task 回放時刻取自檔案時間戳而不是影格序號除以名目幀率()
    {
        Skip.IfNot(RecordingVideoProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上，略過真實解碼驗收。");
        Skip.IfNot(RecordingVideoProbe.Libx264Available, "這個 ffmpeg 沒有 libx264，無法產生測試片段。");

        var file = GenerateGappedClip();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var segment = new SegmentRecord
            {
                Id = 1,
                ChannelId = 1,
                Stream = "main",
                StartUtc = start,
                EndUtc = start.AddSeconds(13),
                FilePath = file,
                SizeBytes = new FileInfo(file).Length,
                DurationSec = 13,
                Format = "mp4",
                Status = SegmentStatus.Final,
            };

            // 名目 10fps（探測說的）／實際 7.7fps（10 秒 100 格攤在 13 秒裡）。
            var info = new StreamProbeInfo(160, 120, "h264", null, 10);
            await using var session = new PlaybackSession(segment, probe: _ => info)
            {
                Speed = 32,
            };

            var frames = new List<VideoFrame>();
            session.FrameDecoded += (_, frame) => frames.Add(frame);

            await session.PlayAsync();

            Assert.Equal(100, frames.Count);
            Assert.True(
                frames[^1].PtsMs > 12_500,
                $"最後一格的 PtsMs 是 {frames[^1].PtsMs}ms；空隙被壓掉或時刻仍以名目幀率推算（預期 >12500ms）。");

            var gaps = frames.Zip(frames.Skip(1), (a, b) => b.PtsMs - a.PtsMs);
            Assert.True(gaps.Max() > 2_500, $"相鄰影格最大間距只有 {gaps.Max()}ms，3 秒的斷流沒有反映在時刻上。");
            Assert.All(gaps, delta => Assert.True(delta >= 0, $"時刻倒退：{delta}ms"));
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>產生一個中間有 3 秒空窗的 10fps 片段（與現場相機斷流的形狀相同）。</summary>
    private static string GenerateGappedClip()
    {
        var file = Path.Combine(Path.GetTempPath(), $"helivms-pts-{Guid.NewGuid():N}.mp4");
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=160x120:r=10:d=10",
            "-vf", "setpts='PTS+if(gte(T,5),3/TB,0)'",
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-y", file,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg");

        // 兩條管線都要抽乾：管線填滿會讓 ffmpeg 卡住不結束（與錄影路徑同一個坑）。
        var stderr = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("產生帶空隙的測試片段逾時。");
        }

        Assert.True(proc.ExitCode == 0 && File.Exists(file), $"產生測試片段失敗：{stderr.GetAwaiter().GetResult()}");
        return file;
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
