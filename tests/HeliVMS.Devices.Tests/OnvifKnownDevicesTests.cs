using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 「已加入」的比對。誤判的兩邊代價不對稱：漏標會讓操作員把已經在錄的攝影機再加一次
/// （同一台兩個頻道、各錄一半）；誤標則只是少一個提示。所以寧可寬鬆地認得出來。
/// </summary>
public class OnvifKnownDevicesTests
{
    private static DiscoveredDevice Device(string xAddr) => new()
    {
        EndpointAddress = $"urn:uuid:{Guid.NewGuid():D}",
        XAddrs = [xAddr],
    };

    [Fact]
    public void 設備IP與頻道位址都能認出同一台()
    {
        var known = OnvifKnownDevices.Build(
            ["192.168.1.64"],
            ["rtsp://admin:pass@192.168.1.65:554/Streaming/Channels/101"]);

        Assert.True(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.64/onvif/device_service")));
        // 頻道位址裡的主機要能認出來：手動貼 RTSP 加入的攝影機沒有 devices 記錄。
        Assert.True(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.65/onvif/device_service")));
        Assert.False(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.66/onvif/device_service")));
    }

    [Fact]
    public void 埠號與帳密不影響比對()
    {
        var known = OnvifKnownDevices.Build(["192.168.1.64"], []);

        // 同一台可能登記成帶埠的位址，而 ONVIF XAddr 不一定帶埠。
        Assert.True(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.64:8000/onvif/device_service")));
    }

    [Fact]
    public void 大小寫與空白不影響比對()
    {
        var known = OnvifKnownDevices.Build([" 192.168.1.64 "], ["rtsp://CAM01.local/stream"]);

        Assert.True(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.64/onvif/device_service")));
        Assert.True(OnvifKnownDevices.IsKnown(known, Device("http://cam01.local/onvif/device_service")));
    }

    [Fact]
    public void 空白與壞掉的位址一律略過不會誤標()
    {
        var known = OnvifKnownDevices.Build(["", "   ", null], ["not a url", "", null]);

        Assert.Empty(known);
        Assert.False(OnvifKnownDevices.IsKnown(known, Device("http://192.168.1.64/onvif/device_service")));
    }

    [Fact]
    public void 從RTSP位址只取主機()
    {
        Assert.Equal("192.168.1.65", OnvifKnownDevices.HostOf("rtsp://admin:pass@192.168.1.65:554/Streaming/Channels/101"));
        Assert.Null(OnvifKnownDevices.HostOf("admin:pass"));
        Assert.Null(OnvifKnownDevices.HostOf(null));
    }
}
