using HeliVMS.Recording;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class RedactionFilterTests
{
    [Fact]
    public void Build_SingleRoi_ContainsCropBlurOverlay()
    {
        var filter = RedactionFilter.Build(new[] { new RedactionRoi(0, 0, 50, 40) });

        Assert.StartsWith("[0:v]split=2[s0][s1];", filter);
        Assert.Contains("[s1]crop=50:40:0:0,boxblur=10:2:10:2,scale=50:40[r1]", filter);
        Assert.EndsWith("[s0][r1]overlay=0:0[vout]", filter);
    }

    [Fact]
    public void Build_ThreeRois_SplitAndOrdered()
    {
        var filter = RedactionFilter.Build(new[]
        {
            new RedactionRoi(0, 0, 10, 10),
            new RedactionRoi(100, 20, 30, 40),
            new RedactionRoi(200, 300, 400, 500),
        });

        Assert.StartsWith("[0:v]split=4[s0][s1][s2][s3];", filter);
        Assert.Contains("[s1]crop=10:10:0:0,boxblur=2:2:2:2,scale=10:10[r1]", filter);
        Assert.Contains("[s2]crop=30:40:100:20,boxblur=7:2:7:2,scale=30:40[r2]", filter);
        Assert.Contains("[s3]crop=400:500:200:300,boxblur=12:2:12:2,scale=400:500[r3]", filter);
        Assert.Contains("[s0][r1]overlay=0:0[m1]", filter);
        Assert.Contains("[m1][r2]overlay=100:20[m2]", filter);
        Assert.EndsWith("[m2][r3]overlay=200:300[vout]", filter);
    }

    [Fact]
    public void Build_Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => RedactionFilter.Build(Array.Empty<RedactionRoi>()));
        Assert.Throws<InvalidOperationException>(() => RedactionFilter.Build(null!));
    }

    [Fact]
    public void Build_InvalidRoi_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionFilter.Build(new[] { new RedactionRoi(-1, 0, 10, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionFilter.Build(new[] { new RedactionRoi(0, 0, 0, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionFilter.Build(new[] { new RedactionRoi(0, 0, 10, 0) }));
    }

    [Fact]
    public void Build_FilledRoi_UsesSolidDrawboxNotBlur()
    {
        var filter = RedactionFilter.Build(new[] { new RedactionRoi(10, 20, 30, 40, Filled: true) });

        Assert.Contains(
            "[s1]crop=30:40:10:20,drawbox=x=0:y=0:w=30:h=40:color=black@1.0:t=fill,scale=30:40[r1]",
            filter);
        Assert.DoesNotContain("boxblur", filter);
        Assert.EndsWith("[s0][r1]overlay=10:20[vout]", filter);
    }

    [Fact]
    public void Build_MixedFilledAndBlur_PerRoiMask()
    {
        var filter = RedactionFilter.Build(new[]
        {
            new RedactionRoi(0, 0, 10, 10),
            new RedactionRoi(100, 20, 30, 40, Filled: true),
        });

        Assert.Contains("boxblur=2:2:2:2", filter);
        Assert.Contains("drawbox=x=0:y=0:w=30:h=40:color=black@1.0:t=fill", filter);
    }

    [Fact]
    public void Build_CustomOutputLabel_EndsWithLabel()
    {
        var filter = RedactionFilter.Build(new[] { new RedactionRoi(0, 0, 10, 10) }, "[redact]");

        Assert.EndsWith("[redact]", filter);
        Assert.DoesNotContain("[vout]", filter);
    }

    [Fact]
    public void Build_BlankOutputLabel_Throws()
    {
        Assert.Throws<ArgumentException>(() => RedactionFilter.Build(new[] { new RedactionRoi(0, 0, 10, 10) }, " "));
    }

    [Fact]
    public void FromRegions_PreservesGeometryAndFilled()
    {
        var t = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);
        var regions = new[]
        {
            new RedactionRegion(1, RedactionSources.Clip, 5, 2, t, 10, 20, 30, 40, Filled: true, t),
            new RedactionRegion(2, RedactionSources.Clip, 5, 2, t, 0, 0, 8, 8, Filled: false, t),
        };

        var rois = RedactionFilter.FromRegions(regions);

        Assert.Equal(2, rois.Count);
        Assert.Equal(new RedactionRoi(10, 20, 30, 40, true), rois[0]);
        Assert.Equal(new RedactionRoi(0, 0, 8, 8, false), rois[1]);
    }

    [Fact]
    public void FromRegions_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RedactionFilter.FromRegions(null!));
    }
}