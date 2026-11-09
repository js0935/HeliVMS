using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>NEMO 伺服器：loopback TCP 實測分幀、回應與容錯。</summary>
public class NemoServerTests
{
    private sealed class TestContext : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => 8;

        public IReadOnlyList<string> ChannelNames
            => Enumerable.Range(1, 8).Select(static i => $"攝影機{i:000}").ToList();

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password)
            => (user, password) is ("admin", "s3cret");

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

    [Fact]
    public async Task 伺服器回應版本查詢且標頭與QCV8001逐字一致()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(NemoMessage.EncodeText("CHECK_SERVER_VERSION"), timeout.Token);

        var (header, payload) = await ReadFrameAsync(stream, timeout.Token);

        // 標頭逐字：「NEMO 11」＋9 個空白補滿 16 bytes；payload 尾端空白是協定慣例。
        Assert.Equal("NEMO 11         ", header);
        Assert.Equal("OK QCV8001 ", payload);

        // 真機把 NUL 寫在宣告長度之外（實際 28 bytes，標頭只宣告 11）——這個
        // 少宣告的 1 是線路格式的一部分，不是我們多送。
        var extra = new byte[1];
        await stream.ReadExactlyAsync(extra, timeout.Token);
        Assert.Equal(0, extra[0]);
    }

    [Fact]
    public async Task 同一連線連續兩個請求都能得到回應()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        // 兩個訊框「同一次寫入」：驗證伺服器會依標頭切分，而不是一個請求配一次讀取。
        var frames = NemoMessage.EncodeText("GET_NCHANNEL")
            .Concat(NemoMessage.EncodeText("GET_MAX_STREAM_NUM"))
            .ToArray();
        await stream.WriteAsync(frames, timeout.Token);

        Assert.Equal("OK 8 ", (await ReadFrameAsync(stream, timeout.Token)).Payload);
        Assert.Equal("OK 2 ", (await ReadFrameAsync(stream, timeout.Token)).Payload);
    }

    [Fact]
    public async Task 未知命令不回覆而後續命令照常回應()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        // 用一個真的不存在的命令名：SEARCH_EVENT 現在是已知命令（不可解析的查詢會回
        // 「OK 0」），拿它當未知命令的例子會讓這條測試失去意義。
        var frames = NemoMessage.EncodeText("NOT_A_REAL_COMMAND 1 2 3")
            .Concat(NemoMessage.EncodeText("ALARM_ISALIVE"))
            .ToArray();
        await stream.WriteAsync(frames, timeout.Token);

        // 未知命令不回應（不猜內容）；下一個命令的回應直接接上。
        Assert.Equal("ALIVE ", (await ReadFrameAsync(stream, timeout.Token)).Payload);
    }

    [Fact]
    public async Task 心跳命令只記錄第一次且不會吃掉下一條命令的回應()
    {
        // QCC 實測：一個工作階段送 1,341 次 ALARM_HELLO（每 5 秒一次）。這份診斷日誌
        // 是 512 KB 滾動檔，每次都記會把真正的線索（連線失敗原因、未知命令、live 診斷）
        // 整段擠掉，症狀反而變成「日誌乾淨」。兩件事一起釘住：
        // (1) 心跳真的不回位元組——否則客戶端會錯位一格（連得上、命令送得出、畫面全空）；
        // (2) 心跳不被記成「未知命令」，避免下一次有人去補一個不該存在的回應。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        var frames = Enumerable.Range(0, 3)
            .SelectMany(_ => NemoMessage.EncodeText("ALARM_HELLO 1 =QCV8001"))
            .Concat(NemoMessage.EncodeText("CHECK_SERVER_VERSION CLIENT_AUTH"))
            .ToArray();
        await stream.WriteAsync(frames, timeout.Token);

        // 心跳不回覆，所以第一個收到的訊框必須是版本查詢的回應（錯位就會是心跳的回應）。
        Assert.Equal("OK QCV8001 ", (await ReadFrameAsync(stream, timeout.Token)).Payload);

        var lines = log.ToArray();
        Assert.Equal(1, lines.Count(l => l.Contains("心跳", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains("未知命令", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CMS完整對話序列逐項正確()
    {
        // 依 NVDLL（CMS 端）反編譯的實際請求序列，逐項演一遍「QCC 加一台新裝置」：
        // CHECK_SERVER_VERSION CLIENT_AUTH → CONNECT <user> <b64(pass)> → GET_NCHANNEL
        // → ALARM_REQUEST … → ALARM_CANCEL。這條測試讓 CI 不必開 QCC 也能釘住整段對話。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        async Task<string> RoundTripAsync(string request, bool trailingNul = false)
        {
            await stream.WriteAsync(NemoMessage.EncodeText(request), timeout.Token);
            var reply = await ReadFrameAsync(stream, timeout.Token);
            if (trailingNul)
            {
                // 真機的 framed 文字回應把 NUL 寫在標頭宣告長度之外（標頭少宣告 1）；
                // 同一條連線上不把這個 NUL 吃掉，下一個標頭就會從它開始解析。
                var extra = new byte[1];
                await stream.ReadExactlyAsync(extra, timeout.Token);
                Assert.Equal(0, extra[0]);
            }

            return reply.Payload;
        }

        var pass = QctekPassword.Encode("s3cret");
        Assert.Equal("OK QCV8001 ", await RoundTripAsync("CHECK_SERVER_VERSION CLIENT_AUTH", trailingNul: true));
        Assert.Equal("OK -1 -1 -1 ", await RoundTripAsync($"CONNECT admin {pass} "));
        Assert.Equal("OK 8 ", await RoundTripAsync("GET_NCHANNEL"));
        Assert.Equal("OK 0 1 ", await RoundTripAsync(
            $"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001"));
        Assert.Equal("OK ", await RoundTripAsync("ALARM_CANCEL 1 1 ", trailingNul: true));
    }

    [Fact]
    public async Task 命令與回應都會寫進診斷記錄()
    {
        // QCC 實測失敗時（連不上、命令不認識、密碼錯，畫面上長得一樣）這份日誌
        // 是唯一的線索——連線、收到的每個命令、回應、與「未知命令」都必須留痕。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(NemoMessage.EncodeText("CHECK_SERVER_VERSION CLIENT_AUTH"), timeout.Token);
        _ = await ReadFrameAsync(stream, timeout.Token);
        await stream.WriteAsync(NemoMessage.EncodeText("WEIRD_UNKNOWN_CMD foo"), timeout.Token);
        await Task.Delay(300); // 未知命令不回覆——等伺服器處理完那一幀

        var lines = log.ToArray();
        Assert.Contains(lines, l => l.Contains("連線來自", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("NEMO ← CHECK_SERVER_VERSION CLIENT_AUTH", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("NEMO → OK QCV8001", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            l => l.Contains("未知命令", StringComparison.Ordinal) && l.Contains("WEIRD_UNKNOWN_CMD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CONNECT的密碼不會出現在診斷記錄裡()
    {
        // 日誌會被複製到郵件與工單裡——密碼（Base64）絕不落地。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        var pass = QctekPassword.Encode("s3cret");
        await stream.WriteAsync(NemoMessage.EncodeText($"CONNECT admin {pass} "), timeout.Token);
        _ = await ReadFrameAsync(stream, timeout.Token);
        await Task.Delay(300);

        var lines = log.ToArray();
        Assert.Contains(lines, l => l.Contains("CONNECT admin ***", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(pass, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 非NEMO資料會把原始位元組寫進診斷記錄()
    {
        // QCC 實測發現：QCC 對埠送出的第一包可能不是 NEMO（別的協定、損壞的串流）。
        // 「非 NEMO 資料」四個字等於沒說——傾印前 96 bytes 才能回答「對方送了什麼」。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("HELLO WORLD \x01\x02\x03\x04"), timeout.Token);
        await Task.Delay(300);

        var lines = log.ToArray();
        Assert.Contains(lines, l => l.Contains("非 NEMO 資料", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("HELLO WORLD", StringComparison.Ordinal));
    }

    /// <summary>讀完整 HTTP 回應（依 Content-Length 收滿即停；回應後連線關閉則讀到 EOF 亦停）。</summary>
    private static async Task<string> ReadHttpResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var data = new List<byte>();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var contentLength = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (n <= 0)
            {
                break;
            }

            data.AddRange(buffer.Take(n));
            if (headerEnd < 0)
            {
                var head = Encoding.ASCII.GetString(data.ToArray());
                headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var clLine = head[..headerEnd]
                        .Split("\r\n")
                        .First(l => l.StartsWith("Content-Length:", StringComparison.Ordinal));
                    contentLength = int.Parse(clLine["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            if (headerEnd >= 0 && data.Count >= headerEnd + 4 + contentLength)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(data.ToArray());
    }

    [Fact]
    public async Task HTTP未知路徑回404並關閉連線()
    {
        // QCC 的裝置探測會先送 HTTP（QCV8001 的 NEMO 與 HTTP 同埠多工，探測端分不出
        // 對面）。真機語意：HTTP 回應後關閉連線（實測 QCV8001 對 GET / 回
        // Connection: close，而真機正是這樣被 QCC 成功加入）——404 也比照；
        // 「標頭說 close 卻不關」會讓探測端等我們關而卡住。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("GET /nope HTTP/1.1\r\nHost: x\r\n\r\n"), timeout.Token);

        var response = await ReadHttpResponseAsync(stream, timeout.Token);

        Assert.StartsWith("HTTP/1.1 404", response, StringComparison.Ordinal);
        Assert.Contains("Connection: close", response, StringComparison.Ordinal);
        var lines = log.ToArray();
        Assert.Contains(lines, l => l.Contains("HTTP 請求", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("GET /nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 根路徑探測回QCV8001頁面並關閉連線()
    {
        // 實測捕獲的 QCC 第一包：GET /（帶 Basic admin:admin）。回 200＋QCV8001 頁面，
        // 標頭逐位元組對齊真機（Server/Connection: close）——真機就是這樣被 QCC
        // 成功加入的；QCC 收到 close 後自己開新連線，不依賴 keep-alive。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"), timeout.Token);

        var response = await ReadHttpResponseAsync(stream, timeout.Token);

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Contains("<title>QCV8001</title>", response, StringComparison.Ordinal);
        Assert.Contains("Server: QCV8001", response, StringComparison.Ordinal);
        Assert.Contains("Connection: close", response, StringComparison.Ordinal);
        Assert.Contains(log.ToArray(), l => l.Contains("HTTP 200", StringComparison.Ordinal));

        // NEMO 不受影響：換一條新連線照常下命令（QCC 收到 close 後的真實路徑）。
        using var nemo = new TcpClient();
        await nemo.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var nemoStream = nemo.GetStream();
        await nemoStream.WriteAsync(NemoMessage.EncodeText("GET_NCHANNEL"), timeout.Token);
        var (_, payload) = await ReadFrameAsync(nemoStream, timeout.Token);
        Assert.Equal("OK 8 ", payload);
    }

    [Fact]
    public async Task 非NEMO資料會關閉連線而不是無限迴圈()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        // 拿 HTTP 打 NEMO 埠＝串流不同步：連線應被關閉（讀到 EOF），而不是卡住或回 garbage。
        // 送 NEMO／HTTP 之外的串流不同步資料（≥ 16 bytes 才會觸發標頭檢查）：連線要被
        // 關閉（讀到 EOF），而不是卡住等更多垃圾。（HTTP 是另一條路徑：回 404 再關閉。）
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HELLO WORLD \x01\x02\x03\x04"), timeout.Token);

        var probe = new byte[16];
        var read = await stream.ReadAsync(probe, timeout.Token);
        Assert.Equal(0, read);
    }

    private static async Task<(string Header, string Payload)> ReadFrameAsync(
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
        return (Encoding.ASCII.GetString(header), Encoding.UTF8.GetString(payload));
    }
}
