namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 「這台已經在系統裡了嗎」：把資料庫裡既有的設備 IP 與頻道位址整理成一組主機名，
/// 供搜尋結果標示「已加入」。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要標：重掃一次網段是常態（補一台、換一台、確認誰還沒進來），而清單上十幾台長得
/// 一模一樣。沒有這個標示，操作員只能回頭比對設備清單，或者更糟——把已經在錄的攝影機
/// 再加一次，於是同一台多出第二個頻道，時間軸上兩個頻道各錄一半。
/// </para>
/// <para>
/// 比對用**主機名**而不是完整位址：同一台攝影機的 ONVIF XAddr、RTSP 位址與 devices.ip
/// 可能寫成不同形式（帶埠、帶帳密、不同路徑），但主機是同一個。
/// </para>
/// </remarks>
public static class OnvifKnownDevices
{
    /// <summary>
    /// 組出既有的主機集合（來源可以是 <c>devices.ip</c> 與各頻道 <c>main_rtsp</c>；
    /// 空值與無法解析者一律略過）。
    /// </summary>
    public static HashSet<string> Build(
        IEnumerable<string?> deviceIps,
        IEnumerable<string?> rtspUrls)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in deviceIps)
        {
            Add(hosts, ip);
        }

        foreach (var url in rtspUrls)
        {
            Add(hosts, HostOf(url));
        }

        return hosts;
    }

    /// <summary>這台探索到的設備是否已在系統裡（以 XAddr 的主機名比對）。</summary>
    public static bool IsKnown(HashSet<string> knownHosts, DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(knownHosts);
        ArgumentNullException.ThrowIfNull(device);

        var host = OnvifEndpointResolver.ExtractHost(device.HttpXAddr ?? device.EndpointAddress);
        return host is not null && knownHosts.Contains(host);
    }

    /// <summary>RTSP 位址的主機名（可含帳密與埠；無法解析時回 null）。</summary>
    public static string? HostOf(string? rtspUrl)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            return null;
        }

        return Uri.TryCreate(rtspUrl.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : null;
    }

    private static void Add(HashSet<string> hosts, string? host)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            hosts.Add(host.Trim());
        }
    }
}
