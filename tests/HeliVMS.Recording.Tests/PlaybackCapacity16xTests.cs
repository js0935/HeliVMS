using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using Xunit;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 16× 倍速與多路同時播放的整合性驗收（僅在有 ffmpeg 時執行）。
/// </summary>
/// <remarks>
/// 依 AGENTS.md 的實作模式：碰到真實 ffmpeg 行程的測試使用 <see cref="SkippableFactAttribute"/>
/// 並透過環境偵測略過（此處沿用類似邏輯：直接檢查 Process 啟動是否可能失敗則略過）。
/// 目的：驗證「4 路同步 + 16× 播放不掉幀」的驗收線（§8.5），不改變解碼邏輯，只驗證節流行為。
/// </remarks>
public sealed class PlaybackCapacity16xTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helivms-cap-" + Guid.NewGuid().ToString("N"));
    private bool _disposed;

    public PlaybackCapacity16xTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 測試資源清理容錯
        }

        _disposed = true;
    }

    [Xunit.SkippableFact]
    public async Task 四路同步十六倍速可持續解碼不提前結束()
    {
        var ffmpeg = HeliVMS.Shared.FfmpegLocator.Ffmpeg;
        Skip.IfNot(File.Exists(ffmpeg) || ffmpeg.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase), "找不到 ffmpeg，略過真實解碼測試。");

        var file = Path.Combine(_dir, "testsrc_10s.mp4");
        var psiGen = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psiGen.ArgumentList.Add("-hide_banner");
        psiGen.ArgumentList.Add("-f");
        psiGen.ArgumentList.Add("lavfi");
        psiGen.ArgumentList.Add("-i");
        psiGen.ArgumentList.Add("testsrc=duration=10:size=320x180:rate=25");
        psiGen.ArgumentList.Add("-c:v");
        psiGen.ArgumentList.Add("libx264");
        psiGen.ArgumentList.Add("-preset");
        psiGen.ArgumentList.Add("ultrafast");
        psiGen.ArgumentList.Add("-tune");
        psiGen.ArgumentList.Add("zerolatency");
        psiGen.ArgumentList.Add("-pix_fmt");
        psiGen.ArgumentList.Add("yuv420p");
        psiGen.ArgumentList.Add("-movflags");
        psiGen.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof");
        psiGen.ArgumentList.Add("-y");
        psiGen.ArgumentList.Add(file);

        using (var p = System.Diagnostics.Process.Start(psiGen))
        {
            Skip.If(p is null, "無法啟動 ffmpeg 產生測試片段。");
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(); } catch { }
                Skip.True(false, "產生測試片段逾時。");
            }

            Skip.If(p.ExitCode != 0, "產生測試片段失敗。");
        }

        Skip.IfNot(File.Exists(file), "測試片段未建立。");

        var seg = new SegmentRecord
        {
            Id = 1,
            ChannelId = 1,
            Stream = "main",
            StartUtc = DateTime.UtcNow,
            EndUtc = DateTime.UtcNow.AddSeconds(10),
            FilePath = file,
            SizeBytes = new FileInfo(file).Length,
            DurationSec = 10,
            Format = "mp4",
            Status = SegmentStatus.Final,
        };

        var maxWays = Environment.GetEnvironmentVariable("HELIVMS_PLAYBACK_WAY");
        if (!int.TryParse(maxWays, out var ways))
        {
            ways = 4;
        }

        ways = Math.Clamp(ways, 1, 16);

        var sessions = new List<PlaybackSession>(ways);
        var framesPerSession = new long[ways];
        var endedPerSession = new bool[ways];

        try
        {
            for (var i = 0; i < ways; i++)
            {
                var s = new PlaybackSession(seg);
                var idx = i;
                s.Speed = 16.0;
                s.FrameDecoded += (o, e) => framesPerSession[idx]++;
                s.Ended += (o, e) => endedPerSession[idx] = true;
                sessions.Add(s);
            }

            var tasks = sessions.Select(s => s.PlayAsync(0, CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);

            for (var i = 0; i < ways; i++)
            {
                Assert.True(endedPerSession[i], $"第 {i + 1} 路未收到 Ended 事件。");
                Assert.True(framesPerSession[i] > 0, $"第 {i + 1} 路未解碼任何幀。");
            }

            var first = framesPerSession[0];
            for (var i = 1; i < ways; i++)
            {
                Assert.True(framesPerSession[i] > first * 0.8, $"第 {i + 1} 路解碼幀數（{framesPerSession[i]}）與首路（{first}）差距過大，疑似掉幀或提前結束。");
                Assert.True(framesPerSession[i] < first * 1.2, $"第 {i + 1} 路解碼幀數（{framesPerSession[i]}）偏高，節流行為可能改變。");
            }
        }
        finally
        {
            foreach (var s in sessions)
            {
                await s.DisposeAsync();
            }
        }
    }
}
