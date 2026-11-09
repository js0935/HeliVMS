using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// Web 埠前置多工器：QCC 連的是裝置的 Web 埠，QCV8001 在同一个埠上分辨
/// NEMO／HTTP（<c>HttpServer::PreParseRequest</c>）。HeliVMS 的 Web 埠是 Kestrel，
/// 所以公開埠由本多工器持有，再依協定分送。
/// </summary>
public class QctekPortMuxTests
{
    /// <summary>單頻道 <c>攝影機001</c>：讓 payload 與黃金抓包逐位元組相同。</summary>
    private sealed class GoldenContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => 1;

        public IReadOnlyList<string> ChannelNames => ["攝影機001"];

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password) => user == "admin";

        public bool IsKnownUser(string user) => user == "admin";

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public HeliVMS.Qctek.Live.QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public HeliVMS.Qctek.Live.IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }

    // ── 分類（純函式）──────────────────────────────────────────────────────

    [Fact]
    public void 分類認得NEMO前導框與大寫命令()
    {
        // NEMO 的 framed 命令一定有 16 bytes 標頭，開頭就是 "NEMO "；
        // raw 命令則是大寫動詞＋空白（CHECK_SERVER_VERSION、GET_NCHANNEL…）。
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(GoldenCapture.Hex(GoldenCapture.ConfigRequest)));
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(GoldenCapture.Hex(GoldenCapture.CheckServerVersionRequest)));
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(Encoding.ASCII.GetBytes("GET_NCHANNEL ")));
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(GoldenCapture.Hex(GoldenCapture.AlarmRequest)));
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(GoldenCapture.Hex(GoldenCapture.IoConfigRequest)));
    }

    [Fact]
    public void 分類認得HTTP方法()
    {
        foreach (var request in new[]
                 {
                     "GET / HTTP/1.1\r\nHost: x\r\n\r\n",
                     "POST /api/v1/events HTTP/1.1\r\n",
                     "PUT /x HTTP/1.0\r\n",
                     "HEAD / HTTP/1.1\r\n",
                     "DELETE /x HTTP/1.1\r\n",
                     "OPTIONS * HTTP/1.1\r\n",
                     "PATCH /x HTTP/1.1\r\n",
                     "TRACE /x HTTP/1.1\r\n",
                     "PRI * HTTP/2.0\r\n\r\n",
                 })
        {
            Assert.Equal(QctekMuxProtocol.Http, QctekPortMux.Classify(Encoding.ASCII.GetBytes(request)));
        }
    }

    [Fact]
    public void CONNECT依是否帶HTTP版本分辨()
    {
        // CONNECT 同時是 HTTP 方法（代理）與 NEMO 命令（帶 Base64 密碼）。
        // HTTP 的請求行一定帶 " HTTP/1.x"；NEMO 的 raw 命令以 NUL 結尾、沒有 CRLF。
        // 分辨錯的代價：把 QCC 的認證連線丟給 Kestrel（回 400 並關線、QCC 只說連不上），
        // 或把瀏覽器的代理請求丟給 NEMO（直接關線）。
        Assert.Equal(
            QctekMuxProtocol.Http,
            QctekPortMux.Classify(Encoding.ASCII.GetBytes("CONNECT example.com:443 HTTP/1.1\r\n")));
        Assert.Equal(
            QctekMuxProtocol.Nemo,
            QctekPortMux.Classify(GoldenCapture.Hex("434f4e4e4543542061646d696e20595752746157342000"))); // CONNECT admin YWRtaW4 \0
    }

    [Fact]
    public void 只收到半個動詞時會等更多位元組()
    {
        // TCP 不保證一次讀到完整的請求行。太早猜成 HTTP 會把 NEMO 命令送進 Kestrel；
        // 太早猜成 NEMO 會把 HTTP 請求交給無法解析它的狀態機。
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(ReadOnlySpan<byte>.Empty));
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(Encoding.ASCII.GetBytes("NEMO")));
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(Encoding.ASCII.GetBytes("GET")));
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(Encoding.ASCII.GetBytes("CHECK_SERVER_VERSION")));
        // CONNECT 的密碼可能短到不足以用「第二個空白」判斷，只能等 NUL／CRLF。
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(Encoding.ASCII.GetBytes("CONNECT admin YW")));
        Assert.Equal(QctekMuxProtocol.NeedMore, QctekPortMux.Classify(Encoding.ASCII.GetBytes("CONNECT admin YWRtaW4 ")));
    }

    [Fact]
    public void 非ASCII或超長動詞直接交給HTTP()
    {
        // 出現控制字元／非 ASCII 就不是我們認得的協定；動詞超過 32 bytes 也不可能是命令。
        // 交給 Kestrel（回 400 並關線）至少不會留下半開連線。
        Assert.Equal(
            QctekMuxProtocol.Http,
            QctekPortMux.Classify([0x16, 0x03, 0x01, 0x00, 0x50])); // TLS ClientHello
        Assert.Equal(
            QctekMuxProtocol.Http,
            QctekPortMux.Classify(Encoding.ASCII.GetBytes(new string('A', 40) + " x")));
    }

    // ── 實際連線（loopback TCP）─────────────────────────────────────────────

    [Fact]
    public async Task 多工器上的NEMO對話與真機逐位元組一致()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mux = new QctekPortMux(new GoldenContext(), httpPort: UnusedHttpPort());
        mux.Start();

        // ① raw 版本查詢：QCC 加裝置的第一個命令。
        using (var probe = new TcpClient())
        {
            await probe.ConnectAsync(IPAddress.Loopback, mux.Port, timeout.Token);
            var stream = probe.GetStream();
            await stream.WriteAsync(GoldenCapture.Hex(GoldenCapture.CheckServerVersionRequest), timeout.Token);

            var reply = new byte[GoldenCapture.Hex(GoldenCapture.CheckServerVersionReply).Length];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal(GoldenCapture.Hex(GoldenCapture.CheckServerVersionReply), reply);
        }

        // ② framed 設定查詢：挑戰 → 認證碼 → 312 bytes 二進位（與黃金抓包相同）。
        using (var config = new TcpClient())
        {
            await config.ConnectAsync(IPAddress.Loopback, mux.Port, timeout.Token);
            var stream = config.GetStream();

            await stream.WriteAsync(GoldenCapture.Hex(GoldenCapture.ConfigRequest), timeout.Token);

            // 挑戰是每次連線現產的（16 字元 ＋ 空白 ＋ 10 位 Unix 秒，固定 27 bytes，無 NUL）。
            var challenge = new byte[QctekChallenge.TotalLength];
            await stream.ReadExactlyAsync(challenge, timeout.Token);
            Assert.Matches(
                "^[A-Za-z0-9+/]{16} [0-9]{10}$",
                Encoding.ASCII.GetString(challenge));

            await stream.WriteAsync(GoldenCapture.Hex(GoldenCapture.ConfigSendCode), timeout.Token);

            var (header, payload) = await ReadFrameAsync(stream, timeout.Token);
            Assert.Equal("NEMO 312        ", header); // 312 ＝ 0x18 ＋ 0x120
            Assert.Equal(GoldenCapture.ConfigPayload, payload);

            // ③ 同一個連線上換一個 framed 命令：GET_IOCONFIG 不需要挑戰。
            await stream.WriteAsync(GoldenCapture.Hex(GoldenCapture.IoConfigRequest), timeout.Token);
            var (ioHeader, ioPayload) = await ReadFrameAsync(stream, timeout.Token);
            Assert.Equal("NEMO 280        ", ioHeader);
            Assert.Equal(GoldenCapture.IoConfigPayload, ioPayload);
        }
    }

    [Fact]
    public async Task HTTP請求會原封不動轉給後端並帶回回應()
    {
        // 這是「首頁／API 還在」的保證：多工器只搶公開埠，HTTP 必須完整送到 Kestrel。
        // 分類時先讀掉的位元組沒有補送的話，後端收到的是半個請求行（400），
        // 而畫面上只有一個空白頁。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            using var server = await upstream.AcceptTcpClientAsync(timeout.Token);
            var stream = server.GetStream();
            var buffer = new byte[4096];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer, timeout.Token);
                if (n == 0)
                {
                    break;
                }

                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }

            received.TrySetResult(text.ToString());
            var body = Encoding.ASCII.GetBytes("hello");
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        });

        await using var mux = new QctekPortMux(new GoldenContext(), upstreamPort);
        mux.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, mux.Port, timeout.Token);
        var clientStream = client.GetStream();

        // 分兩段寫：強迫多工器在分類緩衝上等更多位元組（真實網路一定長這樣）。
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes("GET /probe HT"), timeout.Token);
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes("TP/1.1\r\nHost: x\r\n\r\n"), timeout.Token);

        var responseText = await ReadToEndAsync(clientStream, timeout.Token);
        Assert.Contains("HTTP/1.1 200 OK", responseText, StringComparison.Ordinal);
        Assert.EndsWith("hello", responseText, StringComparison.Ordinal);

        var requestText = await received.Task.WaitAsync(timeout.Token);
        Assert.StartsWith("GET /probe HTTP/1.1\r\n", requestText, StringComparison.Ordinal);
        Assert.Contains("Host: x", requestText, StringComparison.Ordinal);

        await serve;
    }

    [Fact]
    public async Task 無法判定的資料會被關線而不是卡住()
    {
        // 既不是 NEMO 命令也不是 HTTP 方法的一坨垃圾：猜成 NEMO 由狀態機關線
        // （不猜重新同步點）。留著半開連線會讓對端等到自己超時。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var mux = new QctekPortMux(new GoldenContext(), httpPort: UnusedHttpPort());
        mux.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, mux.Port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("garbage \x01\x02"), timeout.Token);

        var probe = new byte[16];
        var read = await stream.ReadAsync(probe, timeout.Token);
        Assert.Equal(0, read);
    }

    /// <summary>內部 Kestrel 的埠：本測試不用它，取一個空閒號碼即可（多工器只轉發才連）。</summary>
    private static int UnusedHttpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(string Header, byte[] Payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[NemoMessage.HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = int.Parse(
            Encoding.ASCII.GetString(header, 5, NemoMessage.HeaderLength - 5).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return (Encoding.ASCII.GetString(header), payload);
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, cancellationToken);
            if (n == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, n);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
