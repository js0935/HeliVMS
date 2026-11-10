using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 視覺搜索排序核心（QC_Live 的 PopupVisualSearch 對齊）。
/// 守門原因：排序寫錯的症狀是「以圖搜索回來的全是不相干的攝影機」，操作員只會說
/// 「這功能沒用」；門檻夾值寫錯則是整頁雜訊。純函式把排序、截斷、穩定性釘死。
/// </summary>
public sealed class VisualSearchRankTests
{
    private const ulong Sample = 0b_1111_0000_1111_0000_1111_0000_1111_0000_1111_0000_1111_0000_1111_0000_1111_0000;

    [Fact]
    public void 距離零的候選排最前()
    {
        var ranked = VisualSearchRank.Rank(Sample, [(7, Sample), (3, Sample ^ 1)], threshold: 12);

        Assert.Equal([7, 3], ranked.Select(m => m.EventId));
        Assert.Equal(0, ranked[0].Distance);
        Assert.Equal(1, ranked[1].Distance);
    }

    [Fact]
    public void 超過門檻的候選不進結果()
    {
        // 距離 13（>12）的候選即使 id 最小也不出現。
        var far = Sample ^ ((ulong)1 << 62) | 1uL; // 距離 ≥ 2，下方再以門檻 1 驗截斷
        var ranked = VisualSearchRank.Rank(Sample, [(1, far), (2, Sample)], threshold: 1);

        Assert.Equal([2], ranked.Select(m => m.EventId));
    }

    [Fact]
    public void 同距離以事件id升冪保持穩定()
    {
        var a = Sample ^ 1;
        var b = Sample ^ 2; // 兩個候選距離都是 1

        var ranked = VisualSearchRank.Rank(Sample, [(9, b), (4, a)], threshold: 12);

        Assert.Equal([4, 9], ranked.Select(m => m.EventId));
    }

    [Fact]
    public void 門檻夾在0到上限()
    {
        Assert.Equal(0, VisualSearchRank.ClampThreshold(-5));
        Assert.Equal(VisualSearchRank.MaxThreshold, VisualSearchRank.ClampThreshold(999));
        Assert.Equal(12, VisualSearchRank.ClampThreshold(VisualSearchRank.DefaultThreshold));
    }

    [Fact]
    public void 空候選回空結果()
    {
        Assert.Empty(VisualSearchRank.Rank(Sample, [], threshold: 12));
    }
}
