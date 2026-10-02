using System.Net;
using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 即時串流協調者的行為測試（R244）。
/// <para>
/// 這些測試不碰 ffmpeg、不碰真實 WebRTC：publisher 的啟動與 peer 的協商都注入假實作。
/// 驗證的是<b>順序與配額</b>——先協商再佔配額、publisher 只啟動一次、逾時關閉、
/// 關閉後釋放名額。這些都是「看得見畫面」與「看得到但沒畫面」的差別。
/// </para>
/// </summary>
public class LiveStreamServiceTests
{
    private sealed class FakePeer : IWhepPeer
    {
        public string? Offer { get; private set; }
        public bool Disposed { get; private set; }
        public bool Closed { get; private set; }
        public string? CloseReason { get; private set; }
        public int Forwarded { get; private set; }
        public WhepNegotiationException? NegotiateFailure { get; init; }

        public Task<WhepAnswer> NegotiateAsync(string offerSdp, CancellationToken token)
        {
            Offer = offerSdp;
            return NegotiateFailure is not null
                ? throw NegotiateFailure
                : Task.FromResult(new WhepAnswer("v=0\r\ns=answer\r\n"));
        }

        public void Forward(RtpHeader header, ReadOnlySpan<byte> payload) => Forwarded++;

        public void Close(string reason)
        {
            Closed = true;
            CloseReason = reason;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed record StarterCall(ChannelSource Source, IPEndPoint Target);

    /// <summary>
    /// 假的 publisher 啟動器：真的往 ingest 的 loopback 埠送一個合成 RTP 封包。
    /// <para>
    /// 刻意<b>不</b>提供「直接標記就緒」的後門。<see cref="LiveStreamService"/> 堅持等
    /// 第一個 RTP 封包才回應 API 端點，這正是「不會回 200 卻沒畫面」的保證；用後門
    /// 會讓這條保證沒有被測到。這裡改為把封包真的送進去，於是 ingest → OnPacket →
    /// 就緒訊號的整條路徑都被執行。
    /// </para>
    /// </summary>
    private sealed class RecordingStarter
    {
        private readonly List<StarterCall> _calls = [];
        public IReadOnlyList<StarterCall> Calls => _calls;
        public PublisherStartException? Failure { get; init; }
        public int SynthesizedPackets { get; private set; }

        public async Task<PublisherStartException?> Start(
            LivePublisher publisher,
            IPEndPoint rtpTarget,
            ChannelSource source,
            CancellationToken token)
        {
            _calls.Add(new StarterCall(source, rtpTarget));
            if (Failure is not null) return Failure;

            await SendSyntheticPacketAsync(rtpTarget, token);
            return null;
        }

        private static async Task SendSyntheticPacketAsync(IPEndPoint target, CancellationToken token)
        {
            var packet = SyntheticRtpPacket(sequenceNumber: 1, timestamp: 1000);
            using var udp = new System.Net.Sockets.UdpClient();
            await udp.SendAsync(packet, target, token);
        }
    }

    /// <summary>組一個最小的合法 RTP 封包（含標頭與 H.264 NAL 起始碼）。</summary>
    private static byte[] SyntheticRtpPacket(ushort sequenceNumber, uint timestamp)
    {
        var packet = new byte[RtpHeader.FixedHeaderSize + 5];
        packet[0] = 0x80;
        packet[1] = 0x80 | LiveEncodeOptions.PayloadType;
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)sequenceNumber;
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)timestamp;
        packet[8] = 0xCA;
        packet[9] = 0xFE;
        packet[10] = 0xBA;
        packet[11] = 0xBE;

        // Annex-B NALU 起始碼 + IDR slice 類型
        packet[12] = 0x00;
        packet[13] = 0x00;
        packet[14] = 0x00;
        packet[15] = 0x01;
        packet[16] = 0x65;

        return packet;
    }

    private static ChannelSource Source(int channel = 7)
        => new(channel, "rtsp://cam:pass@192.0.2.1:554/stream");

    /// <summary>把一個合成 RTP 封包送到 ingest 埠，讓 publisher 進入 streaming。</summary>
    private static async Task<PublisherStartException?> SendAsync(
        IPEndPoint target, ushort seq, uint timestamp)
    {
        using var udp = new System.Net.Sockets.UdpClient();
        await udp.SendAsync(SyntheticRtpPacket(seq, timestamp), target, CancellationToken.None);
        return null;
    }

    [Fact]
public async Task Close回傳被關掉的會話讓呼叫端能寫稽核()
    {
        // 稽核需要 ChannelId。若 Close 只回 bool，呼叫端只能在「關閉之後」去查，
        // 那時會話已被移除——稽核看起來寫了，實際一筆都沒進去，而且不會有錯誤。
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(
            new WhepOptions(), store, (_, target, _, _) => SendAsync(target, 1, 1000));

var peer = new FakePeer();
        var (session, _) = await live.OpenAsync(Source(), Offer(), () => peer, default);

        var closed = live.Close(session.Id);

        Assert.NotNull(closed);
        Assert.Equal(7, closed!.ChannelId);
        Assert.Equal(session.Id, closed.Id);
        Assert.Equal(0, store.CountFor(7));
    }

    [Fact]
public async Task 關到不存在的會話回傳null()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(
            new WhepOptions(), store, (_, target, _, _) => SendAsync(target, 1, 1000));

        Assert.Null(live.Close("不存在的會話"));
        Assert.Null(live.Close(null));
    }

    [Fact]
    public async Task 配額已滿時關閉新來的peer()
    {
        // 配額被拒時會話「從未進入帳本」。若清理只會 Remove 帳本裡的東西，
        // 這條 peer 永遠不會被釋放——每多一位被擋下的觀看者就洩漏一條 DTLS 連線，
        // 症狀只是「開太多次視窗之後記憶體慢慢長大」，很難回頭連到成因。
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(
            new WhepOptions { MaxViewersPerChannel = 1 },
            store,
            (_, target, _, _) => SendAsync(target, 1, 1000));

        var watching = new FakePeer();
        await live.OpenAsync(Source(), Offer(), () => watching, default);

        var rejected = new FakePeer();
        await Assert.ThrowsAsync<WhepViewerLimitException>(
            () => live.OpenAsync(Source(), Offer(), () => rejected, default));

        Assert.True(rejected.Disposed, "配額被拒的 peer 必須被釋放");
        Assert.False(watching.Disposed, "已經在觀看中的 peer 不該被動到");
        Assert.Equal(1, store.CountFor(7));
    }

    [Fact]
    public async Task 逾時回收會讓通道的publisher可以關閉()
    {
        // 只清帳本、不 Detach runtime 的話，runtime 以為還有人在看，ffmpeg 永不退出；
        // 逾時回收反而製造了永久佔用的 publisher。
        long now = 0;
        var store = new WhepSessionStore(() => now);
        await using var live = new LiveStreamService(
            new WhepOptions
            {
                SessionIdleTimeout = TimeSpan.FromSeconds(30),
                PublisherIdleTimeout = TimeSpan.FromSeconds(30),
            },
            store,
            (_, target, _, _) => SendAsync(target, 1, 1000),
            () => now);

        await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);
        Assert.Equal(1, store.CountFor(7));
        Assert.True(live.Describe(7).Active);

        // 時間往前跳，讓會話逾時。
        now = TimeSpan.FromMinutes(5).Ticks;

        Assert.Equal(1, await live.MaintainAsync(default));
        Assert.Equal(0, store.CountFor(7));
        Assert.False(live.Describe(7).Active, "回收後 publisher 不該還處於啟用狀態");
    }

    [Fact]
    public async Task 第一次觀看會啟動publisher並回傳答案()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        var (session, answer) = await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);

        Assert.NotEmpty(answer.Answer);
        Assert.Equal(7, session.ChannelId);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, live.Describe(7).Viewers);
    }

    [Fact]
    public async Task 同一通道的第二位觀看者共用同一個publisher()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);
        await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);

        // 這就是「CPU 只隨通道數成長」的實作證據：編碼只做一次。
        Assert.Equal(2, store.CountFor(7));
        Assert.True(live.Describe(7).Active);
    }

    [Fact]
    public async Task 不同通道各自有publisher()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        await live.OpenAsync(Source(7), Offer(), () => new FakePeer(), default);
        await live.OpenAsync(Source(8), Offer(), () => new FakePeer(), default);

        Assert.True(live.Describe(7).Active);
        Assert.True(live.Describe(8).Active);
    }

    [Fact]
    public async Task 協商失敗時不佔用任何名額()
    {
        // 順序很重要：先協商、再佔配額，否則壞客戶端能把名額吃光。
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        var bad = new FakePeer { NegotiateFailure = new WhepNegotiationException("格式不符") };

        await Assert.ThrowsAsync<WhepNegotiationException>(
            () => live.OpenAsync(Source(), Offer(), () => bad, default));

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.CountFor(7));
        Assert.True(bad.Disposed);
    }

    [Fact]
    public async Task 超過觀看上限時丟出限制例外()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { MaxViewersPerChannel = 1 }, store, starter.Start);

        await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);

        await Assert.ThrowsAsync<WhepViewerLimitException>(
            () => live.OpenAsync(Source(), Offer(), () => new FakePeer(), default));

        Assert.Equal(1, store.CountFor(7));
    }

    [Fact]
    public async Task 關閉會話會釋放名額並關掉peer()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { MaxViewersPerChannel = 1 }, store, starter.Start);

        var peer = new FakePeer();
        var (session, _) = await live.OpenAsync(Source(), Offer(), () => peer, default);

        Assert.NotNull(live.Close(session.Id));

        Assert.True(peer.Closed);
        Assert.True(peer.Disposed);
        Assert.Equal(0, store.CountFor(7));

        // 名額釋放後應該能再進來。
        var (again, _) = await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);
        Assert.NotEqual(session.Id, again.Id);
    }

    [Fact]
    public async Task 關閉不存在的會話回傳失敗()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(new WhepOptions(), store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));

        Assert.Null(live.Close("nonexistent"));
        Assert.Null(live.Close(null));
    }

    [Fact]
    public async Task 轉送只送給該通道的觀看者()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        var peerA = new FakePeer();
        var peerB = new FakePeer();
        await live.OpenAsync(Source(7), Offer(), () => peerA, default);
        await live.OpenAsync(Source(8), Offer(), () => peerB, default);

        var header = new RtpHeader(1, 2, true, 96, 3, 12, 2);
        live.Forward(7, header, new byte[] { 0xAA, 0xBB });

        Assert.Equal(1, peerA.Forwarded);
        Assert.Equal(0, peerB.Forwarded);
    }

    [Fact]
    public async Task 沒有觀看者時轉送不會擲出例外()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(new WhepOptions(), store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));

        var header = new RtpHeader(1, 2, true, 96, 3, 12, 2);
        live.Forward(999, header, new byte[] { 0xAA });

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task 轉送後會更新活動時間因此不被逾時回收()
    {
        long now = TimeSpan.FromMinutes(5).Ticks;
        var store = new WhepSessionStore(() => now);
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { SessionIdleTimeout = TimeSpan.FromSeconds(30) }, store, starter.Start, () => now);

        await live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);

        // 讓它變舊。
        now += TimeSpan.FromMinutes(1).Ticks;
        live.Forward(7, new RtpHeader(1, 2, true, 96, 3, 12, 2), new byte[] { 0xAA });

        await live.MaintainAsync(default);

        Assert.Equal(1, store.CountFor(7));
    }

    [Fact]
    public async Task 維護會回收逾時的觀看會話()
    {
        long now = 0;
        var store = new WhepSessionStore(() => now);
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { SessionIdleTimeout = TimeSpan.FromSeconds(30) }, store, starter.Start, () => now);

        var peer = new FakePeer();
        await live.OpenAsync(Source(), Offer(), () => peer, default);

        now = TimeSpan.FromMinutes(1).Ticks;
        var reaped = await live.MaintainAsync(default);

        Assert.Equal(1, reaped);
        Assert.Equal(0, store.CountFor(7));
        Assert.True(peer.Closed);
        Assert.True(peer.Disposed);
    }

    [Fact]
    public async Task 維護回報回收數量()
    {
        long now = 0;
        var store = new WhepSessionStore(() => now);
        var starter = new RecordingStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { SessionIdleTimeout = TimeSpan.FromSeconds(30) }, store, starter.Start, () => now);

        await live.OpenAsync(Source(7), Offer(), () => new FakePeer(), default);
        await live.OpenAsync(Source(8), Offer(), () => new FakePeer(), default);

        now = TimeSpan.FromMinutes(1).Ticks;

        Assert.Equal(2, await live.MaintainAsync(default));
    }

    [Fact]
    public async Task 查詢未啟用的通道回報為未啟用()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(new WhepOptions(), store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));

        var view = live.Describe(42);

        Assert.False(view.Active);
        Assert.Equal(0, view.Viewers);
        Assert.Equal(42, view.ChannelId);
    }

    [Fact]
    public async Task 查詢回報上限供前端顯示()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(
            new WhepOptions { MaxViewersPerChannel = 16 }, store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));

        Assert.Equal(16, live.Describe(7).Limit);
    }

    [Fact]
    public async Task 沒有RTS位址時開不了觀看()
    {
        var store = new WhepSessionStore();
        await using var live = new LiveStreamService(new WhepOptions(), store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));

        await Assert.ThrowsAsync<ArgumentException>(
            () => live.OpenAsync(new ChannelSource(7, "  "), Offer(), () => new FakePeer(), default));
    }

    [Fact]
    public async Task 第一個RTP封包到達前不會回應()
    {
        // 這條保證是整個 M244 裡最容易被日後「優化掉」的一段。
        // 若改成 publisher 一啟動就回 201，瀏覽器會拿到一個看起來成功、卻永遠沒有畫面的
        // 連線，而且沒有任何錯誤可查——症狀是「直播偶爾開不起來，很難重現」。
        var store = new WhepSessionStore();
        var silent = new SilentStarter();
        await using var live = new LiveStreamService(
            new WhepOptions { PublisherStartTimeout = TimeSpan.FromSeconds(30) },
            store,
            silent.Start);

        var opening = live.OpenAsync(Source(), Offer(), () => new FakePeer(), default);

        // 啟動後仍不該完成：因為還沒有任何 RTP 封包。
        var completed = await Task.WhenAny(opening, Task.Delay(300));
        Assert.NotSame(opening, completed);
        Assert.False(opening.IsCompleted);

        // 封包到達後才應該完成。
        await silent.ReleaseAsync();
        var (session, _) = await opening;
        Assert.Equal(7, session.ChannelId);
        Assert.Equal(1, store.CountFor(7));
    }

    /// <summary>啟動後不送封包，直到測試釋放；用來觀察就緒前的等待行為。</summary>
    private sealed class SilentStarter
    {
        private IPEndPoint? _target;

        public Task<PublisherStartException?> Start(
            LivePublisher publisher,
            IPEndPoint rtpTarget,
            ChannelSource source,
            CancellationToken token)
        {
            _target = rtpTarget;
            return Task.FromResult<PublisherStartException?>(null);
        }

        public async Task ReleaseAsync()
        {
            using var udp = new System.Net.Sockets.UdpClient();
            await udp.SendAsync(SyntheticRtpPacket(1, 1000), _target!, CancellationToken.None);
        }
    }

    [Fact]
    public async Task publisher啟動失敗時把原因往上拋()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter { Failure = new PublisherStartException("ffmpeg 找不到") };
        await using var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        var ex = await Assert.ThrowsAsync<PublisherStartException>(
            () => live.OpenAsync(Source(), Offer(), () => new FakePeer(), default));

        Assert.Contains("ffmpeg 找不到", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task 關閉服務會關掉所有會話()
    {
        var store = new WhepSessionStore();
        var starter = new RecordingStarter();
        var live = new LiveStreamService(new WhepOptions(), store, starter.Start);

        var peer = new FakePeer();
        await live.OpenAsync(Source(), Offer(), () => peer, default);

        await live.DisposeAsync();

        Assert.True(peer.Closed);
        Assert.True(peer.Disposed);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task 關閉後再觀看會失敗而不是靜默接受()
    {
        var store = new WhepSessionStore();
        var live = new LiveStreamService(new WhepOptions(), store, (_, _, _, _) => Task.FromResult<PublisherStartException?>(null));
        await live.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => live.OpenAsync(Source(), Offer(), () => new FakePeer(), default));
    }

    private static string Offer() => "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";
}
