using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 影格縮放尺寸的唯一口徑（<see cref="VideoScaling"/>）。
/// </summary>
/// <remarks>
/// 為什麼值得單獨測試：解碼端送給 ffmpeg 的 <c>scale=</c> 與呼叫端用來湊滿一幀的位元組數
/// 只要差一個像素，每一張影格就錯位（畫面全黑或斜切），而日誌完全乾淨——這是「連上了、
/// 沒畫面」最難查的一種，而它唯一的防線就是兩邊都呼叫這一份口徑。
/// </remarks>
public class VideoScalingTests
{
    [Theory]
    [InlineData(640, 360)]
    [InlineData(512, 288)]
    [InlineData(384, 216)]
    [InlineData(320, 180)]
    [InlineData(256, 144)]
    public void 依寬度換算的一零八零p高度就是等比例縮小(int width, int expectedHeight)
    {
        Assert.Equal(expectedHeight, VideoScaling.EvenHeight(width, 1920, 1080));
    }

    [Fact]
    public void 高度一律取偶數()
    {
        // yuv420p 要求偶數高度；405 這種來源高度（實機次碼流）除以比例後常是奇數。
        var height = VideoScaling.EvenHeight(320, 1280, 405);

        Assert.Equal(0, height % 2);

        // 逐點驗一次：任何一個寬度上限算出來的高度都不得是奇數，否則 ffmpeg 會拒絕
        // scale 或吐出與呼叫端不同的高度。
        foreach (var width in new[] { 64, 100, 128, 256, 320, 480, 640 })
        {
            Assert.Equal(0, VideoScaling.EvenHeight(width, 1920, 1080) % 2);
            Assert.Equal(0, VideoScaling.EvenHeight(width, 1280, 405) % 2);
        }
    }

    [Fact]
    public void 寬度上限不小於來源寬度時不需要縮放()
    {
        // 回 (0,0) 是刻意的：呼叫端要能分辨「不必縮」，否則會多開一次 ffmpeg，
        // 或多塞一個沒有意義的 scale=1920:1080。
        Assert.Equal((0, 0), VideoScaling.Fit(1920, 1080, 1920));
        Assert.Equal((0, 0), VideoScaling.Fit(640, 360, 1920));
        Assert.Equal((0, 0), VideoScaling.Fit(640, 360, 0));
        Assert.Equal((0, 0), VideoScaling.Fit(640, 360, -1));
    }

    [Fact]
    public void 需要縮放時寬度就是上限且高度成對()
    {
        var (width, height) = VideoScaling.Fit(1920, 1080, 640);

        Assert.Equal(640, width);
        Assert.Equal(VideoScaling.EvenHeight(640, 1920, 1080), height);
    }

    [Fact]
    public void 來源尺寸未知時不縮放()
    {
        // 探測失敗（0×0）時回 0 而不是除零或負數。
        Assert.Equal(0, VideoScaling.EvenHeight(640, 0, 0));
        Assert.Equal(0, VideoScaling.EvenHeight(640, 640, 0));
        Assert.Equal((0, 0), VideoScaling.Fit(0, 0, 640));
    }

    [Fact]
    public void 極小高度不會縮成零或一()
    {
        // 3840×40 這種全景來源縮到 64 寬時比例高度不到 1：0 或 1 的高度會讓
        // frameSize 變成 0 或半個像素列，讀取迴圈永遠湊不滿一幀。
        var height = VideoScaling.EvenHeight(64, 3840, 40);

        Assert.True(height >= 2, $"高度至少要 2（實得 {height}）");
        Assert.Equal(0, height % 2);
    }
}
