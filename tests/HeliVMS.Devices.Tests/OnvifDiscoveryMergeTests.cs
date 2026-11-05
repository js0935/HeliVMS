using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 多網卡多播結果的合併去重。同一台攝影機若因為兩張網卡各回一次而在清單上出現兩筆，
/// 操作員會以為有兩台，重複加入則會替同一台開出第二個頻道。
/// </summary>
public class OnvifDiscoveryMergeTests
{
    private static DiscoveredDevice Device(string uuid, string xAddr) => new()
    {
        EndpointAddress = $"urn:uuid:{uuid}",
        XAddrs = [xAddr],
    };

    [Fact]
    public void 同一台從兩個介面回報只留一筆()
    {
        // 同一顆裝置 UUID、兩個介面看到的 XAddr 不同（多介面設備）：仍是一台。
        var merged = DiscoveryClient.Merge(
        [
            [Device("cam-a", "http://192.168.1.64/onvif/device_service")],
            [Device("cam-a", "http://10.0.0.64/onvif/device_service")],
        ]);

        var only = Assert.Single(merged);
        Assert.Contains("192.168.1.64", only.HttpXAddr);
    }

    [Fact]
    public void 不同台不會被合併()
    {
        var merged = DiscoveryClient.Merge(
        [
            [Device("cam-a", "http://192.168.1.64/onvif/device_service")],
            [Device("cam-b", "http://192.168.1.65/onvif/device_service")],
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 沒有UUID時以XAddr判身分()
    {
        // HTTP 掃描的結果沒有裝置 UUID；同一 XAddr 只留一筆。
        var scanned = new DiscoveredDevice
        {
            EndpointAddress = "urn:probe:192.168.1.64",
            XAddrs = ["http://192.168.1.64/onvif/device_service"],
        };

        var merged = DiscoveryClient.Merge([[scanned], [scanned]]);

        Assert.Single(merged);
    }

    [Fact]
    public void 順序沿用來源順序()
    {
        var first = Device("cam-a", "http://192.168.1.64/onvif/device_service");
        var second = Device("cam-b", "http://192.168.1.65/onvif/device_service");

        var merged = DiscoveryClient.Merge([[first], [second, first]]);

        Assert.Equal(["cam-a", "cam-b"], merged.Select(d => d.EndpointAddress.Replace("urn:uuid:", string.Empty)));
    }
}
