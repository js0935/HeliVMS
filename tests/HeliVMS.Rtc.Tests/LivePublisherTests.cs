using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// <see cref="LivePublisher"/> 的行程生命週期與錯誤回報測試。
///
/// <para>
/// 為什麼需要這一層：publisher 是唯一一個「會真的啟動外部行程」的型別，而 M244 的致命
/// 錯誤正是發生在這裡（ffmpeg 參數拼錯 → 進程啟動即死）。純函式測試與假實作都碰不到
/// <c>Process.Start</c>、stderr 幫浦與結束碼處理，而這三件事正是出錯時唯一能給維運線索的
/// 地方。
/// </para>
///
/// <para>
/// 這裡刻意<b>不</b>依賴 ffmpeg：把 <c>ProcessStartInfo.FileName</c> 換成
/// <c>cmd.exe</c> 就能得到真實行程、真實 stderr 與真實結束碼，而不需要在 PATH 上裝任何
/// 東西。需要真實 ffmpeg 行為的測試在 <see cref="PublishPipelineTests"/>。
/// </para>
/// </summary>
[Collection(RtcIoCollection.Name)]
public sealed class LivePublisherTests
{
    /// <summary>
    /// 一個必然不存在、也不該被執行的路徑。用完整副檔名避免誤中 PATH 上的東西。
    /// </summary>
    private const string MissingExecutable = @"C:\helivms-not-a-real-dir\ffmpeg-missing.exe";

    /// <summary>以 <c>cmd.exe</c> 當 publisher：收到 ffmpeg 參數會立刻結束，但行程是真的。</summary>
    private static string CommandInterpreter =>
        Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";

    /// <summary>
    /// 以 <c>findstr.exe</c> 當 publisher：它會把無法解析的參數寫進 <b>stderr</b>
    /// （Windows 內建，不需另外安裝），正好用來驗證 stderr 幫浦與遮蔽。
    /// </summary>
    private const string NoisyStub = "findstr.exe";

    private static IPEndPoint LoopbackTarget() => new(IPAddress.Loopback, 5004);

    /// <summary>
    /// 宣告「編碼器可用」的假探測。
    /// <para>
    /// 這個檔案刻意不用真 ffmpeg，publisher 位置放的是 <c>cmd.exe</c>／<c>findstr.exe</c>——
    /// 它們沒有編碼器清單，若讓真探測去問，每一條測試都會先撞上「沒有 libx264」。
    /// 那些測試守的是重複啟動擋截與 stderr 回收，探測本身由
    /// <see cref="FfmpegEncoderProbeTests"/> 獨立驗證。
    /// </para>
    /// </summary>
    private static readonly Func<string, CancellationToken, Task<FfmpegEncoderStatus>> HasEncoder =
        (_, _) => Task.FromResult(FfmpegEncoderStatus.Available);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>
    /// ffmpeg 不存在時必須丟出可行動的例外，而不是原始的 <c>Win32Exception</c>。
    /// <para>
    /// 「伺服器上沒裝 ffmpeg」是這套功能最常見的部署失敗。若這裡丟原始 Win32Exception，
    /// 端點只能回一句「系統找不到指定的檔案」，維運不會知道要去設
    /// <c>HELIVMS_WHEP_FFMPEG</c>；而這個轉換曾經只存在於 WebApi 的
    /// <c>LivePublishers.StartFfmpeg</c>，任何未來新增的呼叫點都會再退化一次。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 找不到Ffmpeg時必須回報可行動的啟動失敗()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = MissingExecutable },
            LiveEncodeOptions.Default);

        var ex = Assert.Throws<PublisherStartException>(
            () => publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget()));

        // 訊息必須指出該動哪個設定：只有「找不到檔案」等於叫維運猜謎。
        Assert.Contains($"{WhepOptions.Prefix}FFMPEG", ex.Message, StringComparison.Ordinal);

        // 反過來也成立：訊息不能把 RTSP 帳密帶出去（錯誤訊息會進 API 回應與 log）。
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.Equal(PublisherState.Stopped, publisher.State);
    }

    /// <summary>
    /// 同一個 publisher 不可重複啟動：第二個 <c>ffmpeg</c> 會被漏在背景，成為沒人管的行程。
    /// </summary>
    [Fact]
    public async Task 同一個Publisher不可重複啟動()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = CommandInterpreter },
            LiveEncodeOptions.Default,
            HasEncoder);

        _ = publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget());

        var ex = Assert.Throws<InvalidOperationException>(
            () => publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget()));
        Assert.Contains("已在執行中", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 行程結束後必須標記 <see cref="PublisherState.Faulted"/> 並留下結束碼與 stderr。
    /// <para>
    /// 這三樣是故障時唯一的診斷線索。只標「Faulted」而沒有結束碼，維運會看到
    /// 「publisher 掛了」卻不知道是參數錯（1）、找不到輸入（-1）還是正常結束（0）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 行程結束後必須標記Faulted並留下結束碼與stderr()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = NoisyStub },
            LiveEncodeOptions.Default,
            HasEncoder);

        // 啟動前必然沒有結束碼：把「null 與 0」混為一談會讓 UI 顯示成「以 0 結束」。
        Assert.Null(publisher.ExitCode);
        _ = publisher.Start("rtsp://cam:secret@192.0.2.1/live", LoopbackTarget());

        var faulted = await WaitForAsync(() => publisher.State == PublisherState.Faulted, TimeSpan.FromSeconds(30));

        Assert.True(faulted, $"publisher 沒有在 30 秒內進入 Faulted（目前 {publisher.State}）。");

        // 結束碼必須被記下來（0 或非 0 都算成功記錄）；真正要驗「非 0」的是
        // Ffmpeg失敗時的錯誤訊息必須遮蔽Rtsp帳密，那裡用真的 ffmpeg。
        Assert.NotNull(publisher.ExitCode);

        // stderr 幫浦必須真的把外部行程的輸出收回來，否則錯誤永遠是空的——
        // 而「UI 只顯示『無法串流』、沒有任何原因」正是最難排查的症狀。
        await WaitForAsync(() => publisher.LastError.Length > 0, TimeSpan.FromSeconds(15));
        Assert.Contains("FINDSTR", publisher.LastError, StringComparison.OrdinalIgnoreCase);

        // 這條路徑同樣必須遮蔽：參數裡的 RTSP 位址會被外部行程原樣回顯。
        Assert.DoesNotContain("secret", publisher.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="LivePublisher.Start"/> 必須擋掉空白來源與 null 目標。
    /// <para>
    /// 這不是囉嗦的防禦：空白字串會讓 ffmpeg 自己去猜輸入（變成讀 stdin 或卡住），
    /// 而那正是「publisher 卡住不出畫面、又沒有錯誤」的一種。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 啟動參數必須有效()
    {
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = CommandInterpreter },
            LiveEncodeOptions.Default,
            HasEncoder);

        Assert.Throws<ArgumentException>(() => publisher.Start("   ", LoopbackTarget()));
        Assert.Throws<ArgumentNullException>(() => publisher.Start("rtsp://192.0.2.1/live", null!));
        Assert.Equal(PublisherState.Stopped, publisher.State);
    }

    /// <summary>
    /// <see cref="LivePublisher.StopAsync"/> 必須真的讓行程結束，並且清掉狀態。
    /// <para>
    /// 用 ffmpeg 指向一個關閉的埠：ffmpeg 對 RTSP 會<b>無限重連</b>，所以行程會活著等下去
    /// ——這正是「viewer 離開後 ffmpeg 永遠不退出」那類洩漏的形狀。若 <c>StopAsync</c>
    /// 只是清掉參考而不殺行程，pid 會留著。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task 停止必須真的結束行程並清掉狀態()
    {
        Skip.IfNot(PublishPipelineProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上。");

        // 這條會真的走正式啟動器，因此編碼器檢查會擋下缺 libx264 的 ffmpeg——
        // 那正是它該做的，但對這條測試而言是「環境不具備」，不是失敗。
        Skip.IfNot(PublishPipelineProbe.Libx264Available, "這個 ffmpeg 沒有 libx264。");

        var publisher = new LivePublisher(new WhepOptions { FfmpegPath = "ffmpeg" }, LiveEncodeOptions.Default);

        // 保留一個「一定沒人監聽」的埠：連不上就會停在重連迴圈裡。
        var deadPort = FreeUdpPort();
        var process = publisher.Start($"rtsp://cam:secret@127.0.0.1:{deadPort}/live", LoopbackTarget());
        var pid = process.Id;

        try
        {
            Assert.Equal(PublisherState.Starting, publisher.State);

            Assert.True(await publisher.StopAsync());
            Assert.Equal(PublisherState.Stopped, publisher.State);

            // 行程必須真的消失：只清參考的話，ffmpeg 會繼續在背景無限重連。
            var gone = await WaitForAsync(
                () =>
                {
                    try
                    {
                        using var probe = Process.GetProcessById(pid);
                        return probe.HasExited;
                    }
                    catch (ArgumentException)
                    {
                        // GetProcessById 找不到 = 行程已經結束。
                        return true;
                    }
                },
                TimeSpan.FromSeconds(20));

            Assert.True(gone, $"pid {pid} 在 StopAsync 之後還活著——ffmpeg 會無限重連，等於洩漏行程。");
        }
        finally
        {
            await publisher.DisposeAsync();
        }
    }

    /// <summary>
    /// 真實 ffmpeg 失敗時，<see cref="LivePublisher.LastError"/> 不得包含 RTSP 明文帳密。
    ///
    /// <para>
    /// 用一台假的 RTSP 伺服器回 401（帳密錯誤）來觸發：ffmpeg 在
    /// <c>-loglevel error</c> 下會把輸入位址原樣寫進 stderr，形如
    /// <c>Error opening input file rtsp://user:pass@host/live.</c>。這個字串會經由
    /// <c>/api/channels/{id}/stream</c> 的錯誤回應回到瀏覽器，也會進 log。
    /// 遮蔽一旦失效，憑證就會散落在備份與 log 裡。
    /// </para>
    ///
    /// <para>
    /// 刻意不用「連線被拒」當觸發：ffmpeg 對無法連線的 RTSP 會<b>無聲地無限重連</b>
    /// （連 <c>-loglevel info</c> 都沒有輸出），那是另一個形狀的問題——
    /// publisher 會一直停在 Starting，診斷只能靠啟動逾時。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Ffmpeg失敗時的錯誤訊息必須遮蔽Rtsp帳密()
    {
        Skip.IfNot(PublishPipelineProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上。");

        // 同上：會走正式啟動器，缺 libx264 時編碼器檢查會先擋，那是環境限制不是缺陷。
        Skip.IfNot(PublishPipelineProbe.Libx264Available, "這個 ffmpeg 沒有 libx264。");

        await using var camera = new FakeRtspServer();
        await using var publisher = new LivePublisher(
            new WhepOptions { FfmpegPath = "ffmpeg" },
            LiveEncodeOptions.Default);

        var secret = "sup3rs3cret";
        _ = publisher.Start($"rtsp://cam:{secret}@{camera.Address}:{camera.Port}/live", LoopbackTarget());

        var faulted = await WaitForAsync(() => publisher.State == PublisherState.Faulted, TimeSpan.FromSeconds(45));
        Assert.True(faulted, $"ffmpeg 沒有因 401 而退出（目前 {publisher.State}）。LastError：\n{publisher.LastError}");

        // 輸入錯誤必須以非 0 結束碼結束——0 代表「成功」，會讓維運以為 ffmpeg 正常收工。
        Assert.NotNull(publisher.ExitCode);
        Assert.NotEqual(0, publisher.ExitCode!.Value);

        // 先確認「真的抓到了那行含 URL 的錯誤」：否則後面的遮蔽斷言會空洞地通過。
        await WaitForAsync(
            () => publisher.LastError.Contains("401", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        Assert.Contains("401", publisher.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, publisher.LastError, StringComparison.Ordinal);

        // 遮蔽是「拿掉 userinfo」而不是整段刪除：維運仍需要知道是哪一台攝影機在報錯。
        var redactedUrl = $"rtsp://{camera.Address}:{camera.Port}/live";
        Assert.Contains(redactedUrl, publisher.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("cam@", publisher.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// 假的 RTSP 伺服器：接受連線後一律回 401，用來驗證帳密錯誤這條路徑。
    /// </summary>
    private sealed class FakeRtspServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeRtspServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public IPAddress Address => IPAddress.Loopback;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

                        var response = Encoding.ASCII.GetBytes("RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n\r\n");
                        await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    /// <summary>取得一個目前沒有任何人監聽的 UDP 埠。</summary>
    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}