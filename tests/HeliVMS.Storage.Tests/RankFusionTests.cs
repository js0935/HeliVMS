using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>排名融合（M154）：FTS bm25 與 CLIP 向量加權合併。</summary>
public sealed class RankFusionTests
{
    [Fact]
    public void Fuse_RanksVectorOnlyAboveTextOnly()
    {
        var result = RankFusion.Fuse(new[]
        {
            new FusionItem("alarm:1", TextScore: 0.9, VectorScore: null),
            new FusionItem("alarm:2", TextScore: 0.0, VectorScore: 0.5),
            new FusionItem("alarm:3", TextScore: 0.0, VectorScore: 0.9),
        });

        Assert.Equal(new[] { "alarm:3", "alarm:2", "alarm:1" }, result.Select(r => r.Key));
    }

    [Fact]
    public void Fuse_BlendsBothSignals()
    {
        var result = RankFusion.Fuse(new[]
        {
            new FusionItem("a1", TextScore: 0.1, VectorScore: 0.9),
            new FusionItem("a2", TextScore: 0.8, VectorScore: 0.1),
        });

        Assert.Equal("a1", result[0].Key);
        Assert.True(result[0].Score > result[1].Score);
        Assert.InRange(result[0].Score, 0, 1);
        Assert.InRange(result[1].Score, 0, 1);
    }

    [Fact]
    public void Fuse_ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(RankFusion.Fuse(Array.Empty<FusionItem>()));
    }

    [Fact]
    public void NormalizeKey_JoinsSourceAndRef()
    {
        Assert.Equal("pos:42", RankFusion.NormalizeKey("pos", 42));
    }
}