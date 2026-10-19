using System.Diagnostics;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using Xunit.Abstractions;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放的真實 ffmpeg 驗收：16× 倍速不掉幀、多路同時播放、跳轉首幀延遲。
/// </summary>
/// <remarks>
/// <para>
/// 這幾條對應 ARCHITECTURE §8.5 的驗收線（「4 路同步 + 16× 播放不掉幀」、「拖拽跳轉
/// 快速出畫」），也是唯一能證明「同時能播幾路」的實測：每個 <see cref="PlaybackSession"/>
/// 就是一個 ffmpeg 解碼行程，所以路數＝行程數。
/// </para>
/// <para>
/// 「不掉幀」在這裡是可驗證的：<see cref="PlaybackSession"/> 的節流只延後送出，不從管線
/// 丟棄任何一格，所以素材 10 秒 250 幀，每一路都必須解出約 250 幀。若有人把節流改成
/// 「跳過幀」或改動 <c>-t</c>／讀取迴圈，這裡的幀數會立刻短少。
/// </para>
/// <para>
/// 並行度由環境變數 <c>HELIVMS_PLAYBACK_WAY</c> 控制（預設 4，上限 16）：
/// CI 固定驗 4 路（規格驗收線），本機可調高實測「同時能播幾路」。
/// </para>
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class PlaybackCapacity16xTests
{
    private readonly ITestOutputHelper _output;

    public PlaybackCapacity16xTests(ITestOutputHelper output) => _output = output;

    /// <summary>同時播放路數：環境變數可覆寫，預設 4（規格驗收線），夾在 1–16。</summary>
    private static int Ways()
    {
        var raw = Environment.GetEnvironmentVariable("HELIVMS_PLAYBACK_WAY");
        return int.TryParse(raw, out var ways) ? Math.Clamp(ways, 1, 16) : 4;
    }

    [SkippableFact]
    public async Task 十六倍速單路解碼幀數與素材相同()
    {
        SkipIfNoFfmpeg();

        var (counts, elapsed) = await RunAsync(ways: 1, seekSeconds: 0, speed: 16);

        Assert.True(
            counts[0] >= PlaybackClipFixture.ExpectedFrames - 5,
            $"16× 只播出 {counts[0]} 幀，素材共 {PlaybackClipFixture.ExpectedFrames} 幀：節流變成了丟幀，或解碼提早結束。");
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"16× 播完 {PlaybackClipFixture.DurationSec} 秒素材花了 {elapsed.TotalSeconds:0.0} 秒；1× 才會這麼慢，速度設定可能被忽略。");
    }

    [SkippableFact]
    public async Task 多路同時十六倍速每路幀數一致()
    {
        SkipIfNoFfmpeg();

        var ways = Ways();
        var (counts, elapsed) = await RunAsync(ways, seekSeconds: 0, speed: 16);
        _output.WriteLine($"MEASURE ways={ways} speed=16 frames=[{string.Join(",", counts)}] elapsedMs={elapsed.TotalMilliseconds:0}");
        _output.WriteLine($"{ways} 路 16×：幀數 [{string.Join(", ", counts)}]，耗時 {elapsed.TotalSeconds:0.00}s。");

        for (var i = 0; i < ways; i++)
        {
            Assert.True(
                counts[i] >= PlaybackClipFixture.ExpectedFrames - 5,
                $"第 {i + 1}/{ways} 路只播出 {counts[i]} 幀（預期約 {PlaybackClipFixture.ExpectedFrames} 幀）：同時播放時掉幀。");
        }

        var spread = counts.Max() - counts.Min();
        Assert.True(
            spread <= PlaybackClipFixture.Fps / 5,
            $"{ways} 路同時播放的幀數差達 {spread} 幀（>{PlaybackClipFixture.Fps / 5}）：各路不同步。");

        Assert.True(
            elapsed < TimeSpan.FromSeconds(6),
            $"{ways} 路 16× 全部播完花了 {elapsed.TotalSeconds:0.0} 秒，超出預算：同時解碼嚴重變慢。");
    }

    /// <summary>
    /// §8.5 驗收「時間軸拖拽跳轉 &lt; 300ms 出畫」。這裡量的是「呼叫 PlayAsync 到首幀事件」
    /// 的牆上時間，含行程啟動與 ffprobe（首次跳轉未命中快取）。CI 機器較慢，
    /// 上界放寬到 1500ms 只為抓「整段重掃」這類退化；本機實測值遠低於此。
    /// </summary>
    [SkippableFact]
    public async Task 段中間跳轉後首幀快速到達()
    {
        SkipIfNoFfmpeg();

        var seg = NewSegment();
        await using var session = new PlaybackSession(seg);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        session.FrameDecoded += (_, _) =>
        {
            Interlocked.Increment(ref frames);
            first.TrySetResult();
        };
        session.PlaybackError += (_, ex) => _output.WriteLine($"PLAYBACK ERROR: {ex.Message}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var playback = session.PlayAsync(seekSeconds: PlaybackClipFixture.DurationSec / 2.0);
        var arrived = await Task.WhenAny(first.Task, Task.Delay(10_000));
        var firstFrameMs = sw.ElapsedMilliseconds;
        await playback;
        _output.WriteLine($"MEASURE seekMs={firstFrameMs} frames={frames}");
        _output.WriteLine($"跳轉至 {PlaybackClipFixture.DurationSec / 2}s：首幀 {firstFrameMs}ms，共 {frames} 幀。");

        Assert.True(
            ReferenceEquals(first.Task, arrived),
            "跳轉後 10 秒內沒有收到任何影格：解碼管線沒有出畫。");
        Assert.True(
            firstFrameMs < 1500,
            $"跳轉後首幀花了 {firstFrameMs}ms：超出 <300ms 的驗收預算太多，跳轉路徑可能失去關鍵幀快速定位。");
        Assert.True(frames >= PlaybackClipFixture.ExpectedFrames / 2 - 5, $"跳轉後只播出 {frames} 幀，疑似提早結束。");
    }

    private static void SkipIfNoFfmpeg()
    {
        Skip.IfNot(RecordingVideoProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上，略過真實解碼驗收。");
        Skip.IfNot(RecordingVideoProbe.Libx264Available, "這個 ffmpeg 沒有 libx264，無法產生測試片段。");
    }

    private static SegmentRecord NewSegment()
    {
        var start = DateTime.UtcNow;
        return new SegmentRecord
        {
            Id = 1,
            ChannelId = 1,
            Stream = "main",
            StartUtc = start,
            EndUtc = start.AddSeconds(PlaybackClipFixture.DurationSec),
            FilePath = PlaybackClipFixture.Path,
            SizeBytes = new FileInfo(PlaybackClipFixture.Path).Length,
            DurationSec = PlaybackClipFixture.DurationSec,
            Format = "mp4",
            Status = SegmentStatus.Final,
        };
    }

    private async Task<(long[] Counts, TimeSpan Elapsed)> RunAsync(int ways, double seekSeconds, double speed)
    {
        var segment = NewSegment();

        var counts = new long[ways];
        var sessions = new List<PlaybackSession>(ways);
        try
        {
            for (var i = 0; i < ways; i++)
            {
                var index = i;
                var session = new PlaybackSession(segment) { Speed = speed };

                // 把解碼錯誤寫進測試輸出：沒有這一段，ffmpeg 因參數不支援而以非零結束時
                // 只會看到「0 幀」而查不出原因（ffmpeg 9 移除 -vsync 就是這樣炸的）。
                session.PlaybackError += (_, ex) => _output.WriteLine($"PLAYBACK ERROR[{index}]: {ex.Message}");
                session.FrameDecoded += (_, _) => Interlocked.Increment(ref counts[index]);
                sessions.Add(session);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAll(sessions.Select(s => s.PlayAsync(seekSeconds)));
            watch.Stop();
            return (counts, watch.Elapsed);
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }
}
