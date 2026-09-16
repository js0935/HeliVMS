using System.Net;
using System.Net.Sockets;
using System.Text;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證 WS-Discovery 探測（Probe 送出、ProbeMatch 解析／去重、逾時）。</summary>
public class DiscoveryClientTests
{
    private const string ProbeMatchXml = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:d="http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01">
          <s:Body>
            <d:ProbeMatches>
              <d:ProbeMatch>
                <a:EndpointReference><a:Address>uuid:aa11bb22</a:Address></a:EndpointReference>
                <d:Types>tdn:NetworkVideoTransmitter</d:Types>
                <d:Scopes>onvif://www.onvif.org/name/DemoCam onvif://www.onvif.org/type/video_encoder</d:Scopes>
                <d:XAddrs>http://192.168.1.64/onvif/device_service</d:XAddrs>
                <d:MetadataVersion>3</d:MetadataVersion>
              </d:ProbeMatch>
            </d:ProbeMatches>
          </s:Body>
        </s:Envelope>
        """;

    private static Dictionary<string, DiscoveredDevice> Parse(string xml)
    {
        var results = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        DiscoveryClient.ParseMatch(xml, results);
        return results;
    }

    [Fact]
    public void ParseMatch_ParsesProbeMatch_ExtractsFields()
    {
        var device = Assert.Single(Parse(ProbeMatchXml).Values);

        Assert.Equal("uuid:aa11bb22", device.EndpointAddress);
        Assert.Equal("tdn:NetworkVideoTransmitter", device.Types);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
        Assert.Equal("DemoCam", device.NameHint);
        Assert.Contains("onvif://www.onvif.org/name/DemoCam", device.Scopes);
        Assert.Contains("onvif://www.onvif.org/type/video_encoder", device.Scopes);
    }

    [Fact]
    public void ParseMatch_InvalidXml_IsIgnored()
    {
        Assert.Empty(Parse("<<< not xml >>>"));
    }

    [Fact]
    public void ParseMatch_EmptyBody_IsIgnored()
    {
        Assert.Empty(Parse("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>"));
    }

    [Fact]
    public void ParseMatch_MissingEndpointAddress_IsSkipped()
    {
        var xml = ProbeMatchXml.Replace(
            "<a:EndpointReference><a:Address>uuid:aa11bb22</a:Address></a:EndpointReference>", string.Empty);

        Assert.Empty(Parse(xml));
    }

    [Fact]
    public void ParseMatch_NoHttpXAddr_IsSkipped()
    {
        var xml = ProbeMatchXml.Replace(
            "http://192.168.1.64/onvif/device_service", "rtsp://192.168.1.64:554/stream");

        Assert.Empty(Parse(xml));
    }

    [Fact]
    public void ParseMatch_SameHttpXAddr_Deduplicates_LastWins()
    {
        var second = ProbeMatchXml.Replace("uuid:aa11bb22", "uuid:cc33dd44");
        var results = Parse(ProbeMatchXml);
        DiscoveryClient.ParseMatch(second, results);

        var device = Assert.Single(results.Values);
        Assert.Equal("uuid:cc33dd44", device.EndpointAddress);
    }

    [Fact]
    public async Task ProbeAsync_ReceivesProbeMatch_ReturnsParsedDevice()
    {
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(ProbeMatchXml);

        var replyTask = Task.Run(async () =>
        {
            var rx = await peer.ReceiveAsync();
            await peer.SendAsync(payload, rx.RemoteEndPoint);
        });

        var devices = await DiscoveryClient.ProbeAsync(udp, peerEndpoint, TimeSpan.FromSeconds(3));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        var device = Assert.Single(devices);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
        Assert.Equal("DemoCam", device.NameHint);
    }

    [Fact]
    public async Task ProbeAsync_NoResponse_ReturnsEmptyAfterTimeout()
    {
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var devices = await DiscoveryClient.ProbeAsync(udp, peerEndpoint, TimeSpan.FromSeconds(1));

        Assert.Empty(devices);
    }
}