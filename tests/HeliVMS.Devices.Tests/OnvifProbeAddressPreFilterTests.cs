using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 掃描的單一位址探測：TCP 預檢說「沒有人在聽」時不該再花時間做 ONVIF 呼叫
/// （那正是逐埠等兩秒的舊行為），有人聽才輪到 ONVIF。
/// </summary>
public class OnvifProbeAddressPreFilterTests
{
    [Fact]
    public async Task 沒有埠在聽時不會發出任何ONVIF呼叫()
    {
        var calls = 0;
        var closedPort = ClosedPort();

        var found = await DiscoveryClient.ProbeAddressAsync(
            "127.0.0.1",
            ports: [closedPort],
            serviceFactory: xAddr =>
            {
                calls++;
                return new OnvifDeviceService(xAddr, null, null, new RecordingHandler((_, _) => OnvifTestHttp.DeviceInfoXml));
            });

        Assert.Null(found);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task 有人在聽的埠才會做ONVIF呼叫()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var probed = new List<string>();
        var found = await DiscoveryClient.ProbeAddressAsync(
            $"127.0.0.1:{openPort}",
            ports: [openPort],
            serviceFactory: xAddr =>
            {
                probed.Add(xAddr);
                return new OnvifDeviceService(xAddr, null, null, new RecordingHandler((_, _) => OnvifTestHttp.DeviceInfoXml));
            });

        Assert.NotEmpty(probed);
        Assert.NotNull(found);
        Assert.Contains(openPort.ToString(), probed[0]);
    }

    /// <summary>綁一個埠再放掉（同一個測試內使用；只用來確認「沒有回應」的那條路）。</summary>
    private static int ClosedPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }
}
