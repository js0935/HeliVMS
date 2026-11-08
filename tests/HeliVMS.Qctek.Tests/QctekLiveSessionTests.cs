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
    }

    private sealed record FramPacket(int Channel, ushort Status, byte[] Media, byte[] Payload);

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
            var frames = new List<FramPacket>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (frames.Count < 2 && DateTime.UtcNow < deadline)
            {
                var packet = await ReadFramAsync(stream, timeout.Token);
                if (packet.Status == 0)
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
}
