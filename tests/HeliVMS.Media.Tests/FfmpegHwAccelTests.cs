using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 驗證硬體解碼能力偵測（<see cref="FfmpegHwAccel"/>）。這是「設定硬體解碼後連得上卻沒畫面、
/// 日誌卻乾淨」的第一道防線：先問本機 ffmpeg 支援哪些加速方式，支援才帶 <c>-hwaccel</c>。
/// </summary>
public class FfmpegHwAccelTests
{
    [Fact]
    public void 解析hwaccels輸出會略過標題行並轉小寫()
    {
        const string output =
            "Hardware acceleration methods:\r\n" +
            "d3d11va\r\n" +
            "DXVA2\r\n" +
            "cuda\r\n";

        var set = FfmpegHwAccel.ParseHwAccels(output);

        Assert.Equal(3, set.Count);
        Assert.Contains("d3d11va", set);
        Assert.Contains("dxva2", set);
        Assert.Contains("cuda", set);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空的探測輸出得到空集合(string? output)
    {
        Assert.Empty(FfmpegHwAccel.ParseHwAccels(output));
    }

    [Fact]
    public void 解析時略過含空白的行以免把警告當成加速方式()
    {
        // ffmpeg 有時會在 stdout 混入非方法名的訊息（例如找不到 GPU 的警告）。
        // 這些含空白的行絕不能被當成可用的加速方式。
        const string output =
            "Hardware acceleration methods:\n" +
            "d3d11va\n" +
            "No device available\n" +
            "  \n" +
            "cuda\n";

        var set = FfmpegHwAccel.ParseHwAccels(output);

        Assert.Equal(2, set.Count);
        Assert.DoesNotContain("No device available", set);
    }

    [Fact]
    public void 未設定加速值時不啟用()
    {
        Assert.False(FfmpegHwAccel.ShouldUse(null, new HashSet<string> { "d3d11va" }));
    }

    [Fact]
    public void auto一律允許交給ffmpeg自行挑選()
    {
        Assert.True(FfmpegHwAccel.ShouldUse("auto", new HashSet<string> { "d3d11va" }));
        // 即使本機清單沒有 auto 這個字，也應允許。
        Assert.True(FfmpegHwAccel.ShouldUse("auto", new HashSet<string>()));
    }

    [Fact]
    public void 本機支援該加速方式時啟用()
    {
        Assert.True(FfmpegHwAccel.ShouldUse("d3d11va", new HashSet<string> { "d3d11va", "cuda" }));
        // 大小寫不敏感。
        Assert.True(FfmpegHwAccel.ShouldUse("D3D11VA", new HashSet<string> { "d3d11va" }));
    }

    [Fact]
    public void 本機不支援該加速方式時停用()
    {
        // 這是關鍵：設定 cuda 但本機 ffmpeg 不支援 cuda，必須停用，否則連得上卻沒畫面。
        Assert.False(FfmpegHwAccel.ShouldUse("cuda", new HashSet<string> { "d3d11va", "dxva2" }));
    }

    [Fact]
    public void 探測清單為空時仍然啟用交由執行期fallback兜底()
    {
        // 探測失敗（ffmpeg 不存在、逾時）時我們無從判斷；此時若靜默停用，
        // 等於在真正支援硬解的機器上也白白放棄加速。改為照帶，讓執行期 fallback 收拾。
        Assert.True(FfmpegHwAccel.ShouldUse("d3d11va", Array.Empty<string>()));
    }
}
