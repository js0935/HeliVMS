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

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public bool TryAuthenticate(string user, string password)
            => (user, password) is ("admin", "s3cret");
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

        var frames = NemoMessage.EncodeText("SEARCH_EVENT 1 2 3")
            .Concat(NemoMessage.EncodeText("ALARM_ISALIVE"))
            .ToArray();
        await stream.WriteAsync(frames, timeout.Token);

        // 未知命令不回應（不猜內容）；下一個命令的回應直接接上。
        Assert.Equal("ALIVE ", (await ReadFrameAsync(stream, timeout.Token)).Payload);
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

        async Task<string> RoundTripAsync(string request)
        {
            await stream.WriteAsync(NemoMessage.EncodeText(request), timeout.Token);
            return (await ReadFrameAsync(stream, timeout.Token)).Payload;
        }

        var pass = QctekPassword.Encode("s3cret");
        Assert.Equal("OK QCV8001 ", await RoundTripAsync("CHECK_SERVER_VERSION CLIENT_AUTH"));
        Assert.Equal("OK -1 -1 -1 ", await RoundTripAsync($"CONNECT admin {pass} "));
        Assert.Equal("OK 8 ", await RoundTripAsync("GET_NCHANNEL"));
        Assert.Equal("OK 1 1 ", await RoundTripAsync(
            $"ALARM_REQUEST 5 version7.7 user: admin {pass} =QCV0000000001"));
        Assert.Equal("OK ", await RoundTripAsync("ALARM_CANCEL 1 1 "));
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

    [Fact]
    public async Task HTTP未知路徑會收到404而不是被斷線()
    {
        // QCC 的裝置探測會先送 HTTP（QCV8001 的 NEMO 與 HTTP 同埠多工，探測端分不出
        // 對面）——直接斷線會讓探測端只看得到「連線被重設」，與「命令不認識」長得一樣。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("GET /nope HTTP/1.1\r\nHost: x\r\n\r\n"), timeout.Token);

        var buffer = new byte[512];
        var n = await stream.ReadAsync(buffer.AsMemory(), timeout.Token);
        var response = System.Text.Encoding.ASCII.GetString(buffer, 0, n);

        Assert.StartsWith("HTTP/1.1 404", response, StringComparison.Ordinal);
        var lines = log.ToArray();
        Assert.Contains(lines, l => l.Contains("HTTP 探測", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("GET /nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 根路徑探測會收到QCV8001資訊頁()
    {
        // 實測捕獲的 QCC 第一包：GET /（帶 Basic admin:admin）。回 200＋ProductName，
        // QCC 才認得型號——這是「無法確認監控系統的型號與協定」的解藥。
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback, log: log.Enqueue);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(
            System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"), timeout.Token);

        // 整個回應讀到連線關閉（Connection: close）。
        var received = new System.Text.StringBuilder();
        var buffer = new byte[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
        {
            received.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, n));
        }

        var response = received.ToString();
        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Contains("<ProductName> QCV8001", response, StringComparison.Ordinal);
        Assert.Contains(log.ToArray(), l => l.Contains("HTTP 200", StringComparison.Ordinal));
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
