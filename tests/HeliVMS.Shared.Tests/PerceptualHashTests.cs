using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 感知雜湊（以圖搜圖的演算法核心）。
/// </summary>
/// <remarks>
/// 這是「找出相似畫面」唯一的判別依據：位元方向寫反、相鄰比較指到自己，
/// 都會讓「任何兩張圖都說相似」——搜尋結果滿屏假命中，而畫面上看不出來。
/// </remarks>
public sealed class PerceptualHashTests
{
    /// <summary>每列遞減（255、254、…、247）＝左比右亮全成立 → 指紋應為全 1。</summary>
    private static byte[] DescendingRows()
    {
        var g = new byte[PerceptualHash.GrayBytes];
        for (var y = 0; y < PerceptualHash.GrayRows; y++)
        {
            for (var x = 0; x < PerceptualHash.GrayColumns; x++)
            {
                g[(y * PerceptualHash.GrayColumns) + x] = (byte)(255 - x);
            }
        }

        return g;
    }

    [Fact]
    public void 逐列遞減的灰階指紋為全一()
    {
        Assert.Equal(ulong.MaxValue, PerceptualHash.FromGray9x8(DescendingRows()));
    }

    [Fact]
    public void 逐列遞增的灰階指紋為全零()
    {
        var g = new byte[PerceptualHash.GrayBytes];
        for (var y = 0; y < PerceptualHash.GrayRows; y++)
        {
            for (var x = 0; x < PerceptualHash.GrayColumns; x++)
            {
                g[(y * PerceptualHash.GrayColumns) + x] = (byte)(240 + x);
            }
        }

        Assert.Equal(0UL, PerceptualHash.FromGray9x8(g));
    }

    [Fact]
    public void 明暗反轉的指紋漢明距離為滿值()
    {
        var descending = PerceptualHash.FromGray9x8(DescendingRows());
        var h = DescendingRows();
        for (var i = 0; i < h.Length; i++)
        {
            h[i] = (byte)(255 - h[i]);
        }

        var ascending = PerceptualHash.FromGray9x8(h);
        Assert.Equal(PerceptualHash.Bits, PerceptualHash.HammingDistance(descending, ascending));
    }

    [Fact]
    public void 整體亮度平移不改變指紋()
    {
        // 監視畫面日夜曝光差很大；dHash 只看相鄰明暗方向，整體變亮/變暗必須不影響。
        var darker = DescendingRows();
        for (var i = 0; i < darker.Length; i++)
        {
            darker[i] = (byte)(darker[i] - 10);
        }

        Assert.Equal(PerceptualHash.FromGray9x8(DescendingRows()), PerceptualHash.FromGray9x8(darker));
    }

    [Fact]
    public void 相同影像距離零不同結構距離不為零()
    {
        var a = PerceptualHash.FromGray9x8(DescendingRows());
        Assert.Equal(0, PerceptualHash.HammingDistance(a, a));

        var flat = new byte[PerceptualHash.GrayBytes]; // 全黑：無任何明暗變化
        var b = PerceptualHash.FromGray9x8(flat);
        Assert.Equal(PerceptualHash.Bits, PerceptualHash.HammingDistance(a, b));
    }

    [Fact]
    public void 均勻影像降採樣後指紋為零()
    {
        // 全 200 的 4x4：盒降採樣後仍是均勻，沒有任何相鄰差異 → 指紋 0。
        var gray = new byte[16];
        Array.Fill(gray, (byte)200);
        Assert.Equal(0UL, PerceptualHash.FromGrayImage(gray, 4, 4));
    }

    [Fact]
    public void 左右兩區的影像降採樣後保有結構()
    {
        // 左半 200、右半 100 的 16x8：由亮到暗的邊界必須在指紋裡留下位元
        // （不能退化成全 0）。方向互換（左暗右亮）在「左>右記 1」的慣例下
        // 本來就是全 0，不具判別力——這裡刻意選有變化的那一側。
        var gray = new byte[16 * 8];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                gray[(y * 16) + x] = x < 8 ? (byte)200 : (byte)100;
            }
        }

        var hash = PerceptualHash.FromGrayImage(gray, 16, 8);
        Assert.NotEqual(0UL, hash);
        Assert.NotEqual(ulong.MaxValue, hash);
    }

    [Fact]
    public void 相似度一減距離除以位元數()
    {
        Assert.Equal(1.0, PerceptualHash.Similarity(123UL, 123UL));
        Assert.Equal(0.0, PerceptualHash.Similarity(0UL, ulong.MaxValue));
    }

    [Fact]
    public void 尺寸不合法會擋下來()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualHash.FromGrayImage([], 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualHash.FromGrayImage([], 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualHash.FromGrayImage([], 4, 4)); // 長度不足
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualHash.FromGray9x8(new byte[10]));
    }
}
