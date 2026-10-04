using System.Net;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// ffmpeg 編碼器探測（M244 §2：「若伺服器端 ffmpeg 沒有 libx264，啟動時要明確失敗
/// 並說明原因，不能靜默退成沒有畫面」）。
///
/// <para>
/// 症狀很難靠普通測試發現：沒有這道檢查時，缺 libx264 的 ffmpeg 照樣被啟動，
/// 輸出 <c>Unknown encoder</c> 後退出；操作員看到的是「等 30 秒然後失敗」加上
/// 一整段 ffmpeg stderr，而不是「這台 ffmpeg 沒有 H.264 編碼器」。
/// </para>
/// </summary>
[Collection(RtcIoCollection.Name)]
public sealed class FfmpegEncoderProbeTests
{
    private const string MissingExecutable = @"C:\helivms-not-a-real-dir\ffmpeg-missing.exe";

    /// <summary>
    /// 解析器只看「名稱欄」，而且必須整欄相等。
    /// <para>
    /// <c>libx264</c> 是 <c>libx264rgb</c> 的前綴。用 <c>Contains</c> 會在只裝了 RGB
    /// 變體的機器上誤判為可用——而 RGB 是 4:4:4，SFU 端與瀏覽器都解不出來，
    /// 症狀又是「連上了沒有畫面」。這是本測試存在的唯一理由。
    /// </para>
    /// </summary>
    [Fact]
    public void 編碼器名稱必須整欄相符而不是前綴相符()
    {
        // 真實格式的一行（`ffmpeg -encoders` 的輸出欄位以空白分開）。
        const string line = " V....D libx264rgb  libx264rgb H.264 RGB (unstable)";

        Assert.False(FfmpegEncoderProbe.MentionsEncoder(line, "libx264"));
        Assert.True(FfmpegEncoderProbe.MentionsEncoder(
            " V..... libx264  libx264 H.264 / AVC / MPEG-4 AVC", "libx264"));
    }

    [Fact]
    public void 編碼器清單必須是名稱欄而不是任何一欄()
    {
        // libx264 只能出現在描述欄、不能是編碼器名時不算可用。
        const string misleading = " V..... h264  h264 H.264, wrapper of libx264";

        Assert.False(FfmpegEncoderProbe.MentionsEncoder(misleading, "libx264"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 沒有輸出時不能說編碼器存在(string? output)
    {
        Assert.False(FfmpegEncoderProbe.MentionsEncoder(output, "libx264"));
    }

    [Fact]
    public void 問得到時要認得真的libx264()
    {
        // 真實 ffmpeg -encoders 的輸出片段（含表頭與好幾列無關編碼器）。
        const string output = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D a64multi             Multicolor charset for Commodore 64
             V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC
             V..... libx264rgb           libx264rgb H.264 RGB
            """;

        Assert.True(FfmpegEncoderProbe.MentionsEncoder(output, "libx264"));
    }

    /// <summary>
    /// 三種結果的映射：<b>Missing 與 Unavailable 必須分開</b>。
    /// <para>
    /// 兩者對操作員的建議完全相反——Missing 要換 ffmpeg build，Unavailable 只要把
    /// <c>HELIVMS_WHEP_FFMPEG</c> 指向存在的檔案。混成一個之後，維運會在正確的主機上
    /// 做錯誤的設定，而且沒有任何跡象顯示那裡出錯。
    /// </para>
    /// <para>
    /// 讀取器是注入的：真的去跑 <c>cmd.exe</c>／<c>findstr.exe</c> 當假 ffmpeg，
    /// 它們對 <c>-hide_banner -encoders</c> 的反應不是我們能控制的，測試不該建立在
    /// 別支程式的行為上。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 跑得起來但清單裡沒有該編碼器時必須說缺少()
    {
        const string output = """
            Encoders:
             V..... libx264rgb           libx264rgb H.264 RGB
            """;

        var probe = ProbeReturning(output);

        Assert.Equal(FfmpegEncoderStatus.Missing, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    [Fact]
    public async Task 清單裡有該編碼器時必須說可用()
    {
        const string output = """
            Encoders:
             V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC
            """;

        var probe = ProbeReturning(output);

        Assert.Equal(FfmpegEncoderStatus.Available, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    [Fact]
    public async Task 讀不到輸出時必須說問不到()
    {
        var probe = ProbeReturning(null);

        Assert.Equal(FfmpegEncoderStatus.Unavailable, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    [Fact]
    public async Task 讀取失敗時必須說問不到而不是缺少()
    {
        var probe = new FfmpegEncoderProbe("probe-throwing", TimeSpan.FromSeconds(5), _ =>
            throw new IOException("stream closed"));

        Assert.Equal(FfmpegEncoderStatus.Unavailable, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    private static FfmpegEncoderProbe ProbeReturning(string? output)
        => new("probe-injected", TimeSpan.FromSeconds(5), _ => Task.FromResult(output));

    /// <summary>
    /// 真的去啟動一個不存在的檔案時必須回「問不到」。
    /// <para>
    /// 這一條刻意用真的行程：它是正式環境最常見的一條路徑（<c>HELIVMS_WHEP_FFMPEG</c>
    /// 打錯字、ffmpeg 沒裝），而且正是它最容易被誤判成「沒有 libx264」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ffmpeg不存在時必須說問不到而不是缺少()
    {
        var probe = new FfmpegEncoderProbe(MissingExecutable, TimeSpan.FromSeconds(20));

        Assert.Equal(FfmpegEncoderStatus.Unavailable, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    /// <summary>
    /// 真的 ffmpeg 的輸出必須被認得出來。
    /// <para>
    /// 解析器是照著真實 <c>ffmpeg -encoders</c> 的格式寫的；只在注入的假字串上驗過的話，
    /// 一次格式假設錯誤就會讓整道檢查對所有正式環境失效，症狀仍是「沒有畫面」。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task 真的Ffmpeg有libx264時必須回報可用()
    {
        Skip.IfNot(PublishPipelineProbe.Libx264Available, "這個 ffmpeg 沒有 libx264。");

        var probe = new FfmpegEncoderProbe("ffmpeg", TimeSpan.FromSeconds(30));

        Assert.Equal(FfmpegEncoderStatus.Available, await probe.ProbeAsync(LiveEncodeOptions.VideoEncoder));
    }

    [Fact]
    public void 路徑空白必須被擋下()
    {
        Assert.Throws<ArgumentException>(() => new FfmpegEncoderProbe("  "));
    }

    [Fact]
    public async Task 空白編碼器名稱必須被擋下()
    {
        var probe = new FfmpegEncoderProbe("ffmpeg");

        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync("  "));
    }
}

/// <summary>
/// publisher 必須在碰攝影機之前就擋掉缺編碼器的 ffmpeg。
/// </summary>
public sealed class LivePublisherEncoderGuardTests
{
    private static IPEndPoint LoopbackTarget() => new(IPAddress.Loopback, 5004);

    private static readonly Func<string, CancellationToken, Task<FfmpegEncoderStatus>> Missing =
        (_, _) => Task.FromResult(FfmpegEncoderStatus.Missing);

    private static readonly Func<string, CancellationToken, Task<FfmpegEncoderStatus>> Unavailable =
        (_, _) => Task.FromResult(FfmpegEncoderStatus.Unavailable);

    private static readonly Func<string, CancellationToken, Task<FfmpegEncoderStatus>> Available =
        (_, _) => Task.FromResult(FfmpegEncoderStatus.Available);

    /// <summary>
    /// 缺 H.264 編碼器時必須立刻失敗，且訊息要指出該裝什麼。
    /// <para>
    /// 不做這件事的症狀：ffmpeg 照跑、讀完 RTSP、然後以 <c>Unknown encoder</c> 退出，
    /// 使用者等滿 <c>PublisherStartTimeout</c> 才看到一段原始 stderr。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 缺少H264編碼器時必須在啟動publisher前就失敗()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = "ffmpeg" },
            LiveEncodeOptions.Default,
            Missing);

        var ex = Assert.Throws<PublisherStartException>(
            () => publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget()));

        // 訊息必須點名缺的編碼器與解法；只說「啟動失敗」等於把診斷推回給使用者。
        Assert.Contains(LiveEncodeOptions.VideoEncoder, ex.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg-full", ex.Message, StringComparison.Ordinal);

        // RTSP 帳密絕不能跟著錯誤訊息出去（訊息會進 API 回應與 log）。
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);

        // 關鍵：publisher 不能被標成 Starting，否則上層會以為已經在跑。
        Assert.Equal(PublisherState.Stopped, publisher.State);
    }

    /// <summary>
    /// 「問不到 ffmpeg」不能被講成「沒有 libx264」。
    /// <para>
    /// 兩者的處置完全相反：前者只要改 <c>HELIVMS_WHEP_FFMPEG</c>，
    /// 後者要換整個 ffmpeg build。講錯會讓維運在正確的主機上做錯誤的設定。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ffmpeg不存在時仍然回報指向設定而不是編碼器問題()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = @"C:\helivms-not-a-real-dir\ffmpeg-missing.exe" },
            LiveEncodeOptions.Default,
            Unavailable);

        // 探測刻意不擋；真正的診斷由 Process.Start 那條路徑負責。
        var ex = Assert.Throws<PublisherStartException>(
            () => publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget()));

        Assert.Contains($"{WhepOptions.Prefix}FFMPEG", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveEncodeOptions.VideoEncoder, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>編碼器可用時探測不該擋住啟動。</summary>
    [Fact]
    public async Task 編碼器可用時探測不能擋住啟動()
    {
        string interpreter = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec
            ? comspec
            : "cmd.exe";

        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = interpreter },
            LiveEncodeOptions.Default,
            Available);

        // cmd.exe 收到無法解析的參數會立刻結束，但「行程確實被啟動」正是這條要證明的。
        var process = publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget());

        Assert.NotNull(process);
    }
}
