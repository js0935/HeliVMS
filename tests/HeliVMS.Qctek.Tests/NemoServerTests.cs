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
    public async Task 非NEMO資料會關閉連線而不是無限迴圈()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NemoServer(new TestContext(), IPAddress.Loopback);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();

        // 拿 HTTP 打 NEMO 埠＝串流不同步：連線應被關閉（讀到 EOF），而不是卡住或回 garbage。
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"), timeout.Token);

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
