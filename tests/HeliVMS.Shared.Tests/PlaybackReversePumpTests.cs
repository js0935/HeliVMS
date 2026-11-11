using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 倒轉播放的節奏與落點（<see cref="PlaybackReversePump"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 倒轉最容易錯的不是「退多少」，而是<b>退到哪裡</b>：段與段之間有白天的空隙、當日第一段之前
/// 什麼都沒有。錯了畫面看起來完全正常，只是倒著倒著跳到別的時間或繞回當天最後一段——
/// 操作員只會覺得「倒轉壞了」。所以這裡把三種處境（段內、跨空隙、到開頭）逐一釘住。
/// </para>
/// </remarks>
public class PlaybackReversePumpTests
{
    private static readonly DateTime Base = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    private static SegmentRecord Segment(long id, DateTime startUtc, double lengthSeconds)
        => new()
        {
            Id = id,
            ChannelId = 1,
            Stream = "main",
            StartUtc = startUtc,
            EndUtc = startUtc.AddSeconds(lengthSeconds),
            FilePath = $@"D:\rec\ch01\seg-{id}.mp4",
            SizeBytes = 1,
            DurationSec = lengthSeconds,
        };

    [Fact]
    public void 段內倒退一步落在同一段且時間往回()
    {
        var segments = new[] { Segment(1, Base, 60) };
        var playhead = Base.AddSeconds(30);

        var step = PlaybackReversePump.Next(segments, playhead, speed: 1);

        Assert.NotNull(step);
        Assert.Equal(1, step!.Value.Landing.Segment.Id);
        Assert.Equal(29.75, step.Value.Landing.OffsetSeconds, precision: 3);
        Assert.False(step.Value.ReachedStart);
    }

    [Fact]
    public void 速度倍率決定單步倒退量且夾在允許範圍()
    {
        Assert.Equal(0.25, PlaybackReversePump.StepSeconds(1), precision: 3);
        Assert.Equal(0.5, PlaybackReversePump.StepSeconds(2), precision: 3);
        Assert.Equal(1.0, PlaybackReversePump.StepSeconds(4), precision: 3);
        Assert.Equal(4.0, PlaybackReversePump.StepSeconds(16), precision: 3);

        // 非法或離譜的值退回 1×（不是 0——0 會讓倒轉停在原地，看起來像壞掉）。
        Assert.Equal(0.25, PlaybackReversePump.StepSeconds(0), precision: 3);
        Assert.Equal(0.25, PlaybackReversePump.StepSeconds(double.NaN), precision: 3);
        Assert.Equal(0.25, PlaybackReversePump.StepSeconds(-4), precision: 3);

        // 0.5× 也夾到最小步長：再小就小於影格間距，倒轉會停在原地（看起來像壞掉）。
        Assert.Equal(0.25, PlaybackReversePump.StepSeconds(0.5), precision: 3);
    }

    [Fact]
    public void 目標落在空隙時回到前一段的最後一格()
    {
        // 08:00:00–08:00:30 有錄，08:00:30–08:01:00 斷訊，08:01:00 之後又有錄。
        var segments = new[]
        {
            Segment(1, Base, 30),
            Segment(2, Base.AddSeconds(60), 30),
        };
        var playhead = Base.AddSeconds(62);   // 在第二段

        var step = PlaybackReversePump.Next(segments, playhead, speed: 16);

        // 一步退 4 秒（16×）→ 目標 08:00:58 落在空隙 → 回第一段的最後一格。
        Assert.NotNull(step);
        Assert.Equal(1, step!.Value.Landing.Segment.Id);
        Assert.Equal(PlaybackSeekPlanner.LastFrameOf(segments[0]), step.Value.Landing.OffsetSeconds, precision: 3);
        Assert.False(step.Value.ReachedStart);
    }

    [Fact]
    public void 退到第一段開頭時標記為已到開頭且不會繞回最後一段()
    {
        var segments = new[]
        {
            Segment(1, Base, 30),
            Segment(2, Base.AddSeconds(600), 30),
        };

        // 在第一段的第 1 秒按倒轉（1× 退 0.25 秒 → 第 0.75 秒，還沒到開頭）。
        var nearStart = PlaybackReversePump.Next(segments, Base.AddSeconds(1), speed: 1);
        Assert.NotNull(nearStart);
        Assert.Equal(1, nearStart!.Value.Landing.Segment.Id);
        Assert.False(nearStart.Value.ReachedStart);

        // 已經在第 0 秒：目標越過第一段 → 被吸附回第一段開頭，且標記「已到開頭」。
        var atStart = PlaybackReversePump.Next(segments, Base, speed: 1);
        Assert.NotNull(atStart);
        Assert.Equal(1, atStart!.Value.Landing.Segment.Id);
        Assert.Equal(0, atStart.Value.Landing.OffsetSeconds, precision: 3);
        Assert.True(atStart.Value.ReachedStart, "倒轉到開頭就該停住，不能繞回當日最後一段");
    }

    [Fact]
    public void 沒有可播的段時回未知()
    {
        Assert.Null(PlaybackReversePump.Next([], Base, speed: 1));
    }

    [Fact]
    public void 重複倒轉會單調往回且最終停在開頭()
    {
        // 三個段、兩個空隙：倒轉一圈必須一路往回（單調不增），最後停在第一段開頭。
        var segments = new[]
        {
            Segment(1, Base, 20),
            Segment(2, Base.AddSeconds(60), 20),
            Segment(3, Base.AddSeconds(200), 20),
        };

        var playhead = Base.AddSeconds(215);
        var visited = new List<DateTime> { playhead };
        var reachedStart = false;

        for (var i = 0; i < 200 && !reachedStart; i++)
        {
            var step = PlaybackReversePump.Next(segments, playhead, speed: 4);
            Assert.NotNull(step);
            playhead = step!.Value.Landing.Utc;
            visited.Add(playhead);
            reachedStart = step.Value.ReachedStart;
        }

        Assert.True(reachedStart, "倒轉必須能收斂到當日開頭，而不是在片段之間來回跳");
        Assert.Equal(Base, playhead);

        for (var i = 1; i < visited.Count; i++)
        {
            Assert.True(
                visited[i] <= visited[i - 1],
                $"倒轉第 {i} 步往前走了：{visited[i - 1]:HH:mm:ss.fff} → {visited[i]:HH:mm:ss.fff}");
        }
    }
}
