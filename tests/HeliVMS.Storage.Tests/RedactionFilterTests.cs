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
}