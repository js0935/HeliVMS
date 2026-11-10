using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 「這一輪要不要帶 <c>-hwaccel</c>」的決策（<see cref="HardwareDecodePolicy"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這條規則錯了不會有任何錯誤訊息：<c>auto</c> 若被原樣交給 ffmpeg，實測（本機 i5-1035G1、
/// 6 路 1080p15 即時速率）原生尺寸 36.42s，比純軟解 25.57s 還差；16 路縮到 640×360 時
/// 軟解 61.20s vs <c>d3d11va</c> 26.11s。也就是「設了 auto」和「設了 d3d11va」在現場
/// 差了整整一倍 CPU，而畫面上完全看不出來。
/// </para>
/// <para>
/// 另一個不能搞錯的方向是「原生尺寸不要硬解」：那裡的下載成本把省下的解碼成本吃光，
/// 卻多了一層 GPU 依賴——所以政策必須看得到這一輪有沒有縮尺寸。
/// </para>
/// </remarks>
public class HardwareDecodePolicyTests
{
    private static readonly IReadOnlyCollection<string> WindowsFfmpeg = new[] { "cuda", "dxva2", "d3d11va" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData("none")]
    [InlineData("false")]
    [InlineData("0")]
    public void 停用的設定一律回軟解(string? configured)
    {
        Assert.Null(HardwareDecodePolicy.Resolve(configured, WindowsFfmpeg, scalingActive: true));
    }

    [Fact]
    public void auto在有縮小解碼時挑偏好順序中第一個可用的()
    {
        // 偏好順序是量出來的：d3d11va 在縮尺寸解碼省下 52–62%，且 24 路同時仍可用。
        var resolved = HardwareDecodePolicy.Resolve("auto", WindowsFfmpeg, scalingActive: true);

        Assert.Equal("d3d11va", resolved);
    }

    [Fact]
    public void auto在缺少d3d11va時才退而求其次()
    {
        Assert.Equal(
            "dxva2",
            HardwareDecodePolicy.Resolve("auto", new[] { "dxva2", "qsv", "vaapi" }, scalingActive: true));
        Assert.Equal(
            "qsv",
            HardwareDecodePolicy.Resolve("auto", new[] { "qsv" }, scalingActive: true));
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("H264")]
    [InlineData("avc1")]
    public void 原生尺寸的H264維持軟解(string codec)
    {
        // 原生 1080p H.264 硬解實測與軟解打平（26.17s vs 25.57s），不值得多一層 GPU 依賴。
        Assert.Null(HardwareDecodePolicy.Resolve("auto", WindowsFfmpeg, scalingActive: false, videoCodec: codec));
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("HEVC")]
    [InlineData(" h265 ")]
    [InlineData("hvc1")]
    [InlineData("hev1")]
    public void 原生尺寸的HEVC仍會啟用硬體解碼(string codec)
    {
        // 實測原生 1080p：HEVC 8-bit 40.42/46.86s → 35.47/36.28s（−12%～−23%），
        // HEVC 10-bit 62.73/84.36s → 41.66/46.03s（−34%～−45%）。軟解愈貴，下載成本佔比愈小。
        Assert.Equal(
            "d3d11va",
            HardwareDecodePolicy.Resolve("auto", WindowsFfmpeg, scalingActive: false, videoCodec: codec));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mpeg2video")]
    public void 未探測到編碼格式或非HEVC時原生尺寸回軟解(string? codec)
    {
        // 不知道格式時挑任何一個都是猜的；猜錯的代價是白吃的 GPU 依賴（連得上卻沒畫面的風險）。
        Assert.Null(HardwareDecodePolicy.Resolve("auto", WindowsFfmpeg, scalingActive: false, videoCodec: codec));
    }

    [Fact]
    public void 縮小解碼時不看編碼格式()
    {
        // 縮尺寸是量到 52–70% 的那一條，與格式無關：H.264 也照用。
        Assert.Equal(
            "d3d11va",
            HardwareDecodePolicy.Resolve("auto", WindowsFfmpeg, scalingActive: true, videoCodec: "h264"));
    }

    [Fact]
    public void HEVC在探測失敗時仍回軟解()
    {
        // 格式划算不代表本機做得到：探測失敗＝不知道有哪一個方法。
        Assert.Null(HardwareDecodePolicy.Resolve("auto", [], scalingActive: false, videoCodec: "hevc"));
    }

    [Fact]
    public void auto在探測失敗時回軟解()
    {
        // 探測失敗＝不知道本機有哪一個；此時挑任何一個都是猜的，而猜錯的症狀是
        // 「連得上卻沒畫面」——比多花一點 CPU 難診斷得多。
        Assert.Null(HardwareDecodePolicy.Resolve("auto", [], scalingActive: true));
    }

    [Fact]
    public void auto在沒有偏好清單內的方法時回軟解()
    {
        Assert.Null(HardwareDecodePolicy.Resolve("auto", new[] { "cuda", "vaapi", "vulkan" }, scalingActive: true));
    }

    [Fact]
    public void 明確指定的值不受縮小解碼與否影響()
    {
        // 使用者已經表明要用那個方法（包含原生尺寸也要用），政策不替他改主意。
        Assert.Equal("d3d11va", HardwareDecodePolicy.Resolve("d3d11va", WindowsFfmpeg, scalingActive: false));
        Assert.Equal("d3d11va", HardwareDecodePolicy.Resolve("d3d11va", WindowsFfmpeg, scalingActive: true));
    }

    [Fact]
    public void 明確指定但本機未回報時仍會一試()
    {
        // 探測失敗（清單為空）時無從判斷，交由執行期 fallback 兜底：寧可試了再退，
        // 也不要因探測失敗而靜默停用（既有 ShouldUse 的語意）。
        Assert.Equal("cuda", HardwareDecodePolicy.Resolve("cuda", [], scalingActive: true));
    }

    [Fact]
    public void 明確指定但清單不含它時回軟解()
    {
        Assert.Null(HardwareDecodePolicy.Resolve("cuda", new[] { "d3d11va", "dxva2" }, scalingActive: true));
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData(" auto ")]
    public void auto不分大小寫與前後空白(string configured)
    {
        Assert.Equal(
            "d3d11va",
            HardwareDecodePolicy.Resolve(configured, WindowsFfmpeg, scalingActive: true));
    }
}
