using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 即時影像通道：keepalive 解析、<c>GET_LIVE_START</c> 的交棒、以及 loopback 實測的
/// 「keepalive → 開來源 → FRAM 輸出」。
/// <para>
/// 端到端用 <see cref="NemoServer"/>（臨時埠、loopback）與假的記憶體來源跑完整條鏈：
/// 這是「線路位元組 + 狀態機 + 交棒」第一次真的接起來的地方——任何一段的長度差 1，
/// QCC 端的症狀都只是「連著但沒畫面」，只有這種測試能在出貨前抓到。
/// </para>
/// </summary>
public class QctekLiveSessionTests
{
    // ── 測試用的 H.264（NAL type 正確、內容隨意）────────────────────────────

    private static byte[] SpSc() => [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1f];
    private static byte[] PpSc() => [0x00, 0x00, 0x00, 0x01, 0x68, 0xce, 0x3c, 0x80];
    private static byte[] IdrSc() => [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x11];
    private static byte[] SliceSc() => [0x00, 0x00, 0x00, 0x01, 0x41, 0x9a, 0x22, 0x33];

    private static byte[] AnnexBStream() => [.. SpSc(), .. PpSc(), .. IdrSc(), .. SliceSc()];

    // ── keepalive 解析 ─────────────────────────────────────────────────────

    private static byte[] KeepAlive(int subStream, params int[] channelIndexes)
    {
        var packet = new byte[QctekLiveSession.KeepAliveLength];
        "QCTEK_RM"u8.CopyTo(packet);
        packet[8] = 0x01;
        packet[11] = 0x01; // +0x08 起的真機慣例 01 00 00 01
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0x0c), subStream);
        foreach (var index in channelIndexes)
        {
            packet[0x10 + (index >> 3)] |= (byte)(1 << (index & 7));
        }

        return packet;
    }

    [Fact]
    public void keepalive解析讀出碼流與頻道位圖()
    {
        var ok = QctekLiveSession.TryParseKeepAlive(KeepAlive(1, 0, 2, 5), out var subStream, out var channels);

        Assert.True(ok);
        Assert.Equal(1, subStream); // 子碼流
        Assert.True(channels[0]);
        Assert.False(channels[1]);
        Assert.True(channels[2]);
        Assert.True(channels[5]);
        Assert.False(channels[1023]);
    }

    [Fact]
    public void keepalive開頭錯誤或碼流欄位為負時拒絕解析()
    {
        Assert.False(QctekLiveSession.TryParseKeepAlive(new byte[0x90], out _, out _));

        var badType = KeepAlive(-1);
        Assert.False(QctekLiveSession.TryParseKeepAlive(badType, out _, out _));

        // 位元組不夠 0x90：等下一包，不是錯誤。
        Assert.False(QctekLiveSession.TryParseKeepAlive(KeepAlive(0)[..0x8f], out _, out _));
    }

    // ── GET_LIVE_START 的交棒旗標（session 層）──────────────────────────────

    private static byte[] LiveStartRequest()
        => [.. Encoding.ASCII.GetBytes("GET_LIVE_START admin QCV8001_AUTH"), (byte)0];

    private static byte[] AuthCodeReply()
        => [.. Encoding.ASCII.GetBytes("QCV8001_AUTH_SENDCODE " + new string('a', 32)), (byte)0];

    [Fact]
    public void 即時影像開啟的成功回覆是剛好四個位元組()
    {
        var session = new QctekSession(new LiveContext());

        var challenge = session.Advance(LiveStartRequest());
        Assert.Equal(27, challenge.Reply!.Length);
        Assert.False(challenge.Live);

        var result = session.Advance(AuthCodeReply());

        // "OK "＋NUL（黃金抓包 4f4b2000）。多一個或少一個位元組，客戶端的
        // keepalive 解析就錯位——「即時影像永遠連不上」的最短解釋。
        Assert.Equal(new byte[] { 0x4f, 0x4b, 0x20, 0x00 }, result.Reply);
        Assert.True(result.Live);
    }

    [Fact]
    public void framed的即時影像開啟用FramedWithNul框法()
    {
        var session = new QctekSession(new LiveContext());

        var challenge = session.Advance(
            [.. NemoMessage.Encode(Encoding.ASCII.GetBytes("GET_LIVE_START admin QCV8001_AUTH"))]);
        Assert.NotNull(challenge.Reply);

        var result = session.Advance(AuthCodeReply());

        // 真機框法：標頭只宣告 "OK " 的 3 bytes，NUL 寫在宣告長度之外（實際 4 bytes）。
        var wire = result.Reply!;
        Assert.True(NemoMessage.TryDecode(wire, out var payload, out var consumed));
        Assert.Equal("OK "u8.ToArray(), payload);
        Assert.Equal(0, wire[consumed]); // 第 4 個位元組是 NUL
        Assert.True(result.Live);
    }

    [Fact]
    public void 已授權的連線再次開啟即時影像也會交棒()
    {
        var session = new QctekSession(new LiveContext());
        session.Advance(LiveStartRequest());
        var first = session.Advance(AuthCodeReply());
        Assert.True(first.Live);

        // 同一條連線重開即時影像（客戶端斷線重連會這樣）：命令已授權、不必再挑戰。
        var again = session.Advance(LiveStartRequest());
        Assert.Equal(new byte[] { 0x4f, 0x4b, 0x20, 0x00 }, again.Reply);
        Assert.True(again.Live);
    }

    // ── 端到端（loopback + 假來源）─────────────────────────────────────────

    private sealed class FakeLiveSource(byte[] payload) : IQctekLiveSource
    {
        private readonly MemoryStream _stream = new(payload);

        public int Disposed { get; private set; }

        public Stream Output => _stream;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LiveContext : IQctekDeviceContext
    {
        public Func<int, int, IQctekLiveSource?>? Factory { get; init; }

        public string ServerVersion => "QCV8001";

        public int ChannelCount => 1;

        public IReadOnlyList<string> ChannelNames => ["攝影機001"];

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password) => user == "admin";

        public bool IsKnownUser(string user) => user == "admin";

        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream)
            => Factory?.Invoke(channelIndex, subStream);

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }

    private sealed record FramPacket(int Channel, ushort Status, byte[] Media, byte[] Payload)
    {
        /// <summary>media 幀的 type（1＝視訊、2＝參數）；狀態幀為 0。</summary>
        public int Type => Media.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Media) : 0;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        // throwOnEndOfStream（預設 true）：對端提前關閉時拋錯，而不是讓後續斷言
        // 對著半包資料講出模糊的錯誤。
        await stream.ReadAtLeastAsync(buffer, buffer.Length, true, ct);
    }

    private static async Task<FramPacket> ReadFramAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(stream, header, ct);
        var channel = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        var status = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (status != 0)
        {
            return new FramPacket(channel, status, [], []);
        }

        var media = new byte[0x38];
        await ReadExactAsync(stream, media, ct);
        var video = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x08));
        var block1 = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x0c));
        var block2 = BinaryPrimitives.ReadInt32LittleEndian(media.AsSpan(0x10));
        var payload = new byte[block1 + block2 + video];
        await ReadExactAsync(stream, payload, ct);
        return new FramPacket(channel, status, media, payload);
    }

    /// <summary>模擬 QCC 的登入：GET_LIVE_START → 挑戰 → 認證碼 → "OK "＋NUL。</summary>
    private static async Task LoginAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync(LiveStartRequest(), ct);

        var challenge = new byte[27];
        await ReadExactAsync(stream, challenge, ct);

        await stream.WriteAsync(AuthCodeReply(), ct);

        var ok = new byte[4];
        await ReadExactAsync(stream, ok, ct);
        Assert.Equal(new byte[] { 0x4f, 0x4b, 0x20, 0x00 }, ok);
    }

    [Fact]
    public async Task 位圖要求的頻道會輸出關鍵與非關鍵影格的FRAM()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new LiveContext { Factory = (_, _) => new FakeLiveSource(AnnexBStream()) };
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        // 客戶端每 250ms 一個 keepalive（bit 0），像真 QCC 一樣。
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var keepAlives = KeepAlive(0, 0);
        var pacer = Task.Run(async () =>
        {
            try
            {
                while (!keepAliveCts.IsCancellationRequested)
                {
                    await stream.WriteAsync(keepAlives, keepAliveCts.Token);
                    await Task.Delay(250, keepAliveCts.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        try
        {
            // 假來源一次讀完：AU 分組會交出 IDR AU 與 P AU 各一個，tick 逐個輸出。
            // 音訊幀（type=2，每頻道開流時一個）不算影像幀。
            var frames = new List<FramPacket>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (frames.Count < 2 && DateTime.UtcNow < deadline)
            {
                var packet = await ReadFramAsync(stream, timeout.Token);
                if (packet.Status == 0 && packet.Type == 1)
                {
                    frames.Add(packet);
                }
            }

            Assert.Equal(2, frames.Count);

            var keyframe = frames[0];
            Assert.Equal(0, keyframe.Channel);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(keyframe.Media.AsSpan(0x00)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(keyframe.Media.AsSpan(0x04)));

            // 關鍵影格：block1 = SPS＋4-byte start code，且在 video 之前。
            Assert.Equal(SpSc(), keyframe.Payload[..SpSc().Length]);
            Assert.Equal(PpSc(), keyframe.Payload.Skip(SpSc().Length).Take(PpSc().Length).ToArray());
            Assert.Equal(IdrSc(), keyframe.Payload[^IdrSc().Length..]);

            var pFrame = frames[1];
            Assert.Equal(0, pFrame.Channel);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(pFrame.Media.AsSpan(0x0c))); // 非關鍵：block1 空
            Assert.Equal(SliceSc(), pFrame.Payload);
        }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await pacer;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task 來源缺席的頻道每秒回八位元組狀態幀()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new LiveContext { Factory = (_, _) => null }; // 頻道存在但來源開不起來
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var keepAlives = KeepAlive(0, 0);
        var pacer = Task.Run(async () =>
        {
            try
            {
                while (!keepAliveCts.IsCancellationRequested)
                {
                    await stream.WriteAsync(keepAlives, keepAliveCts.Token);
                    await Task.Delay(250, keepAliveCts.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        try
        {
            // 來源缺席：狀態幀 0x8002（攝影機資訊無效），第一個 tick（33ms）就會送。
            var packet = await ReadFramAsync(stream, timeout.Token);
            Assert.Equal(0, packet.Channel);
            Assert.Equal(0x8002, packet.Status);
            Assert.Empty(packet.Media); // 狀態幀只有 8 bytes
        }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await pacer;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task 位圖撤掉的頻道其來源會被收掉()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        FakeLiveSource? source = null;
        var context = new LiveContext { Factory = (_, _) => source = new FakeLiveSource(AnnexBStream()) };
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        // 先勾上 bit 0（等來源真的被建立），再送一個空白位圖的 keepalive。
        await stream.WriteAsync(KeepAlive(0, 0), timeout.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (source is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.NotNull(source);
        await stream.WriteAsync(KeepAlive(0), timeout.Token); // bit 全空

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (source.Disposed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.Equal(1, source.Disposed);
    }

    [Fact]
    public async Task 第一個keepalive到達前不會寫出任何位元組()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new LiveContext { Factory = (_, _) => new FakeLiveSource(AnnexBStream()) };
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        // 真機逐包證據（黃金抓包 stream 48，2026-11-14 重析）：QCC 在 "OK " 後
        // +14.5ms 送第一個 keepalive、真機再 +30ms 才送第一個 FRAM。提早送
        //（舊版「連線即推流」）的失敗實錄（stream 163）：QCC 收到資料後 3.5ms
        // 就以 RST 拒絕——它還沒送出 keepalive、還沒進入讀取狀態。這裡故意不送
        // keepalive：600ms 內必須一個位元組都收不到（舊版 ~10ms 就寫出音訊幀）。
        client.ReceiveTimeout = 600;
        var byteBeforeKeepAlive = false;
        try
        {
            var n = stream.Read(new byte[1], 0, 1);
            byteBeforeKeepAlive = n > 0;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // 逾時＝沒有位元組＝正確。
        }

        Assert.False(byteBeforeKeepAlive, "第一個 keepalive 之前不該寫出任何位元組");

        // 送出訂閱（bit 0）：通道才開始輸出，且輸出必須在寬限內到達。
        await stream.WriteAsync(KeepAlive(0, 0), timeout.Token);
        var gotVideo = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!gotVideo && DateTime.UtcNow < deadline)
        {
            var packet = await ReadFramAsync(stream, timeout.Token);
            gotVideo = packet.Status == 0 && packet.Type == 1;
        }

        Assert.True(gotVideo, "keepalive 之後應該開始輸出影像幀");
    }

    // ── 啟動突發（ffmpeg 把 RTSP 輸入緩衝一次倒出來）──────────────────────

    private static QctekAccessUnit KeyUnit(byte[] video) => new(true, SpSc(), PpSc(), video);

    private static QctekAccessUnit SliceUnit() => new(false, [], [], SliceSc());

    [Fact]
    public void 啟動突發不會把關鍵影格擠出佇列()
    {
        // 實測（VIVOTEK IP9164-HT、1080p15）：ffmpeg 建立 RTSP 工作階段後第一包是
        // SPS/PPS/IDR，1 毫秒後又一包 27 個 P-frame，兩包都遠早於第一個 33ms tick。
        // 容量小於這個突發量時，「滿了丟最舊」會先丟掉那張 IDR，QCC 從此只收到
        // 解不開的 P-frame——連線正常、畫面全黑、日誌乾淨，要等下一個 GOP。
        var queue = new LiveFrameQueue(QctekLiveSession.QueueCapacity);
        var keyframe = KeyUnit(IdrSc());
        queue.Push(keyframe);
        for (var i = 0; i < 27; i++)
        {
            queue.Push(SliceUnit());
        }

        Assert.Equal(0, queue.Dropped);
        Assert.True(queue.TryPop(out var first));
        Assert.Same(keyframe, first);
    }

    [Fact]
    public void 新的關鍵影格到達時會清掉積壓的舊影格()
    {
        // 積壓超過容量時，「丟最舊」會把唯一的解碼起點丟掉，剩下的 P-frame 解不開。
        // 新關鍵影格自己就帶著 SPS/PPS，整段舊積壓都已被它超越，直接從它重開。
        var queue = new LiveFrameQueue(4);
        for (var i = 0; i < 4; i++)
        {
            queue.Push(SliceUnit());
        }

        var keyframe = KeyUnit(IdrSc());
        queue.Push(keyframe);

        Assert.Equal(4, queue.Dropped);
        Assert.True(queue.TryPop(out var first));
        Assert.Same(keyframe, first);
    }

    [Fact]
    public async Task 啟動突發後的第一個資料幀是含SPS的關鍵影格()
    {
        // 端到端重現現場：來源一次吐出 IDR 加 27 個 P-frame（比佇列容量更早發生的
        // 那一種突發）。送給 QCC 的第一個資料幀必須是 block1 > 0 的關鍵影格——
        // QCC 只以 block1 判關鍵影格，缺 SPS 的 IDR 會被它當非關鍵丟棄，畫面永遠
        // 起不來，而且沒有任何錯誤訊息。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var burst = new List<byte>();
        burst.AddRange(AnnexBStream());
        burst.AddRange(Enumerable.Repeat(SliceSc(), 26).SelectMany(slice => slice));
        var context = new LiveContext { Factory = (_, _) => new FakeLiveSource([.. burst]) };
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var keepAlives = KeepAlive(0, 0);
        var pacer = Task.Run(async () =>
        {
            try
            {
                while (!keepAliveCts.IsCancellationRequested)
                {
                    await stream.WriteAsync(keepAlives, keepAliveCts.Token);
                    await Task.Delay(250, keepAliveCts.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        try
        {
            FramPacket? first = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (first is null && DateTime.UtcNow < deadline)
            {
                var packet = await ReadFramAsync(stream, timeout.Token);
                if (packet.Status == 0 && packet.Type == 1) // 跳過音訊幀（type=2）
                {
                    first = packet;
                }
            }

            Assert.NotNull(first);
            Assert.Equal(SpSc().Length, BinaryPrimitives.ReadInt32LittleEndian(first!.Media.AsSpan(0x0c)));
            Assert.Equal(SpSc(), first.Payload[..SpSc().Length]);
        }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await pacer;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public void 音訊幀與真機黃金抓包逐位元組一致()
    {
        // 黃金抓包的 type=2 幀（channel 0）：56B 頭 + 1000B µ-law payload。時戳欄位
        // 動態，這裡注入真機抓包同一瞬間的值做逐位元組比對。
        var unixSeconds = 0x6af5b212L; // 1773482693（抓包瞬間）
        var microSeconds = 525_000;     // 抓包瞬間的當秒微秒
        var frame = QctekLivePackets.AudioFrame(0, unixSeconds, microSeconds);

        Assert.Equal(8 + 0x38 + 1000, frame.Length);
        var media = frame.AsSpan(8, 0x38);
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media));
        Assert.Equal(200, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x04..]));
        Assert.Equal(1000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x08..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x0c..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x10..]));
        Assert.Equal(525_000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(media[0x14..]));
        Assert.Equal(0x6af5b212u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(media[0x18..]));
        Assert.Equal(0L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(media[0x20..]));
        Assert.Equal(QctekLivePackets.SilentAudioPayload.ToArray(), frame[(8 + 0x38)..]);
    }

    [Fact]
    public async Task 不送keepalive不會有推流直接送keepalive才開啟通道()
    {
        // 舊契約（已推翻的 §5c）是「真機先推流、QCC 才發 keepalive」；逐包重析
        // 黃金抓包（docs/QCC-LIVE-DIAGNOSIS.md §8）的結論相反：真機在第一個
        // keepalive 之前一個位元組都不送，提早送的實測後果是 QCC 3.5ms 內 RST。
        // 這條連線走「raw keepalive 直接交棒」路徑（交棒時 keepalive 是初始
        // 殘餘、socket 不會再有新資料——解析必須不依賴 DataAvailable）。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new LiveContext { Factory = (_, _) => new FakeLiveSource(AnnexBStream()) };
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        // 不 Login、不送 keepalive——1 秒內必須一個位元組都收不到。
        client.ReceiveTimeout = 1000;
        var streamedAnyway = false;
        try
        {
            var n = stream.Read(new byte[1], 0, 1);
            streamedAnyway = n > 0;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // 逾時＝沒有位元組＝正確。
        }

        Assert.False(streamedAnyway, "不送 keepalive 之前不該有推流");

        // 直接送訂閱（raw QCTEK_RM keepalive）：通道才開始輸出。
        await stream.WriteAsync(KeepAlive(0, 0), timeout.Token);
        var gotVideo = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!gotVideo && DateTime.UtcNow < deadline)
        {
            var packet = await ReadFramAsync(stream, timeout.Token);
            gotVideo = packet.Status == 0 && packet.Type == 1 && packet.Channel == 0;
        }

        Assert.True(gotVideo, "keepalive 之後應該開始輸出頻道 0 的影像幀");
    }

    [Fact]
    public async Task 未收到keepalive的寬限內不會被逾時斷線()
    {
        // 啟動寬限（三倍逾時≈30 秒）內，即使一個 keepalive 都沒有，連線也不能
        // 被砍——但也不能寫任何位元組（真機在第一個 keepalive 前全程靜默）。
        // 這裡觀察 3 秒：無位元組、連線活著；訂閱一到立刻開始回應（狀態幀）。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new LiveContext { Factory = (_, _) => null }; // 沒有來源：只送狀態幀
        await using var server = new NemoServer(context, IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await LoginAsync(stream, timeout.Token);

        client.ReceiveTimeout = 3000;
        var streamedAnyway = false;
        try
        {
            var n = stream.Read(new byte[1], 0, 1);
            streamedAnyway = n > 0;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // 逾時＝沒有位元組＝正確。
        }

        Assert.False(streamedAnyway, "寬限內不該有任何位元組");

        // 寬限內連線仍活著：訂閱一到，狀態幀立刻開始（來源缺席＝0x8002）。
        await stream.WriteAsync(KeepAlive(0, 0), timeout.Token);
        var packet = await ReadFramAsync(stream, timeout.Token);
        Assert.Equal(0x8002, packet.Status);
    }
}
