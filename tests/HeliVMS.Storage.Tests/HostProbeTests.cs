namespace HeliVMS.Storage.Tests;

/// <summary>
/// 攝影機管理「Ping」按鈕背後的可達性探測（<see cref="HostProbe"/>）。
/// </summary>
/// <remarks>
/// 全部用注入的假 ICMP／TCP，真正連網的只剩兩支很薄的私有方法。這不是為了測試方便而已：
/// 現場判斷一台攝影機在不在，會直接影響操作員要不要去機房。若這裡的邏輯錯了，錯的不是
/// 一個像素，而是「把一台好好在出畫的機器報成故障」。所以每一條分支都在講一個現場情境。
/// </remarks>
public class HostProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ICMP有回應就不再試TCP()
    {
        // 有 ICMP 回覆就是最短的路徑，沒有理由再開一條 TCP 連線去打擾裝置（有些攝影機
        // 對同時連上的連線數很敏感，會踢掉正在錄的那一條）。
        var tcpCalled = false;

        var result = await HostProbe.ProbeAsync(
            "192.168.1.64",
            80,
            Timeout,
            icmp: (_, _) => Task.FromResult((true, 7)),
            tcp: (_, _, _) =>
            {
                tcpCalled = true;
                return Task.FromResult(true);
            });

        Assert.Equal(HostProbeOutcome.Reached, result.Outcome);
        Assert.Equal(7, result.LatencyMs);
        Assert.False(result.ViaTcp);
        Assert.False(tcpCalled);
    }

    [Fact]
    public async Task ICMP被丟封包但TCP連得上仍然算可達()
    {
        // 這是現場最常見的誤判來源：攝影機或交換器把 ICMP 關掉，只回 TCP。
        // 若只做 ICMP，一台正在正常出畫的攝影機就會被報成「逾時」，操作員於是去重開
        // 一台根本沒壞的機器——比沒有 Ping 更糟，因為它給了一個看起來很確定的錯答案。
        var result = await HostProbe.ProbeAsync(
            "192.168.1.64",
            554,
            Timeout,
            icmp: (_, _) => Task.FromResult((false, 0)),
            tcp: (host, port, _) => Task.FromResult(host == "192.168.1.64" && port == 554));

        Assert.Equal(HostProbeOutcome.Reached, result.Outcome);
        Assert.True(result.ViaTcp);
        Assert.True(result.LatencyMs >= 0);
    }

    [Fact]
    public async Task ICMP與TCP都沒有回應才算逾時()
    {
        var result = await HostProbe.ProbeAsync(
            "192.168.1.64",
            80,
            Timeout,
            icmp: (_, _) => Task.FromResult((false, 0)),
            tcp: (_, _, _) => Task.FromResult(false));

        Assert.Equal(HostProbeOutcome.Timeout, result.Outcome);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task ICMP擲例外時仍會退回TCP()
    {
        // 名稱解析失敗、沒有權限送 ICMP、網路介面正在切換都會擲例外。把例外直接丟到 UI
        // 只會得到一句看不懂的訊息，操作員無從判斷機器到底在不在。
        var result = await HostProbe.ProbeAsync(
            "camera-not-in-dns",
            80,
            Timeout,
            icmp: (_, _) => throw new InvalidOperationException("boom"),
            tcp: (_, _, _) => Task.FromResult(true));

        Assert.Equal(HostProbeOutcome.Reached, result.Outcome);
        Assert.True(result.ViaTcp);
    }

    [Fact]
    public async Task 位址空白時直接回報無效且不發出任何探測()
    {
        var probed = false;

        var result = await HostProbe.ProbeAsync(
            "   ",
            80,
            Timeout,
            icmp: (_, _) =>
            {
                probed = true;
                return Task.FromResult((true, 1));
            },
            tcp: (_, _, _) =>
            {
                probed = true;
                return Task.FromResult(true);
            });

        Assert.Equal(HostProbeOutcome.InvalidAddress, result.Outcome);
        Assert.False(probed);
    }

    [Theory]
    [InlineData("192.168.1.64", 80, "192.168.1.64", 80)]
    [InlineData("192.168.1.64:8000", 80, "192.168.1.64", 8000)]
    [InlineData("rtsp://admin:pw@192.168.1.64:554/Streaming/Channels/101", 80, "192.168.1.64", 554)]
    [InlineData("http://192.168.1.64/onvif/device_service", 8080, "192.168.1.64", 8080)]
    [InlineData("[fe80::1]:8000", 80, "fe80::1", 8000)]
    [InlineData("fe80::1", 80, "fe80::1", 80)]
    public void 位址字串會拆出主機與要測的埠(string raw, int fallback, string host, int port)
    {
        // 現場貼進 devices.ip 的常常不是純 IP，而是整條 RTSP 網址或帶埠的位址。
        // 直接拿這種字串去 Ping 會失敗，操作員就會以為機器壞了。
        // 埠以字串裡的為準：它比 devices.port 更接近裝置實際在聽的埠。
        var (parsedHost, parsedPort, valid) = HostProbe.ParseEndpoint(raw, fallback);

        Assert.True(valid);
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("[fe80::1")]
    [InlineData(":8000")]
    [InlineData("rtsp://")]
    public void 無法解析的位址會被標記為無效(string raw)
    {
        var (_, _, valid) = HostProbe.ParseEndpoint(raw, 80);

        Assert.False(valid);
    }
}
