using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// WS-Discovery（ONVIF §2.4 設備發現）：UDP 多播 239.255.255.250:3702
/// 送出 〈Probe〉，收集 3702 埠回應之 〈ProbeMatch〉。僅限本機網段（TTL=1）。
/// </summary>
public static class DiscoveryClient
{
    private const string DiscoveryAddress = "239.255.255.250";
    private const int DiscoveryPort = 3702;
    private const int UdpReadTimeoutMs = 500;

    private static readonly XNamespace WsaNs = "http://www.w3.org/2005/08/addressing";
    private static readonly XNamespace WsdNs = "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01";

    /// <summary>
    /// 於指定逾時內對 ONVIF 多播端位址執行 WS-Discovery 探測，
    /// 回傳所有收到之設備候選。
    /// </summary>
    /// <param name="timeout">總探測時間（建議 ≥3000 毫秒）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.MulticastTimeToLive,
            1);
        udp.EnableBroadcast = true;

        var endpoint = new IPEndPoint(IPAddress.Parse(DiscoveryAddress), DiscoveryPort);
        return await ProbeAsync(udp, endpoint, timeout, cancellationToken);
    }

    /// <summary>
    /// 送出 ONVIF Hello 公告（設備上線；多播 239.255.255.250:3702）。
    /// </summary>
    /// <param name="instanceId">設備 UUID（urn:uuid:{id}）。</param>
    /// <param name="xAddrs">可定址之 onvif/device_service 端點（單一值）。</param>
    /// <param name="types">設備類型（預設 tdn:NetworkVideoTransmitter）。</param>
    /// <param name="scopes">選用的探索範圍（逗號分隔）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static Task<bool> SendHelloAsync(
        string instanceId,
        string xAddrs,
        string types = "tdn:NetworkVideoTransmitter",
        string? scopes = null,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.MulticastTimeToLive,
            1);
        udp.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Parse(DiscoveryAddress), DiscoveryPort);
        return SendDiscoveryMessageAsync(udp, endpoint, BuildHello(instanceId, xAddrs, types, scopes), cancellationToken);
    }

    /// <summary>送出 ONVIF Bye 公告（設備離線；多播 TTL=1）。</summary>
    /// <param name="instanceId">設備 UUID（urn:uuid:{id}）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static Task<bool> SendByeAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.MulticastTimeToLive,
            1);
        udp.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Parse(DiscoveryAddress), DiscoveryPort);
        return SendDiscoveryMessageAsync(udp, endpoint, BuildBye(instanceId), cancellationToken);
    }

    /// <summary>以 Resolve 查詢特定 InstanceId 設備在位與否（回傳其 XAddrs 等資訊）。</summary>
    /// <param name="instanceId">設備 UUID（urn:uuid:{id}）。</param>
    /// <param name="timeout">總等待時間。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static async Task<DiscoveredDevice?> ResolveAsync(
        string instanceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.MulticastTimeToLive,
            1);
        udp.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Parse(DiscoveryAddress), DiscoveryPort);
        return await ResolveAsync(udp, endpoint, instanceId, timeout, cancellationToken);
    }

    /// <summary>
    /// 對指定端位址執行 WS-Discovery 探測（測試可注入 loopback 假設備與自備 UdpClient）。
    /// </summary>
    internal static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var envelope = BuildProbe(Guid.NewGuid().ToString("D"));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes(envelope);
                await udp.SendAsync(payload.AsMemory(), endpoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            while (DateTime.UtcNow < deadline)
            {
                if (udp.Available == 0)
                {
                    await Task.Delay(UdpReadTimeoutMs, cancellationToken);
                    continue;
                }

                try
                {
                    var result = await udp.ReceiveAsync(cancellationToken);
                    ParseMatch(Encoding.UTF8.GetString(result.Buffer), results);
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }

        return results.Values.ToList();
    }

    /// <summary>
    /// 送出 Hello 至指定端點（測試可注入 loopback 假接收端與自備 UdpClient）。
    /// </summary>
    internal static Task<bool> SendHelloAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        string instanceId,
        string xAddrs,
        string types = "tdn:NetworkVideoTransmitter",
        string? scopes = null,
        CancellationToken cancellationToken = default)
        => SendDiscoveryMessageAsync(udp, endpoint, BuildHello(instanceId, xAddrs, types, scopes), cancellationToken);

    /// <summary>送出 Bye 至指定端點（測試注入）。</summary>
    internal static Task<bool> SendByeAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        string instanceId,
        CancellationToken cancellationToken = default)
        => SendDiscoveryMessageAsync(udp, endpoint, BuildBye(instanceId), cancellationToken);

    /// <summary>
    /// 對指定端點執行 Resolve，收集 ResolveMatch（比對 EndpointReference）；
    /// 逾時回 null。測試可注入 loopback 假設備與自備 UdpClient。
    /// </summary>
    internal static async Task<DiscoveredDevice?> ResolveAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        string instanceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var envelope = BuildResolve(instanceId);
        var expected = $"urn:uuid:{instanceId}";

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await SendDiscoveryMessageAsync(udp, endpoint, envelope, cancellationToken))
            {
                break;
            }

            while (DateTime.UtcNow < deadline)
            {
                if (udp.Available == 0)
                {
                    await Task.Delay(UdpReadTimeoutMs, cancellationToken);
                    continue;
                }

                try
                {
                    var result = await udp.ReceiveAsync(cancellationToken);
                    var match = ParseResolveMatch(Encoding.UTF8.GetString(result.Buffer), expected);
                    if (match is not null)
                    {
                        return match;
                    }
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }

        return null;
    }

    private static async Task<bool> SendDiscoveryMessageAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        string envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(envelope);
            await udp.SendAsync(payload.AsMemory(), endpoint, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string BuildHello(string instanceId, string xAddrs, string types, string? scopes)
    {
        var hello = new XElement(WsdNs + "Hello",
            new XElement(WsaNs + "EndpointReference",
                new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}")),
            new XElement(WsdNs + "Types", types));
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            hello.Add(new XElement(WsdNs + "Scopes", scopes));
        }

        hello.Add(
            new XElement(WsdNs + "XAddrs", xAddrs),
            new XElement(WsdNs + "MetadataVersion", "1"));
        return new XDocument(hello).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildBye(string instanceId)
        => new XDocument(
            new XElement(WsdNs + "Bye",
                new XElement(WsaNs + "EndpointReference",
                    new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}"))))
            .ToString(SaveOptions.DisableFormatting);

    private static string BuildResolve(string instanceId)
        => new XDocument(
            new XElement(WsdNs + "Resolve",
                new XElement(WsaNs + "To", $"{DiscoveryAddress}:{DiscoveryPort}"),
                new XElement(WsaNs + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Resolve"),
                new XElement(WsaNs + "MessageID", $"urn:uuid:{Guid.NewGuid():D}"),
                new XElement(WsdNs + "EndpointReference",
                    new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}"))))
            .ToString(SaveOptions.DisableFormatting);

    private static DiscoveredDevice? ParseResolveMatch(string response, string expectedInstance)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(response);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        foreach (var match in doc.Descendants(WsdNs + "ResolveMatch"))
        {
            var addr = match.Element(WsaNs + "EndpointReference")?.Element(WsaNs + "Address")?.Value ?? string.Empty;
            if (addr == expectedInstance)
            {
                var xaddrs = match.Element(WsdNs + "XAddrs")?.Value ?? string.Empty;
                if (string.IsNullOrEmpty(xaddrs))
                {
                    continue;
                }

                return new DiscoveredDevice
                {
                    EndpointAddress = addr,
                    XAddrs = SplitSpaceSeparated(xaddrs),
                    Scopes = SplitSpaceSeparated(match.Element(WsdNs + "Scopes")?.Value ?? string.Empty),
                    Types = match.Element(WsdNs + "Types")?.Value ?? string.Empty,
                };
            }
        }

        return null;
    }

    private static string BuildProbe(string messageId)
    {
        var doc = new XDocument(
            new XElement(WsdNs + "Probe",
                new XElement(WsaNs + "To", $"{DiscoveryAddress}:{DiscoveryPort}"),
                new XElement(WsaNs + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe"),
                new XElement(WsaNs + "MessageID", $"urn:uuid:{messageId}"),
                new XElement(WsdNs + "Types", "tdn:NetworkVideoTransmitter")));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    internal static void ParseMatch(string response, IDictionary<string, DiscoveredDevice> results)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(response);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }

        foreach (var match in doc.Descendants(WsdNs + "ProbeMatch"))
        {
            var xaddrs = match.Element(WsdNs + "XAddrs")?.Value ?? string.Empty;
            var addr = match.Element(WsaNs + "EndpointReference")?.Element(WsaNs + "Address")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(xaddrs))
            {
                continue;
            }

            var scopes = match.Element(WsdNs + "Scopes")?.Value ?? string.Empty;
            var device = new DiscoveredDevice
            {
                EndpointAddress = addr,
                XAddrs = SplitSpaceSeparated(xaddrs),
                Scopes = SplitSpaceSeparated(scopes),
                Types = match.Element(WsdNs + "Types")?.Value ?? string.Empty,
            };

            if (device.HttpXAddr is not null)
            {
                results[device.HttpXAddr] = device;
            }
        }
    }

    private static IReadOnlyList<string> SplitSpaceSeparated(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}