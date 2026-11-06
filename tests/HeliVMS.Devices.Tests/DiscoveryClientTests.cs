using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證 WS-Discovery 探測（SOAP 封套形狀、ProbeMatch 解析／去重、逾時）。</summary>
public class DiscoveryClientTests
{
    private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa04 = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Disc04 = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";
    private static readonly XNamespace Wsd = "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01";

    /// <summary>WS-Discovery 1.1（2009/01＋WS-Addressing 2005/08）回應：部分廠牌與較新韌體。</summary>
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

    /// <summary>
    /// 實機（VIVOTEK，2026-11 現場驗證）回應的形狀（去識別化：IP/UUID 換成測試值，其餘原樣）：
    /// <c>wsadis</c> 前綴的 2004/08 定址、名稱 scope 百分比編碼、XAddrs 兩個（公網＋link-local）。
    /// </summary>
    private const string ProbeMatchXmlVivotek = """
        <?xml version='1.0' encoding='utf-8'?><soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:wsadis="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:dn="http://www.onvif.org/ver10/network/wsdl" xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><soap:Header><wsadis:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsadis:Action><wsadis:MessageID>uuid:aa11bb22-0000-0000-0000-000000000001</wsadis:MessageID><wsadis:RelatesTo>urn:uuid:00000000-1111-2222-3333-444444444444</wsadis:RelatesTo><wsadis:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsadis:To><d:AppSequence InstanceId="1793619951" MessageNumber="24" /></soap:Header><soap:Body><d:ProbeMatches><d:ProbeMatch><wsadis:EndpointReference><wsadis:Address>urn:uuid:273da1db-4355-ba70-8455-5183233e2787</wsadis:Address></wsadis:EndpointReference><d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types><d:Scopes>onvif://www.onvif.org/hardware/IP9164-HT onvif://www.onvif.org/name/VIVOTEK%20IP9164-HT onvif://www.onvif.org/device/Camera onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/type/audio_encoder onvif://www.onvif.org/type/video_analytic onvif://www.onvif.org/Profile/Streaming onvif://www.onvif.org/Profile/G onvif://www.onvif.org/location/global</d:Scopes><d:XAddrs>http://192.168.1.64:80/onvif/device_service http://169.254.8.68:80/onvif/device_service</d:XAddrs><d:MetadataVersion>1793619952</d:MetadataVersion></d:ProbeMatch></d:ProbeMatches></soap:Body></soap:Envelope>
        """;

    /// <summary>WS-Discovery 1.0（2005/04＋WS-Addressing 2004/08）回應：ONVIF 規範版本，現場實機格式。</summary>
    private const string ProbeMatchXmlOnvif = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <s:Body>
            <d:ProbeMatches>
              <d:ProbeMatch>
                <a:EndpointReference><a:Address>urn:uuid:aa11bb22</a:Address></a:EndpointReference>
                <d:Types>dn:NetworkVideoTransmitter</d:Types>
                <d:Scopes>onvif://www.onvif.org/name/DemoCam onvif://www.onvif.org/type/video_encoder</d:Scopes>
                <d:XAddrs>http://192.168.1.64/onvif/device_service</d:XAddrs>
                <d:MetadataVersion>1</d:MetadataVersion>
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

    /// <summary>
    /// 嚴格檢查送出的 Probe 是否符合 ONVIF 核心規範 §5.1：
    /// SOAP 1.2 封套＋2005/04 本體＋2004/08 定址＋探索 URN 的 To＋dn 繫結的 NetworkVideoTransmitter。
    /// </summary>
    private static void AssertValidProbe(XDocument request)
    {
        var envelope = request.Root;
        Assert.NotNull(envelope);
        Assert.Equal(Soap12 + "Envelope", envelope!.Name);

        var header = envelope.Element(Soap12 + "Header");
        Assert.NotNull(header);
        Assert.Equal(
            "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe",
            header!.Element(Wsa04 + "Action")?.Value);
        Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery", header.Element(Wsa04 + "To")?.Value);

        var messageId = header.Element(Wsa04 + "MessageID")?.Value ?? string.Empty;
        Assert.True(
            Guid.TryParse(messageId.Replace("urn:uuid:", string.Empty, StringComparison.Ordinal), out _),
            $"MessageID 應為 urn:uuid:{{guid}}，實際 {messageId}");

        var probe = envelope.Element(Soap12 + "Body")?.Element(Disc04 + "Probe");
        Assert.NotNull(probe);
        var types = probe!.Element(Disc04 + "Types");
        Assert.NotNull(types);
        Assert.Equal("dn:NetworkVideoTransmitter", types!.Value);

        // dn 前綴必須有繫結：Types 是 QName，嚴格解析的設備找不到前綴就忽略整個探測。
        Assert.Equal(
            "http://www.onvif.org/ver10/network/wsdl",
            types.GetNamespaceOfPrefix("dn")?.NamespaceName);
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
    public void ParseMatch_ONVIF200504的回應也會解析()
    {
        // 舊寫法寫死 2009/01＋2005/08 命名空間，1.0 設備的回應會被整批丟掉——
        // 現場徵狀是「掃描跑完但清單是空的」。
        var device = Assert.Single(Parse(ProbeMatchXmlOnvif).Values);

        Assert.Equal("urn:uuid:aa11bb22", device.EndpointAddress);
        Assert.Equal("dn:NetworkVideoTransmitter", device.Types);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
        Assert.Equal("DemoCam", device.NameHint);
    }

    [Fact]
    public void ParseMatch_實機VIVOTEK回應的名稱會解碼且XAddr取第一個()
    {
        // 真機驗證抓到的形狀：名稱 scope 是百分比編碼（name/VIVOTEK%20IP9164-HT），
        // 不解碼的話精靈清單上顯示的是「VIVOTEK%20IP9164-HT」；XAddrs 有兩個
        // （公網＋link-local），取第一個 http 條目。
        var device = Assert.Single(Parse(ProbeMatchXmlVivotek).Values);

        Assert.Equal("urn:uuid:273da1db-4355-ba70-8455-5183233e2787", device.EndpointAddress);
        Assert.Equal("http://192.168.1.64:80/onvif/device_service", device.HttpXAddr);
        Assert.Equal(2, device.XAddrs.Count);
        Assert.Equal("VIVOTEK IP9164-HT", device.NameHint);
        Assert.Equal("dn:NetworkVideoTransmitter tds:Device", device.Types);
    }

    [Fact]
    public void ParseMatch_EndpointReference在探索命名空間也能解析()
    {
        // 部分設備把 EndpointReference 放在探索命名空間（d:）而不是定址命名空間（a:）。
        var xml = ProbeMatchXmlOnvif.Replace("a:EndpointReference", "d:EndpointReference");

        var device = Assert.Single(Parse(xml).Values);
        Assert.Equal("urn:uuid:aa11bb22", device.EndpointAddress);
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
    public async Task 嚴格假攝影機只回應合格的SOAP探測()
    {
        // 現場回報「搜尋一台都找不到」而手動輸入 IP 卻正常。根因：送出的根本不是 SOAP 封套
        // （裸的 <Probe>），命名空間還是 2009/01 配 2005/04 的 Action——真實設備（gsoap 等）
        // 直接丟棄。這個假攝影機只回應合格探測，把缺陷釘住：拿掉封套、命名空間改錯、
        // To 改成位址、Types 少了 dn 繫結，它都會靜默而測試轉紅。
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(ProbeMatchXmlOnvif);

        var replyTask = Task.Run(async () =>
        {
            var rx = await peer.ReceiveAsync();
            AssertValidProbe(XDocument.Parse(Encoding.UTF8.GetString(rx.Buffer)));
            await peer.SendAsync(payload, rx.RemoteEndPoint);
        });

        var devices = await DiscoveryClient.ProbeAsync(udp, peerEndpoint, TimeSpan.FromSeconds(3));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        var device = Assert.Single(devices);
        Assert.Equal("urn:uuid:aa11bb22", device.EndpointAddress);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
    }

    [Fact]
    public async Task 探測會重送而不是只送一次()
    {
        // UDP 沒有重傳；Wi-Fi 掉一個探測封包＝整批攝影機找不到。假設備刻意忽略第一個封包，
        // 只回應第二個——沒有重送的話這裡永遠收不到裝置。
        using var udp = new UdpClient();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(ProbeMatchXmlOnvif);

        var replyTask = Task.Run(async () =>
        {
            _ = await peer.ReceiveAsync();          // 第一個探測：故意不理（模擬掉包）
            var second = await peer.ReceiveAsync(); // 重送的探測：才回應
            await peer.SendAsync(payload, second.RemoteEndPoint);
        });

        var devices = await DiscoveryClient.ProbeAsync(udp, peerEndpoint, TimeSpan.FromSeconds(3));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        var device = Assert.Single(devices);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
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
        var envelope = XDocument.Parse(xml).Root!;
        Assert.Equal(Soap12 + "Envelope", envelope.Name);

        var header = envelope.Element(Soap12 + "Header")!;
        Assert.Equal("http://schemas.xmlsoap.org/ws/2005/04/discovery/Hello", header.Element(Wsa04 + "Action")!.Value);
        Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery", header.Element(Wsa04 + "To")!.Value);
        Assert.StartsWith("urn:uuid:", header.Element(Wsa04 + "MessageID")!.Value, StringComparison.Ordinal);

        var hello = envelope.Element(Soap12 + "Body")!.Element(Disc04 + "Hello")!;
        Assert.Equal(
            "urn:uuid:cam-abc",
            hello.Element(Wsa04 + "EndpointReference")!.Element(Wsa04 + "Address")!.Value);
        Assert.Equal(
            "http://192.168.1.9/onvif/device_service",
            hello.Element(Disc04 + "XAddrs")!.Value);
        Assert.Equal("dn:NetworkVideoTransmitter", hello.Element(Disc04 + "Types")!.Value);
        Assert.Equal("1", hello.Element(Disc04 + "MetadataVersion")!.Value);
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
        var envelope = XDocument.Parse(xml).Root!;
        Assert.Equal(Soap12 + "Envelope", envelope.Name);

        var header = envelope.Element(Soap12 + "Header")!;
        Assert.Equal("http://schemas.xmlsoap.org/ws/2005/04/discovery/Bye", header.Element(Wsa04 + "Action")!.Value);
        Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery", header.Element(Wsa04 + "To")!.Value);

        var bye = envelope.Element(Soap12 + "Body")!.Element(Disc04 + "Bye")!;
        Assert.Equal(
            "urn:uuid:cam-abc",
            bye.Element(Wsa04 + "EndpointReference")!.Element(Wsa04 + "Address")!.Value);
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
            var request = XDocument.Parse(Encoding.UTF8.GetString(rx.Buffer));
            var header = request.Root!.Element(Soap12 + "Header")!;
            Assert.Equal(
                "http://schemas.xmlsoap.org/ws/2005/04/discovery/Resolve",
                header.Element(Wsa04 + "Action")!.Value);
            Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery", header.Element(Wsa04 + "To")!.Value);

            var resolve = request.Root.Element(Soap12 + "Body")!.Element(Disc04 + "Resolve")!;
            Assert.Equal(
                "urn:uuid:cam-abc",
                resolve.Element(Wsa04 + "EndpointReference")!.Element(Wsa04 + "Address")!.Value);
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

    // ---- 多廠牌相容：單播 Probe 與多播群組 ----

    [Fact]
    public async Task ProbeUnicastAsync_ReceivesProbeMatch_FromKnownHost()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var payload = Encoding.UTF8.GetBytes(ProbeMatchXmlOnvif);

        var replyTask = Task.Run(async () =>
        {
            var rx = await peer.ReceiveAsync();
            var request = XDocument.Parse(Encoding.UTF8.GetString(rx.Buffer));
            // To 一律是探索 URN：gsoap 的 directed probe（SOAP_WSDD_TO_TS）就是這樣送，
            // 目標位址放在封包目的地即可，用位址塞 To 會被部分設備的 WS-Addressing 檢查擋掉。
            var to = request.Descendants().FirstOrDefault(e => e.Name.LocalName == "To")?.Value
                ?? throw new InvalidOperationException($"missing wsa:To in {request}");
            Assert.Equal("urn:schemas-xmlsoap-org:ws:2005:04:discovery", to);
            await peer.SendAsync(payload, rx.RemoteEndPoint);
        });

        var devices = await DiscoveryClient.ProbeUnicastAsync(
            "127.0.0.1", peerEndpoint.Port, TimeSpan.FromSeconds(3));
        await replyTask.WaitAsync(TimeSpan.FromSeconds(5));

        var device = Assert.Single(devices);
        Assert.Equal("http://192.168.1.64/onvif/device_service", device.HttpXAddr);
    }

    [Fact]
    public async Task ProbeUnicastAsync_NoResponse_ReturnsEmpty()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndpoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var devices = await DiscoveryClient.ProbeUnicastAsync(
            "127.0.0.1", peerEndpoint.Port, TimeSpan.FromSeconds(1));

        Assert.Empty(devices);
    }

    [Fact]
    public async Task ProbeUnicastAsync_UnknownHostName_ReturnsEmpty()
    {
        var devices = await DiscoveryClient.ProbeUnicastAsync(
            "host.invalid.", 3702, TimeSpan.FromMilliseconds(300));

        Assert.Empty(devices);
    }

    [Fact]
    public async Task ProbeUnicastAsync_HostNameNotIp_ResolvesViaDns()
    {
        // 手動輸入可為主機名而非 IP：必須先解析 DNS 再探測，無人回應也應正常結束而非例外。
        var devices = await DiscoveryClient.ProbeUnicastAsync(
            "localhost", 3702, TimeSpan.FromMilliseconds(200));

        Assert.NotNull(devices);
    }

    [Fact]
    public async Task DiscoverAsync_CompletesWithoutMulticastSupport()
    {
        // 所在網路無多播時不應拋出例外（CI／隔離網段常見）
        var devices = await DiscoveryClient.DiscoverAsync(TimeSpan.FromMilliseconds(200));

        Assert.NotNull(devices);
    }

    // ---- 手動輸入位址：候選端點探測（DiscoverByIpAsync） ----

    [Fact]
    public async Task DiscoverByIpAsync_FirstCandidateResponds_ReturnsItsXAddr()
    {
        // 手動輸入 IP 時，逐一以 GetDeviceInformation 驗證各廠牌常見路徑；
        // 第一個回應成功者即為可用端點——這是「找不到攝影機」最常見的來源路徑。
        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates("192.168.1.50");
        Assert.NotEmpty(candidates);

        var resolved = await DiscoveryClient.DiscoverByIpAsync(
            "192.168.1.50",
            serviceFactory: _ => new OnvifDeviceService(
                candidates[0],
                null,
                null,
                new RecordingHandler((_, _) => OnvifTestHttp.DeviceInfoXml),
                new OnvifClientOptions { RetryCount = 0 }));

        Assert.Equal(candidates[0], resolved);
        Assert.Contains("/onvif/device_service", resolved!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverByIpAsync_UnparseableAddress_ReturnsNullWithoutProbing()
    {
        var factoryCalls = 0;
        var resolved = await DiscoveryClient.DiscoverByIpAsync(
            string.Empty,
            serviceFactory: _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("位址無法解析時不應建立任何候選服務");
            });

        Assert.Null(resolved);
        Assert.Equal(0, factoryCalls);
    }
}
