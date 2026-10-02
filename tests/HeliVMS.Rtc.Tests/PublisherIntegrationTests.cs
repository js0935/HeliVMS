using System.Globalization;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 用<b>真的 ffmpeg</b> 驗證「publisher 啟動 → RTP 進來 → 服務就緒 → 轉給觀看者」的整條路徑。
///
/// <para>
/// 為什麼需要這一層：<see cref="PublishPipelineTests"/> 只驗到 ffmpeg 產出 RTP 與
/// <see cref="RtpIngest"/> 解析；<see cref="LiveStreamServiceTests"/> 則全部注入假實作。
/// 中間那一段——正式環境真正使用的 <see cref="PublisherStarters.StartFfmpeg"/>、
/// <see cref="LivePublisher.Start"/>、以及 <see cref="ChannelRuntime"/> 等待第一個封包的
/// 邏輯——此前完全沒有被執行過。參數拼錯正是發生在中間那一段，而症狀只會是「沒畫面」。
/// </para>
///
/// <para>
/// 「攝影機」用 <see cref="FakeRtspCamera"/>（測試內自建的極簡 RTSP 伺服器）加上 ffmpeg
/// 事先產生的 raw H.264：如此就能走完<b>同一條正式參數</b>，包含
/// <c>-rtsp_transport tcp</c> 與真實的 RTSP 握手，而不需要真的攝影機。
/// </para>
///
/// <para>
/// 環境沒有 ffmpeg（或沒有 libx264）時略過。
/// </para>
/// </summary>
public sealed class PublisherIntegrationTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>記錄轉送內容的假觀看者。</summary>
    private sealed class RecordingPeer : IWhepPeer
    {
        private int _forwards;

        public int Forwards => Volatile.Read(ref _forwards);

        public byte? LastPayloadType { get; private set; }

        public int LastSequence { get; private set; }

        public Task<WhepAnswer> NegotiateAsync(string offerSdp, CancellationToken token)
            => Task.FromResult(new WhepAnswer("v=0\r\ns=answer\r\n"));

        public void Forward(RtpHeader header, ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref _forwards);
            LastPayloadType = header.PayloadType;
            LastSequence = header.SequenceNumber;
        }

        public void Close(string reason)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 產生一份短 raw H.264（Annex-B）檔當攝影機的串流內容。
    /// </summary>
    private static byte[] CreateCameraStream(string directory)
    {
        var path = Path.Combine(directory, "camera.h264");
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "lavfi",
            "-i", "testsrc=size=320x240:rate=15",
            "-t", "2",
            "-an",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-profile:v", "baseline",
            "-pix_fmt", "yuv420p",
            "-g", "15",
            "-f", "h264",
            path,
        };

        using var ffmpeg = PublishPipelineProbe.StartProbe([.. args]);
        var stderr = ffmpeg.StandardError.ReadToEnd();
        ffmpeg.WaitForExit(60_000);

        Assert.True(ffmpeg.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0,
            $"無法產生測試用的 H.264 檔（exit={ffmpeg.ExitCode}）。stderr：\n{stderr}");
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// 正式啟動器 + 正式 publisher + 正式 ingest：viewer 加入後必須真的拿到 H.264 RTP。
    /// </summary>
    [SkippableFact]
    public async Task 正式啟動器必須讓第一個封包到達觀看者()
    {
        Skip.IfNot(PublishPipelineProbe.FfmpegAvailable, "ffmpeg 不在 PATH 上。");
        Skip.IfNot(PublishPipelineProbe.Libx264Available, "這個 ffmpeg 沒有 libx264。");

        var directory = Directory.CreateTempSubdirectory("helivms-publisher-it");
        try
        {
            var stream = CreateCameraStream(directory.FullName);

            // 讓 ffmpeg 自己把這段 H.264 編成 RTP 封包並錄下來，再由假攝影機以
            // interleaved TCP 重播。RTP 的分片與 marker bit 因此完全由 ffmpeg 產生，
            // 這份測試基礎設施只需負責 RTSP 握手與 framing。
            var (rtp, sprop) = await FakeRtspCamera.RecordRtpAsync(stream, seconds: 4);
            Assert.NotEmpty(rtp);

            await using var camera = new FakeRtspCamera(rtp, sprop, frameRate: 15);

            var options = new WhepOptions { PublisherStartTimeout = StartTimeout };
            var store = new WhepSessionStore();
            var peer = new RecordingPeer();

            // 用正式的 PublisherStarter，而不是測試裡的 lambda：這段接線正是本測試要守的東西。
            await using var live = new LiveStreamService(options, store, PublisherStarters.StartFfmpeg);

            var (session, answer) = await live.OpenAsync(
                new ChannelSource(11, $"rtsp://127.0.0.1:{camera.Port}/live"),
                "v=0\r\n",
                () => peer,
                CancellationToken.None);

            try
            {
                Assert.Equal("v=0\r\ns=answer\r\n", answer.Answer);
                Assert.Equal(11, session.ChannelId);

                // 通道必須被視為「正在發佈」，這是 UI 顯示「直播中」的依據。
                Assert.True(live.Describe(11).Active, "viewer 已經加入，通道卻不是 active。");

                // OpenAsync 只有在第一個 RTP 封包到達後才會返回，所以這裡一定已經有畫面資料。
                var forwarded = await WaitForAsync(() => peer.Forwards > 0, TimeSpan.FromSeconds(20));
                Assert.True(forwarded, $"沒有任何封包被轉給觀看者（runtime 收到 {live.Describe(11).Viewers} 位）。");

                // PT 必須與 WHEP answer 宣告的一致：不一致瀏覽器端就解不出畫面。
                Assert.Equal(LiveEncodeOptions.PayloadType, peer.LastPayloadType!.Value);
                Assert.True(peer.LastSequence >= 0);

                // 來源必須真的被取流（假攝影機有送出封包），否則這條測試可能只是自己送自己。
                Assert.True(camera.PacketsSent > 0, "假攝影機一個封包都沒送出，測試等於沒驗到串流。");

                // 關閉 viewer 後名額必須立刻釋放（publisher 的 idle 回收由維護迴圈負責）。
                Assert.NotNull(live.Close(session.Id));
                Assert.Equal(0, live.Describe(11).Viewers);
            }
            finally
            {
                live.Close(session.Id);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 正式啟動器必須把「ffmpeg 沒裝」變成可回應的啟動失敗，而不是讓例外往上炸成 500。
    /// </summary>
    [Fact]
    public async Task 正式啟動器必須把缺少ffmpeg轉成啟動失敗()
    {
        var options = new WhepOptions
        {
            FfmpegPath = @"C:\helivms-not-a-real-dir\ffmpeg-missing.exe",
            PublisherStartTimeout = TimeSpan.FromSeconds(5),
        };

        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(options, store, PublisherStarters.StartFfmpeg);

        var ex = await Assert.ThrowsAsync<PublisherStartException>(
            () => live.OpenAsync(
                new ChannelSource(3, "rtsp://cam:secret@192.0.2.1/live"),
                "v=0\r\n",
                () => new RecordingPeer(),
                CancellationToken.None));

        Assert.Contains($"{WhepOptions.Prefix}FFMPEG", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);

        // 啟動失敗必須留下乾淨的狀態：通道不該被標為發佈中，viewer 名額也不能被佔住。
        Assert.False(live.Describe(3).Active);
        Assert.Equal(0, live.Describe(3).Viewers);

        // 再試一次仍然要得到同樣可理解的錯誤（而不是「已在執行中」之類的殘留狀態）。
        var again = await Assert.ThrowsAsync<PublisherStartException>(
            () => live.OpenAsync(
                new ChannelSource(3, "rtsp://cam:secret@192.0.2.1/live"),
                "v=0\r\n",
                () => new RecordingPeer(),
                CancellationToken.None));
        Assert.Contains($"{WhepOptions.Prefix}FFMPEG", again.Message, StringComparison.Ordinal);
    }

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
}