using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 視覺搜索雜湊管線（Bgra→灰階→ROI 裁切→感知雜湊）。
/// 守門原因：樣本與事件快照必須走同一條管線——裁切/灰階任何一側不一致，
/// 漢明距離就是亂數，以圖搜索回來的全是不相干的攝影機。
/// 另釘 ROI 的像素換算（相對→絕對、夾邊界、至少 1×1）。
/// </summary>
public sealed class VisualHashTests
{
    private static byte[] White(int w = 8, int h = 8)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = 255;
            px[i * 4 + 1] = 255;
            px[i * 4 + 2] = 255;
            px[i * 4 + 3] = 255;
        }

        return px;
    }

    private static byte[] LeftBrightRightDark(int w = 16, int h = 8)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var v = (byte)(x < w / 2 ? 255 : 0);
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
                px[i + 3] = 255;
            }
        }

        return px;
    }

    [Fact]
    public void 純白圖的雜湊不受roi影響()
    {
        var full = VisualHash.FromBgra(White(), 8, 8, null);
        var bottomRight = VisualHash.FromBgra(
            White(), 8, 8, new VisualHash.Roi(0.5, 0.5, 0.5, 0.5));

        Assert.Equal(0ul, full);
        Assert.Equal(0ul, bottomRight);
    }

    [Fact]
    public void roi改變雜湊_條紋與均勻的裁切不同()
    {
        // 左半＝亮暗直條紋（dHash 非零），右半＝均勻暗（dHash 為 0）：
        // 均勻區域的 dHash 恆為 0，所以測試圖不能用均勻半圖。
        var w = 16;
        var h = 8;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var v = (byte)(x < w / 2 ? (x % 2 == 0 ? 255 : 0) : 0);
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
                px[i + 3] = 255;
            }
        }

        var leftHash = VisualHash.FromBgra(px, w, h, new VisualHash.Roi(0, 0, 0.5, 1));
        var rightHash = VisualHash.FromBgra(px, w, h, new VisualHash.Roi(0.5, 0, 0.5, 1));

        Assert.NotEqual(0ul, leftHash);
        Assert.NotEqual(leftHash, rightHash);
    }

    [Fact]
    public void roi為null時用整張圖()
    {
        var px = LeftBrightRightDark();

        var full = VisualHash.FromBgra(px, 16, 8, null);
        var left = VisualHash.FromBgra(px, 16, 8, new VisualHash.Roi(0, 0, 0.5, 1));

        Assert.NotEqual(full, left);
    }

    [Fact]
    public void roi像素換算夾在影像邊界內且至少一像素()
    {
        var (x, y, w, h) = VisualHash.PixelRect(
            new VisualHash.Roi(0.8, 0.8, 0.5, 0.5), 100, 50);
        Assert.Equal(80, x);
        Assert.Equal(40, y);
        Assert.Equal(20, w);
        Assert.Equal(10, h);
    }

    [Fact]
    public void roi為null的像素矩形是整張圖()
    {
        var (x, y, w, h) = VisualHash.PixelRect(null, 1920, 1080);
        Assert.Equal((0, 0, 1920, 1080), (x, y, w, h));
    }

    [Fact]
    public void 灰階用Rec709加權()
    {
        var green = VisualHash.BgraToGray([0, 255, 0, 255], 1, 1);
        var blue = VisualHash.BgraToGray([255, 0, 0, 255], 1, 1);

        Assert.True(green[0] > blue[0]);
        Assert.Equal(149, green[0]);
        Assert.Equal(29, blue[0]);
    }

    [Fact]
    public void 裁切保留對應列的像素()
    {
        var gray = new byte[16];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                gray[y * 4 + x] = (byte)y;
            }
        }

        var (cropped, cw, ch) = VisualHash.CropGray(gray, 4, 4, 1, 1, 2, 2);

        Assert.Equal(2, cw);
        Assert.Equal(2, ch);
        Assert.Equal([1, 1, 2, 2], cropped);
    }
}
