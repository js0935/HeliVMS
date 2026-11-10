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

    /// <summary>有紋理的測試圖（斜向漸層＋棋盤），避免低紋理讓雜湊恆為 0。</summary>
    private static byte[] Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var v = (byte)((x * 7 + y * 13 + ((x / 4 + y / 4) % 2) * 90) & 0xFF);
                px[i] = v;
                px[i + 1] = (byte)(255 - v);
                px[i + 2] = (byte)((v * 3) & 0xFF);
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
    public void 索引解碼尺寸讓每個九乘八區塊剛好八乘八像素()
    {
        // 索引尺寸決定每個區塊的取樣面積，也就是既有的 snapshot_hash 本身。
        // 換掉它等於換掉雜湊的定義，而舊索引不會自動重建（沒有任何錯誤訊息）。
        Assert.Equal(PerceptualHash.GrayColumns * 8, VisualHash.IndexWidth);
        Assert.Equal(PerceptualHash.GrayRows * 8, VisualHash.IndexHeight);
        Assert.Equal(0, VisualHash.IndexWidth % PerceptualHash.GrayColumns);
        Assert.Equal(0, VisualHash.IndexHeight % PerceptualHash.GrayRows);
    }

    [Fact]
    public void 整張圖的雜湊與舊的灰階入口完全相同()
    {
        // 原本 VisualSearchService 自己解碼成灰階再呼叫 PerceptualHash.FromGrayImage。
        // 改成共用的 VisualHash.FromBgra(roi: null) 之後，既有 snapshot_hash 必須仍然可比，
        // 否則以圖搜索會默默變成「找出來的都不像」——所以這條相等性就是「不需要重建索引」
        // 的唯一依據。
        var px = Gradient(VisualHash.IndexWidth, VisualHash.IndexHeight);

        var viaBgra = VisualHash.FromBgra(px, VisualHash.IndexWidth, VisualHash.IndexHeight, null);
        var viaGray = PerceptualHash.FromGrayImage(
            VisualHash.BgraToGray(px, VisualHash.IndexWidth, VisualHash.IndexHeight),
            VisualHash.IndexWidth,
            VisualHash.IndexHeight);

        // 先確認測試圖不是低紋理（否則兩側都是 0，這條會「通過」卻什麼都沒驗到）。
        Assert.NotEqual(0ul, viaGray);
        Assert.Equal(viaGray, viaBgra);
    }

    [Fact]
    public void 框選太小會被拒絕而不是變成一個像素的雜湊()
    {
        // 1×1 的裁切沒有任何明暗變化，雜湊恆為 0；而任何樣本與 0 的漢明距離都等於它的
        // 位元數。若把「點一下」當成框選，以圖搜索會回全部事件，操作員只看到自己框了一小角。
        Assert.Null(VisualHash.FromPixels(10, 10, 0, 0, 640, 360));
        Assert.Null(VisualHash.FromPixels(10, 10, 7, 200, 640, 360));
        Assert.Null(VisualHash.FromPixels(10, 10, 200, 7, 640, 360));
        Assert.NotNull(VisualHash.FromPixels(10, 10, 8, 8, 640, 360));

        // 影像尺寸無效（還沒載入）也不能算出一個框。
        Assert.Null(VisualHash.FromPixels(0, 0, 100, 100, 0, 0));
        Assert.Null(VisualHash.FromPixels(double.NaN, 0, 100, 100, 640, 360));
    }

    [Fact]
    public void 框選可以反向拖曳且夾在影像邊界內()
    {
        // 操作員從右下往左上拖是常態，不該因為方向不同就得到不同的框。
        Assert.Equal(
            VisualHash.FromPixels(100, 50, 200, 100, 800, 400),
            VisualHash.FromPixels(300, 150, -200, -100, 800, 400));

        // 拖出畫面外時夾回邊界（否則 PixelRect 會把寬高夾成 1，等於框了一個像素）。
        var clipped = VisualHash.FromPixels(700, 300, 400, 400, 800, 400);
        Assert.Equal(new VisualHash.Roi(0.875, 0.75, 0.125, 0.25), clipped);

        // 換算回像素時仍在影像內。
        var (x, y, w, h) = VisualHash.PixelRect(clipped, 800, 400);
        Assert.Equal((700, 300, 100, 100), (x, y, w, h));
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
