using HeliVMS.Alarms;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class MotionDetectionTests
{
    private static VideoFrame SolidFrame(int width, int height, byte value)
    {
        var px = new byte[width * height * 3];
        Array.Fill(px, value);
        return new VideoFrame
        {
            Width = width,
            Height = height,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    [Fact]
    public void StaticFrame_ReportsNoMotion()
    {
        var d = new FrameDifferenceMotionDetector(0.5);
        var a = SolidFrame(160, 120, 80);

        var first = d.Update(a);   // 預熱
        var second = d.Update(a);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void ChangedFrame_ReportsMotionAboveSensitivity()
    {
        var d = new FrameDifferenceMotionDetector(0.5);
        var a = SolidFrame(160, 120, 80);

        d.Update(a);                              // 預熱
        var ratio = d.Update(SolidFrame(160, 120, 150));   // 全域亮度 +70

        Assert.True(d.IsMotion(ratio));
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void PartialChange_LowerRatioButDetectable()
    {
        var d = new FrameDifferenceMotionDetector(0.1);
        var a = SolidFrame(160, 120, 80);
        d.Update(a);

        var b = SolidFrame(160, 120, 80);
        Array.Fill(b.Pixels, (byte)150, 0, b.Pixels.Length / 2);   // 上半變動
        var ratio = d.Update(b);

        Assert.True(d.IsMotion(ratio));
        Assert.True(ratio is < 1.0 and > 0);
    }

    [Fact]
    public void Reset_RewarmsWithoutJudgement()
    {
        var d = new FrameDifferenceMotionDetector(0.5);
        d.Update(SolidFrame(160, 120, 80));
        Assert.Equal(1.0, d.Update(SolidFrame(160, 120, 150)));

        d.Reset();
        Assert.Equal(0, d.Update(SolidFrame(160, 120, 150)));
    }
}