using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證 WS-Discovery 探測（Probe 送出、ProbeMatch 解析／去重、逾時）。</summary>
public class DiscoveryClientTests
{
    private static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";
    private static readonly XNamespace Wsd = "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01";

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

    // ---- M35：Hello / Bye / Resolve ----

    [Fact]
    public async Task SendHello_VerifiesXmlOnPeer()
    {
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var receivedTask = Task.Run(async () => Encoding.UTF8.GetString((await peer.ReceiveAsync()).Buffer));

        var ok = await DiscoveryClient.SendHelloAsync(
            udp, peerEndpoint, "cam-abc", "http://192.168.1.9/onvif/device_service");
        Assert.True(ok);

        var xml = await receivedTask.WaitAsync(TimeSpan.FromSeconds(5));
        var doc = XDocument.Parse(xml);
        var hello = doc.Root!;
        Assert.Equal(
            "urn:uuid:cam-abc",
            hello.Element(Wsa + "EndpointReference")
                !.Element(Wsa + "Address")!.Value);
        Assert.Equal(
            "http://192.168.1.9/onvif/device_service",
            hello.Element(Wsd + "XAddrs")!.Value);
        Assert.Equal("1", hello.Element(Wsd + "MetadataVersion")!.Value);
    }

    [Fact]
    public async Task SendBye_VerifiesXmlOnPeer()
    {
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var receivedTask = Task.Run(async () => Encoding.UTF8.GetString((await peer.ReceiveAsync()).Buffer));

        var ok = await DiscoveryClient.SendByeAsync(udp, peerEndpoint, "cam-abc");
        Assert.True(ok);

        var xml = await receivedTask.WaitAsync(TimeSpan.FromSeconds(5));
        var doc = XDocument.Parse(xml);
        var address = doc.Root!
            .Element(Wsa + "EndpointReference")!
            .Element(Wsa + "Address")!
            .Value;
        Assert.Equal("urn:uuid:cam-abc", address);
    }

    [Fact]
    public async Task Resolve_SendsResolveAndParsesMatch()
    {
        const string resolveMatchXml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:d="http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01">
              <s:Body>
                <d:ResolveMatch>
                  <a:EndpointReference><a:Address>urn:uuid:cam-abc</a:Address></a:EndpointReference>
                  <d:Types>tdn:NetworkVideoTransmitter</d:Types>
                  <d:Scopes>onvif://www.onvif.org/name/TestCam</d:Scopes>
                  <d:XAddrs>http://192.168.1.9/onvif/device_service</d:XAddrs>
                  <d:MetadataVersion>1</d:MetadataVersion>
                </d:ResolveMatch>
              </s:Body>
            </s:Envelope>
            """;

        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(resolveMatchXml);

        var replyTask = Task.Run(async () =>
        {
            var rx = await peer.ReceiveAsync();
            var req = Encoding.UTF8.GetString(rx.Buffer);
            Assert.Contains("http://schemas.xmlsoap.org/ws/2005/04/discovery/Resolve", req);
            Assert.Contains("urn:uuid:cam-abc", req);
            await peer.SendAsync(payload, rx.RemoteEndPoint);
        });

        var device = await DiscoveryClient.ResolveAsync(udp, peerEndpoint, "cam-abc", TimeSpan.FromSeconds(3));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(device);
        Assert.Equal("urn:uuid:cam-abc", device!.EndpointAddress);
        Assert.Equal("http://192.168.1.9/onvif/device_service", device.HttpXAddr);
        Assert.Equal("TestCam", device.NameHint);
    }

    [Fact]
    public async Task Resolve_WrongInstanceId_IgnoresMatch()
    {
        const string resolveMatchXml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:d="http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01">
              <s:Body>
                <d:ResolveMatch>
                  <a:EndpointReference><a:Address>urn:uuid:other</a:Address></a:EndpointReference>
                  <d:XAddrs>http://192.168.1.9/onvif/device_service</d:XAddrs>
                </d:ResolveMatch>
              </s:Body>
            </s:Envelope>
            """;

        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(resolveMatchXml);

        var replyTask = Task.Run(async () =>
        {
            var rx = await peer.ReceiveAsync();
            await peer.SendAsync(payload, rx.RemoteEndPoint);
        });

        var device = await DiscoveryClient.ResolveAsync(udp, peerEndpoint, "cam-abc", TimeSpan.FromSeconds(1));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(device);
    }

    [Fact]
    public async Task Resolve_NoResponse_ReturnsNullAfterTimeout()
    {
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var device = await DiscoveryClient.ResolveAsync(udp, peerEndpoint, "cam-abc", TimeSpan.FromSeconds(1));

        Assert.Null(device);
    }
}