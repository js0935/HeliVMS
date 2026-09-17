using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms.Tests;

public class TamperDetectionTests
{
    private static VideoFrame Uniform(byte value)
    {
        var px = new byte[160 * 120 * 3];
        Array.Fill(px, value);
        return new VideoFrame
        {
            Width = 160,
            Height = 120,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    private static VideoFrame Checker(int cell = 8, byte dark = 40, byte light = 200)
    {
        const int w = 160;
        const int h = 120;
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = ((x / cell) + (y / cell)) % 2 == 0 ? light : dark;
                var i = ((y * w) + x) * 3;
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
            }
        }

        return new VideoFrame
        {
            Width = w,
            Height = h,
            Pixels = px,
            TimestampUtc = DateTime.UtcNow,
            PtsMs = 0,
        };
    }

    [Fact]
    public void Blackout_DetectedImmediately()
    {
        var detector = new TamperDetector();
        var obs = detector.Update(Uniform(0));
        Assert.Equal(TamperKind.Blackout, obs.Kind);
    }

    [Fact]
    public void Whiteout_DetectedImmediately()
    {
        var detector = new TamperDetector();
        var obs = detector.Update(Uniform(255));
        Assert.Equal(TamperKind.Whiteout, obs.Kind);
    }

    [Fact]
    public void NormalTextured_StaysNone_AndBuildsBaseline()
    {
        var detector = new TamperDetector(baselineFrames: 4);
        Assert.False(detector.IsBaselineReady);

        for (var i = 0; i < 6; i++)
        {
            var obs = detector.Update(Checker());
            Assert.Equal(TamperKind.None, obs.Kind);
        }

        Assert.True(detector.IsBaselineReady);
        Assert.True(detector.BaselineEdge > 0);
    }

    [Fact]
    public void Covered_WhenEdgeCollapsesAfterBaseline()
    {
        var detector = new TamperDetector(baselineFrames: 4);
        for (var i = 0; i < 4; i++)
        {
            detector.Update(Checker());
        }

        Assert.True(detector.IsBaselineReady);

        var obs = detector.Update(Uniform(128));
        Assert.Equal(TamperKind.Covered, obs.Kind);
    }

    [Fact]
    public void Covered_NotReported_WhenBaselineEdgeTooLow()
    {
        // 低紋理場景（均勻灰）建立基準 → 之後仍均勻，不應誤判為被遮。
        var detector = new TamperDetector(baselineFrames: 4);
        for (var i = 0; i < 6; i++)
        {
            detector.Update(Uniform(128));
        }

        Assert.True(detector.IsBaselineReady);
        Assert.Equal(TamperKind.None, detector.Update(Uniform(128)).Kind);
    }

    [Fact]
    public void Reset_ClearsBaseline()
    {
        var detector = new TamperDetector(baselineFrames: 4);
        for (var i = 0; i < 4; i++)
        {
            detector.Update(Checker());
        }

        Assert.True(detector.IsBaselineReady);
        detector.Reset();
        Assert.False(detector.IsBaselineReady);
        Assert.Equal(0, detector.BaselineEdge);
    }

    [Fact]
    public void EdgeEnergy_UniformIsZero_CheckerIsPositive()
    {
        var uniform = new byte[16 * 16];
        var checker = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                checker[(y * 16) + x] = (byte)(((x + y) % 2 == 0) ? 200 : 40);
            }
        }

        Assert.Equal(0, TamperDetector.EdgeEnergy(uniform, 16, 16));
        Assert.True(TamperDetector.EdgeEnergy(checker, 16, 16) > 0);
    }
}
