using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// WS-Discovery（ONVIF 核心規範 §5.1 設備發現）：UDP 多播 239.255.255.250:3702
/// 送出 SOAP 封套的 〈Probe〉，收集 3702 埠回應之 〈ProbeMatch〉。僅限本機網段（TTL=1）。
/// </summary>
/// <remarks>
/// 訊息格式依 ONVIF 核心規範 §5.1：SOAP 1.2 封套、WS-Discovery 1.0（2005/04）、
/// WS-Addressing 2004/08、Types＝<c>dn:NetworkVideoTransmitter</c>。
/// 回應端（<see cref="ParseMatch"/>）刻意採命名空間無關解析，2009/01（WS-Discovery 1.1）
/// 的設備也能收進清單——現場機隊兩種都有。
/// </remarks>
public static class DiscoveryClient
{
    private const string DiscoveryAddress = "239.255.255.250";
    private const int DiscoveryPort = 3702;
    private const int UdpReadTimeoutMs = 500;

    /// <summary>
    /// 探測重送間隔：UDP 沒有重傳，一次 5 秒的掃描只送一個封包在現場等同賭運氣
    /// （Wi-Fi／企業網路掉一個就整批找不到）。重送沿用同一個 MessageID，
    /// 重複的 ProbeMatch 由 HttpXAddr 去重，不影響結果。
    /// </summary>
    private const int ProbeResendIntervalMs = 1000;

    /// <summary>WS-Addressing 命名空間（ONVIF／WS-Discovery 1.0 用 2004/08；2009/01 的 1.1 才用 2005/08）。</summary>
    private static readonly XNamespace WsaNs = "http://schemas.xmlsoap.org/ws/2004/08/addressing";

    /// <summary>
    /// 探測本體的命名空間：ONVIF 核心規範 §5.1 指定 WS-Discovery 1.0
    /// （<c>http://schemas.xmlsoap.org/ws/2005/04/discovery</c>）。
    /// </summary>
    /// <remarks>
    /// 舊寫法用 2009/01（WS-Discovery 1.1）**而且** Action 用 2005/04 的 URI，兩者不一致，
    /// 再加上送出的根本不是 SOAP 封套——真實攝影機一律不回應。2009/01 的設備改由解析端的
    /// 命名空間無關解析支援（見 <see cref="ParseMatch"/>）。
    /// </remarks>
    private static readonly XNamespace DiscNs = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    /// <summary>SOAP 1.2 封套命名空間。</summary>
    private static readonly XNamespace SoapNs = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>
    /// WS-Discovery 的探索 URN：Probe／Resolve 的 <c>wsa:To</c> 一律用它。
    /// </summary>
    /// <remarks>
    /// 連**單播**探測也用 URN 而非目標位址——這是 gsoap（絕大多數 ONVIF 設備的堆疊，
    /// 見 <c>soap_wsdd_Probe</c> 的 <c>SOAP_WSDD_TO_TS</c> 分支）與 ONVIF Device Manager
    /// 的行為。舊程式把多播位址或 <c>host:port</c> 塞進 To，嚴格檢查的設備會直接忽略。
    /// </remarks>
    private const string DiscoveryUrn = "urn:schemas-xmlsoap-org:ws:2005:04:discovery";

    /// <summary>ONVIF 設備類型 QName 前綴 <c>dn</c> 的命名空間（Types 必須綁定它，QName 才可解析）。</summary>
    private const string NetworkTypeNs = "http://www.onvif.org/ver10/network/wsdl";

    /// <summary>Probe 的 Types 值（ONVIF 核心規範 §5.1 指定的設備類型）。</summary>
    private const string NetworkVideoTransmitter = "dn:NetworkVideoTransmitter";

    private const string ProbeAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe";
    private const string ResolveAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Resolve";
    private const string HelloAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Hello";
    private const string ByeAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Bye";

    /// <summary>
    /// 於指定逾時內對 ONVIF 多播端位址執行 WS-Discovery 探測，
    /// 回傳所有收到之設備候選。
    /// </summary>
    /// <param name="timeout">總探測時間（建議 ≥3000 毫秒）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <param name="localAddresses">
    /// 要從哪些來源位址送出探測（預設＝本機所有可多播的 IPv4 介面，見
    /// <see cref="MulticastSourceAddresses"/>）。測試可注入以固定來源。
    /// </param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<IPAddress>>? localAddresses = null)
    {
        var sources = (localAddresses ?? MulticastSourceAddresses.Enumerate)();
        if (sources.Count == 0)
        {
            sources = [IPAddress.Any];
        }

        // 逐來源位址各送一輪，再合併去重。多網卡的機器只有預設路由那個介面送得出多播，
        // 不逐介面的話另一邊的攝影機永遠找不到，而畫面上沒有任何錯誤。
        if (sources.Count > 1)
        {
            var perSource = await Task.WhenAll(sources.Select(
                source => ProbeFromSourceAsync(source, timeout, cancellationToken))).ConfigureAwait(false);

            return Merge(perSource);
        }

        return await ProbeFromSourceAsync(sources[0], timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>逐來源位址探測（單一來源時就是原本的行為）。</summary>
    private static async Task<IReadOnlyList<DiscoveredDevice>> ProbeFromSourceAsync(
        IPAddress source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var udp = IPAddress.Any.Equals(source) ? new UdpClient() : new UdpClient(new IPEndPoint(source, 0));
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
            // 加入多播群組可提升 ProbeMatch 接收率（部分網路/VLAN 未加入仍可收到單播回應）。
            // 逐介面加入：綁定來源位址的 socket 要指定同一張網卡，否則群組加在別張上、回應收不到。
            udp.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                IPAddress.Any.Equals(source)
                    ? new MulticastOption(address, IPAddress.Any)
                    : new MulticastOption(address, source));
        }
        catch (SocketException)
        {
        }

        var endpoint = new IPEndPoint(address, DiscoveryPort);
        return await ProbeAsync(udp, endpoint, timeout, cancellationToken);
    }

    /// <summary>
    /// 合併多個來源位址的探測結果：同一台設備（裝置 UUID 或 XAddr 相同）只留一筆，
    /// 順序沿用來源順序（第一個回報它的那個介面），讓清單穩定可預期。
    /// </summary>
    /// <remarks>
    /// 身分判準與掃描共用 <see cref="OnvifDiscoveryIdentity"/>：兩邊各寫一份，
    /// 遲早出現「多播看到一台、掃描看到兩台」這種同一台被算兩次的情況。
    /// </remarks>
    public static IReadOnlyList<DiscoveredDevice> Merge(
        IEnumerable<IReadOnlyList<DiscoveredDevice>> perSource)
    {
        ArgumentNullException.ThrowIfNull(perSource);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<DiscoveredDevice>();
        foreach (var devices in perSource)
        {
            foreach (var device in devices)
            {
                if (seen.Add(OnvifDiscoveryIdentity.KeyOf(device)))
                {
                    merged.Add(device);
                }
            }
        }

        return merged;
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
        // To 仍用探索 URN（不是目標位址）：gsoap 的 directed probe（SOAP_WSDD_TO_TS）就是這樣送，
        // 用位址會被部分設備的 WS-Addressing 檢查擋掉。
        return await ProbeAsync(udp, endpoint, DiscoveryUrn, timeout, cancellationToken);
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

    /// <summary>
    /// 掃描時嘗試的 ONVIF 服務埠（與 <see cref="OnvifEndpointResolver.CommonPorts"/> 同一份清單：
    /// 80 為 Profile S 慣例，其餘為海康／大華／TVT 常見值）。清單可以放大是因為 TCP 預檢
    /// 已經把「沒有人在聽」的埠濾掉——逐埠花兩秒做 ONVIF 探測的舊寫法才需要限制數量。
    /// </summary>
    private static readonly int[] SweepPorts = [.. OnvifEndpointResolver.CommonPorts];

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

        // 先做 TCP 預檢：沒有回應的位址只等一次連線逾時，而不是每個埠各等一次 ONVIF 逾時；
        // 掃到的埠才值得花兩秒做 ONVIF 探測（也讓非標準埠的清單可以放大而不拖慢掃描）。
        var candidatesByPort = BuildSweepCandidates(host, explicitPort, ports);
        var openPorts = await OnvifPortScan
            .OpenPortsAsync(host, [.. candidatesByPort.Keys], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (openPorts.Count == 0)
        {
            return null;
        }

        foreach (var port in openPorts)
        {
            var candidate = candidatesByPort[port];
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

    /// <summary>
    /// 掃描候選：每個埠只試 Profile S 慣例路徑（<c>/onvif/device_service</c>），
    /// 以埠號為鍵（TCP 預檢回傳埠號，之後據此取回候選 URL）。
    /// </summary>
    private static IReadOnlyDictionary<int, string> BuildSweepCandidates(
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
        return candidates.ToDictionary(
            port => port,
            port => port == 80
                ? $"http://{authority}/onvif/device_service"
                : $"http://{authority}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/onvif/device_service");
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
            Types = NetworkVideoTransmitter,
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
    /// <param name="types">設備類型（預設 <c>dn:NetworkVideoTransmitter</c>）。</param>
    /// <param name="scopes">選用的探索範圍（逗號分隔）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public static Task<bool> SendHelloAsync(
        string instanceId,
        string xAddrs,
        string types = NetworkVideoTransmitter,
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
        await ProbeAsync(udp, endpoint, DiscoveryUrn, timeout, cancellationToken);

    /// <summary>
    /// 對指定端位址執行 WS-Discovery 探測（可指定 wsa:To；多播與單播共用）。
    /// 探測每 <see cref="ProbeResendIntervalMs"/> 重送一次直到逾時，回應持續累積。
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

            // 只等一個重送間隔就再送，而不是一路等到逾時：單次探測掉封包＝整批找不到。
            var nextProbe = DateTime.UtcNow + TimeSpan.FromMilliseconds(ProbeResendIntervalMs);
            while (DateTime.UtcNow < deadline && DateTime.UtcNow < nextProbe)
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
        string types = NetworkVideoTransmitter,
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
        var hello = new XElement(DiscNs + "Hello",
            new XElement(WsaNs + "EndpointReference",
                new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}")),
            new XElement(DiscNs + "Types", types));
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            hello.Add(new XElement(DiscNs + "Scopes", scopes));
        }

        hello.Add(
            new XElement(DiscNs + "XAddrs", xAddrs),
            new XElement(DiscNs + "MetadataVersion", "1"));
        return BuildEnvelope(HelloAction, $"urn:uuid:{Guid.NewGuid():D}", DiscoveryUrn, hello);
    }

    private static string BuildBye(string instanceId)
        => BuildEnvelope(
            ByeAction,
            $"urn:uuid:{Guid.NewGuid():D}",
            DiscoveryUrn,
            new XElement(DiscNs + "Bye",
                new XElement(WsaNs + "EndpointReference",
                    new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}"))));

    private static string BuildResolve(string instanceId)
        => BuildEnvelope(
            ResolveAction,
            $"urn:uuid:{Guid.NewGuid():D}",
            DiscoveryUrn,
            new XElement(DiscNs + "Resolve",
                new XElement(WsaNs + "EndpointReference",
                    new XElement(WsaNs + "Address", $"urn:uuid:{instanceId}"))));

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

        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ResolveMatch"))
        {
            // 缺 EndpointReference 的畸形回應直接跳過（舊寫法用 First()，會在這裡拋例外）。
            var addr = Child(match, "EndpointReference") is { } endpoint
                ? Child(endpoint, "Address")?.Value ?? string.Empty
                : string.Empty;
            if (addr == expectedInstance)
            {
                var xaddrs = Child(match, "XAddrs")?.Value ?? string.Empty;
                if (string.IsNullOrEmpty(xaddrs))
                {
                    continue;
                }

                return new DiscoveredDevice
                {
                    EndpointAddress = addr,
                    XAddrs = SplitSpaceSeparated(xaddrs),
                    Scopes = SplitSpaceSeparated(Child(match, "Scopes")?.Value ?? string.Empty),
                    Types = Child(match, "Types")?.Value ?? string.Empty,
                };
            }
        }

        return null;
    }

    private static string BuildProbe(string messageId, string to)
        => BuildEnvelope(
            ProbeAction,
            $"urn:uuid:{messageId}",
            to,
            new XElement(DiscNs + "Probe",
                new XElement(DiscNs + "Types",
                    // 明確綁定 dn 前綴：Types 是 QName，嚴格解析的設備會找不到前綴就忽略探測。
                    new XAttribute(XNamespace.Xmlns + "dn", NetworkTypeNs),
                    NetworkVideoTransmitter)));

    /// <summary>
    /// 把 WS-Discovery 訊息包進 SOAP 1.2 封套，並把 WS-Addressing 標頭放進 Header。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 舊寫法直接送出**裸的** <c>&lt;Probe&gt;</c> 元素——不是 SOAP 封套，真實攝影機的
    /// 剖析器（gsoap 等）根本不會處理它；而測試的假攝影機太寬容（收到什麼都回），
    /// 所以這個缺陷一直沒被發現。現場都以手動 IP ＋ HTTP 探測註冊，多播搜尋從未成功。
    /// </para>
    /// <para>
    /// 命名空間依 ONVIF 核心規範 §5.1：訊息本體用
    /// <c>http://schemas.xmlsoap.org/ws/2005/04/discovery</c>（WS-Discovery 1.0），
    /// 定址用 <c>http://schemas.xmlsoap.org/ws/2004/08/addressing</c>，To 用探索 URN
    /// （連單播探測也是，見 <see cref="DiscoveryUrn"/>）。2009/01（WS-Discovery 1.1）
    /// 的設備由解析端的命名空間無關解析支援（見 <see cref="ParseMatch"/>）。
    /// </para>
    /// </remarks>
    private static string BuildEnvelope(string action, string messageId, string to, XElement body)
        => new XDocument(
            new XElement(SoapNs + "Envelope",
                // 固定前綴（s/a/d）與 ONVIF 實機封包一致：LINQ to XML 不指定時會自創前綴
                // （p1:…），命名空間雖正確，但讓 Wireshark 比對與少數做字串比對的設備都好過。
                new XAttribute(XNamespace.Xmlns + "s", SoapNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", WsaNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "d", DiscNs.NamespaceName),
                new XElement(SoapNs + "Header",
                    new XElement(WsaNs + "Action", action),
                    new XElement(WsaNs + "MessageID", messageId),
                    new XElement(WsaNs + "To", to)),
                new XElement(SoapNs + "Body", body)))
            .ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// 解析 ProbeMatch 回應並併入結果集（以 HttpXAddr 去重，後到者覆蓋）。
    /// </summary>
    /// <remarks>
    /// **命名空間無關**（只比 local-name）：現場同時有 WS-Discovery 1.0（2005/04，
    /// ONVIF 規範指定的版本）與 1.1（2009/01）的設備，定址命名空間也有 2004/08 與
    /// 2005/08 兩種，而 EndpointReference 曾被觀察到放在兩種命名空間。舊寫法寫死
    /// 2009/01＋2005/08，1.0 設備的回應會整批被丟掉——「掃得到但清單是空的」。
    /// </remarks>
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

        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = Child(match, "XAddrs")?.Value ?? string.Empty;
            var addr = Child(match, "EndpointReference") is { } endpoint
                ? Child(endpoint, "Address")?.Value ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(addr) || string.IsNullOrEmpty(xaddrs))
            {
                continue;
            }

            var device = new DiscoveredDevice
            {
                EndpointAddress = addr,
                XAddrs = SplitSpaceSeparated(xaddrs),
                Scopes = SplitSpaceSeparated(Child(match, "Scopes")?.Value ?? string.Empty),
                Types = Child(match, "Types")?.Value ?? string.Empty,
            };

            if (device.HttpXAddr is not null)
            {
                results[device.HttpXAddr] = device;
            }
        }
    }

    /// <summary>在元素底下找第一個 local-name 相符的子元素（命名空間無關）。</summary>
    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IReadOnlyList<string> SplitSpaceSeparated(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}