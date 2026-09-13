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
    /// 於指定逾時內執行 WS-Discovery 探測，回傳所有收到之設備候選。
    /// </summary>
    /// <param name="timeoutMs">總探測時間（建議 ≥3000）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        TimeSpan timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var endpoint = new IPEndPoint(IPAddress.Parse(DiscoveryAddress), DiscoveryPort);
        var envelope = BuildProbe(Guid.NewGuid().ToString("D"));

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.MulticastTimeToLive, 1);
        udp.EnableBroadcast = true;

        var deadline = DateTime.UtcNow + timeoutMs;
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

    private static void ParseMatch(string response, IDictionary<string, DiscoveredDevice> results)
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