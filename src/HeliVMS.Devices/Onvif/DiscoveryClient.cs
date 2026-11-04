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
        udp.EnableBroadcast = true;

        // 部分環境（VPN、受限 CI、已停用 IPv4 多播）不允許設定多播 socket 選項，
        // 此時仍可送出探測並等待回應，故僅忽略設定失敗。
        try
        {
            udp.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.MulticastTimeToLive,
                1);
        }
        catch (SocketException)
        {
        }

        var address = IPAddress.Parse(DiscoveryAddress);
        try
        {
            // 加入多播群組可提升 ProbeMatch 接收率（部分網路/VLAN 未加入仍可收到單播回應）
            udp.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(address, IPAddress.Any));
        }
        catch (SocketException)
        {
        }

        var endpoint = new IPEndPoint(address, DiscoveryPort);
        return await ProbeAsync(udp, endpoint, timeout, cancellationToken);
    }

    /// <summary>
    /// 單播 WS-Discovery 探測：多播被封鎖（VLAN／VPN／企業 Wi-Fi）時之主要路徑。
    /// 對指定主機與埠送出 〈Probe〉並收集 〈ProbeMatch〉。
    /// </summary>
    /// <param name="host">設備 IP 位址或主機名稱（主機名稱會先解析）。</param>
    /// <param name="port">WS-Discovery 埠（慣例 3702；部分 NVR 使用 80/8080/2020 等）。</param>
    /// <param name="timeout">等待回應時間。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> ProbeUnicastAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, cancellationToken);
        if (address is null)
        {
            return [];
        }

        using var udp = new UdpClient(address.AddressFamily);
        var endpoint = new IPEndPoint(address, port);

        // 保持未連線（unconnected）狀態，ProbeAsync 才可對任意端點重複送出探測。
        return await ProbeAsync(udp, endpoint, $"{host}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}", timeout, cancellationToken);
    }

    /// <summary>
    /// 依手動輸入之位址（IP／主機名／完整 URL）尋找可用的 ONVIF device service 端點：
    /// 逐一試探各廠牌常見路徑與埠並以 <c>GetDeviceInformation</c> 驗證；
    /// 全部失敗時再以單播 WS-Discovery 探測。成功回傳設備 XAddr。
    /// </summary>
    /// <param name="address">使用者輸入之位址（IP／主機名／完整 URL）。</param>
    /// <param name="username">ONVIF 帳號；未提供時以匿名探測。</param>
    /// <param name="password">ONVIF 密碼。</param>
    /// <param name="options">連線選項；未提供時使用預設值。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <param name="serviceFactory">
    /// 建立候選端點服務之工廠；預設使用真實 <see cref="OnvifDeviceService"/>。
    /// 測試可注入以假 HttpMessageHandler 建構的服務，無需真實設備。
    /// </param>
    public static async Task<string?> DiscoverByIpAsync(
        string address,
        string? username = null,
        string? password = null,
        OnvifClientOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<string, OnvifDeviceService>? serviceFactory = null)
    {
        serviceFactory ??= xAddr => new OnvifDeviceService(xAddr, username, password, options);

        var candidates = OnvifEndpointResolver.BuildDeviceServiceCandidates(address);
        if (candidates.Count == 0)
        {
            return null;
        }

        string? unauthorizedCandidate = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var service = serviceFactory(candidate);
            try
            {
                _ = await service.GetInfoAsync(cancellationToken);
                return candidate;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OnvifException ex) when (ex.Code == OnvifErrorCode.Unauthorized)
            {
                // 端點存在但需要認證：保留為最終回報候選（讓 UI 顯示認證錯誤而非找不到設備）
                unauthorizedCandidate ??= candidate;
            }
            catch (OnvifException)
            {
                // 其餘錯誤（連線、逾時、格式）視為此路徑無效，繼續下一候選
            }
        }

        if (TryUnicastProbeTargets(candidates[0], out var probeHost, out var probePorts))
        {
            var deadline = DateTime.UtcNow + UnicastProbeBudget;
            foreach (var port in probePorts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                IReadOnlyList<DiscoveredDevice> matches;
                try
                {
                    matches = await ProbeUnicastAsync(probeHost, port, UnicastProbeTimeout, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SocketException)
                {
                    continue;
                }

                foreach (var xAddr in matches.Select(m => m.HttpXAddr).OfType<string>())
                {
                    using var service = serviceFactory(xAddr);
                    try
                    {
                        _ = await service.GetInfoAsync(cancellationToken);
                        return xAddr;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (OnvifException)
                    {
                    }
                }
            }
        }

        return unauthorizedCandidate;
    }

    private static readonly TimeSpan UnicastProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnicastProbeBudget = TimeSpan.FromSeconds(8);

    /// <summary>掃描用的短逾時：單次 SOAP 請求 2 秒、不重試、不做時鐘校正。</summary>
    /// <remarks>
    /// 掃描問的是「這台在不在」，不是「這台的全部服務」；<see cref="OnvifClientOptions.Timeout"/>
    /// 的預設 10 秒乘上候選數量，單一 IP 就可能花上數分鐘。
    /// </remarks>
    private static readonly OnvifClientOptions SweepOptions = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
        RetryCount = 0,
        AutoClockSkew = false,
    };

    /// <summary>掃描時嘗試的 ONVIF 服務埠（80 為 Profile S 慣例；其餘為海康／大華／TVT 常見值）。</summary>
    private static readonly int[] SweepPorts = [80, 8000, 8080, 2020];

    /// <summary>掃描時單一位址的 WS-Discovery 等待時間。</summary>
    private static readonly TimeSpan SweepProbeTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 網段掃描用的單一位址探測：先單播 WS-Discovery（最快、且直接拿到設備真正的 XAddr），
    /// 沒有回應再對常見 ONVIF 埠用短逾時試一次 <c>device_service</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意不使用 <see cref="DiscoverByIpAsync"/>：它為了「單一台、一定要找到」而逐一嘗試
    /// 42 個埠×路徑候選，每個候選 10 秒逾時，單一 IP 就可能花掉幾分鐘——掃一個 /24 等於整晚。
    /// </para>
    /// <para>
    /// <b>401 未授權也算找到</b>：設備確實在，只是還沒給帳密；回報它是為了讓操作員在清單上
    /// 看到「這裡有一台」，而不是對著一個空清單繼續懷疑網路。設備名在未授權時拿不到，
    /// 清單就只有位址。
    /// </para>
    /// </remarks>
    /// <param name="address">IP 或主機名（可含埠，如 <c>192.168.1.64:8000</c>）。</param>
    /// <param name="username">ONVIF 帳號；未提供時以匿名探測。</param>
    /// <param name="password">ONVIF 密碼。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <param name="serviceFactory">建立候選端點服務之工廠（測試注入）。</param>
    /// <param name="ports">自訂嘗試的埠；未提供時用 <see cref="SweepPorts"/>。</param>
    public static async Task<DiscoveredDevice?> ProbeAddressAsync(
        string address,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default,
        Func<string, OnvifDeviceService>? serviceFactory = null,
        IReadOnlyList<int>? ports = null)
    {
        if (!OnvifEndpointResolver.TryParseHostInput(address, out var host, out var explicitPort, out _, out _))
        {
            return null;
        }

        serviceFactory ??= xAddr => new OnvifDeviceService(xAddr, username, password, SweepOptions);

        try
        {
            var matches = await ProbeUnicastAsync(host, DiscoveryPort, SweepProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (matches.FirstOrDefault(m => m.HttpXAddr is not null) is { } match)
            {
                return match;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException)
        {
            // 這個網路介面送不出單播探測：仍可繼續走 HTTP 候選。
        }

        foreach (var candidate in BuildSweepCandidates(host, explicitPort, ports))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var service = serviceFactory(candidate);
            try
            {
                var info = await service.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                return FromProbe(candidate, host, info.DisplayName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OnvifException ex) when (ex.Code == OnvifErrorCode.Unauthorized)
            {
                return FromProbe(candidate, host, name: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 這個埠沒有 ONVIF（連線被拒、逾時、回應不是 SOAP…）：換下一個。
            }
        }

        return null;
    }

    /// <summary>掃描候選：每個埠只試 Profile S 慣例路徑（<c>/onvif/device_service</c>）。</summary>
    private static IReadOnlyList<string> BuildSweepCandidates(
        string host,
        int? explicitPort,
        IReadOnlyList<int>? ports)
    {
        var candidates = new List<int>();
        if (explicitPort is { } specified)
        {
            candidates.Add(specified);
        }

        foreach (var port in ports ?? SweepPorts)
        {
            if (!candidates.Contains(port))
            {
                candidates.Add(port);
            }
        }

        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return [.. candidates.Select(port => port == 80
            ? $"http://{authority}/onvif/device_service"
            : $"http://{authority}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/onvif/device_service")];
    }

    /// <summary>
    /// 把「HTTP 探測成功」包成與 WS-Discovery 相同的形狀，讓清單可以直接顯示。
    /// 設備名借用 scope 的字面形狀（<see cref="DiscoveredDevice.NameHint"/> 讀的就是
    /// <c>name/…</c>）：掃描的結果要回答「哪一台」，一排 IP 沒有人認得出來。
    /// </summary>
    private static DiscoveredDevice FromProbe(string xAddr, string host, string? name)
        => new()
        {
            EndpointAddress = $"urn:probe:{host}",
            XAddrs = [xAddr],
            Scopes = string.IsNullOrWhiteSpace(name) ? [] : [$"onvif://www.onvif.org/name/{name}"],
            Types = "tdn:NetworkVideoTransmitter",
        };

    private static bool TryUnicastProbeTargets(
        string deviceXAddr,
        out string host,
        out IReadOnlyList<int> ports)
    {
        ports = [];
        if (!OnvifEndpointResolver.TryParseHostInput(deviceXAddr, out host, out var port, out _, out _))
        {
            return false;
        }

        var ordered = new List<int> { DiscoveryPort };
        foreach (var candidate in OnvifEndpointResolver.CommonPorts)
        {
            if (candidate != port && candidate != DiscoveryPort)
            {
                ordered.Add(candidate);
            }
        }

        ports = ordered;
        return true;
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var parsed))
        {
            return parsed;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.FirstOrDefault(a =>
                a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        }
        catch (SocketException)
        {
            return null;
        }
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
        CancellationToken cancellationToken = default) =>
        await ProbeAsync(udp, endpoint, $"{DiscoveryAddress}:{DiscoveryPort}", timeout, cancellationToken);

    /// <summary>
    /// 對指定端位址執行 WS-Discovery 探測（可指定 wsa:To；多播與單播共用）。
    /// </summary>
    internal static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(
        UdpClient udp,
        IPEndPoint endpoint,
        string to,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var envelope = BuildProbe(Guid.NewGuid().ToString("D"), to);

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

    private static string BuildProbe(string messageId, string to)
    {
        var doc = new XDocument(
            new XElement(WsdNs + "Probe",
                new XElement(WsaNs + "To", to),
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