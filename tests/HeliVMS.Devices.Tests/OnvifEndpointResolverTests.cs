using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證多廠牌端點解析：手動位址展開、路徑/埠排序、XAddr 位址校正、IPv6。</summary>
public class OnvifEndpointResolverTests
{
    [Fact]
    public void BuildDeviceServiceCandidates_BareIp_PutsStandardPathOnPort80First()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("192.168.1.64");

        Assert.Equal("http://192.168.1.64/onvif/device_service", candidates[0]);
        Assert.Contains("http://192.168.1.64:8000/onvif/device_service", candidates);
        Assert.Contains("http://192.168.1.64:8080/onvif/device_service", candidates);
        Assert.Contains("http://192.168.1.64:2020/onvif/device_service", candidates);
    }

    [Fact]
    public void BuildDeviceServiceCandidates_ExplicitPort_ComesFirst()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("192.168.1.64:8000");

        Assert.Equal("http://192.168.1.64:8000/onvif/device_service", candidates[0]);
        Assert.DoesNotContain("http://192.168.1.64/onvif/device_service", candidates.Take(1));
    }

    [Fact]
    public void BuildDeviceServiceCandidates_IncludesVendorPathVariants()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("10.0.0.9");

        Assert.Contains("http://10.0.0.9/onvif/device_service?device_type=NetworkCamera", candidates);
        Assert.Contains("http://10.0.0.9/onvif/DeviceService", candidates);
        Assert.Contains("http://10.0.0.9/Services", candidates);
    }

    [Fact]
    public void BuildDeviceServiceCandidates_ExplicitPath_IsTrustedAndNotExpanded()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("http://cam.local:2020/onvif/Media");

        var single = Assert.Single(candidates);
        Assert.Equal("http://cam.local:2020/onvif/Media", single);
    }

    [Fact]
    public void BuildDeviceServiceCandidates_HostName_IsPreserved()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("cam.example.com");

        Assert.Equal("http://cam.example.com/onvif/device_service", candidates[0]);
    }

    [Fact]
    public void BuildDeviceServiceCandidates_Ipv6_BracketsHost()
    {
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("fe80::1");

        Assert.Equal("http://[fe80::1]/onvif/device_service", candidates[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://192.168.1.5/onvif/device_service")]
    [InlineData("not a host")]
    public void BuildDeviceServiceCandidates_InvalidInput_ReturnsEmpty(string input)
    {
        Assert.Empty(OnvifEndpointResolver.BuildDeviceServiceCandidates(input));
    }

    [Fact]
    public void BuildServiceCandidates_UsesDeviceServicePort()
    {
        var candidates = OnvifEndpointResolver.BuildServiceCandidates(
            "http://192.168.1.64:8000/onvif/device_service",
            OnvifEndpointResolver.MediaServicePaths);

        Assert.Equal("http://192.168.1.64:8000/onvif/Media", candidates[0]);
        Assert.Contains("http://192.168.1.64:8000/onvif/media_service", candidates);
    }

    [Fact]
    public void NormalizeServiceXAddr_ForeignHost_IsReplacedByConnectionHost()
    {
        var normalized = OnvifEndpointResolver.NormalizeServiceXAddr(
            "http://camera-internal.local/onvif/Media",
            "http://192.168.1.64/onvif/device_service");

        Assert.Equal("http://192.168.1.64/onvif/Media", normalized);
    }

    [Fact]
    public void NormalizeServiceXAddr_ForeignHostWithPort_KeepsServicePort()
    {
        var normalized = OnvifEndpointResolver.NormalizeServiceXAddr(
            "http://10.0.0.5:9000/onvif/Media",
            "http://192.168.1.64/onvif/device_service");

        Assert.Equal("http://192.168.1.64:9000/onvif/Media", normalized);
    }

    [Fact]
    public void NormalizeServiceXAddr_MissingServicePort_AdoptsDeviceServicePort()
    {
        // 多數海康/大華固件回報之 XAddr 不含實際服務埠，直接使用會連到 80 埠失敗
        var normalized = OnvifEndpointResolver.NormalizeServiceXAddr(
            "http://192.168.1.64/onvif/Media",
            "http://192.168.1.64:8000/onvif/device_service");

        Assert.Equal("http://192.168.1.64:8000/onvif/Media", normalized);
    }

    [Fact]
    public void NormalizeServiceXAddr_SameHostAndPort_IsUnchanged()
    {
        const string xaddr = "http://192.168.1.64/onvif/Media?device_type=NetworkCamera";

        Assert.Equal(xaddr, OnvifEndpointResolver.NormalizeServiceXAddr(xaddr, "http://192.168.1.64/onvif/device_service"));
    }

    [Fact]
    public void NormalizeServiceXAddr_InvalidOrEmpty_IsReturnedAsIs()
    {
        Assert.Equal(string.Empty, OnvifEndpointResolver.NormalizeServiceXAddr("  ", "http://192.168.1.64/x"));
        Assert.Equal("not-a-uri", OnvifEndpointResolver.NormalizeServiceXAddr("not-a-uri", "http://192.168.1.64/x"));
    }

    [Theory]
    [InlineData("192.168.1.64", "192.168.1.64")]
    [InlineData("192.168.1.64:8000", "192.168.1.64")]
    [InlineData("http://cam.local/onvif/device_service", "cam.local")]
    [InlineData("[fe80::1]:8000", "fe80::1")]
    [InlineData("nonsense!", null)]
    public void ExtractHost_ParsesCommonInputs(string input, string? expected)
    {
        Assert.Equal(expected, OnvifEndpointResolver.ExtractHost(input));
    }
}
