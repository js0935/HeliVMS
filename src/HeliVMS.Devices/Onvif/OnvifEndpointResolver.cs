namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 多廠牌 ONVIF 端點解析：手動輸入 IP／主機名時，產生各廠牌常見之
/// device_service／Media／PTZ 路徑與埠候選；並校正設備回報之 XAddr
/// （多數設備回報之 XAddr 未帶實際服務埠或使用別的主機名／網卡位址，直接使用會連線失敗）。
/// </summary>
public static class OnvifEndpointResolver
{
    /// <summary>各廠牌常見之 ONVIF 服務埠（遞增信心度；8000/8080/2020 為海康／大華／TVT 常見值）。</summary>
    public static IReadOnlyList<int> CommonPorts { get; } = [80, 8000, 8080, 2020, 8899, 34567, 5000];

    /// <summary>常見 device_service 路徑（依相容性排序：第一者為 ONVIF Profile S 慣例）。</summary>
    public static IReadOnlyList<string> DeviceServicePaths { get; } =
    [
        "/onvif/device_service",
        "/onvif/device_service?device_type=NetworkCamera",
        "/onvif/DeviceService",
        "/onvif/services",
        "/Services",
        "/ipcam/service",
    ];

    /// <summary>常見 Media 服務路徑。</summary>
    public static IReadOnlyList<string> MediaServicePaths { get; } =
    [
        "/onvif/Media",
        "/onvif/media_service",
        "/onvif/Media2",
        "/Media",
        "/onvif/Media?device_type=NetworkCamera",
    ];

    /// <summary>常見 PTZ 服務路徑。</summary>
    public static IReadOnlyList<string> PtzServicePaths { get; } =
    [
        "/onvif/ptz_service",
        "/onvif/PTZService",
        "/onvif/PTZ",
        "/onvif/ptz",
    ];

    /// <summary>
    /// 依手動輸入（<c>192.168.1.64</c>、<c>192.168.1.64:8000</c>、
    /// <c>http://cam.local/onvif/device_service</c>）產生待試之 device_service 候選，
    /// 依相容性排序：最可能者在前；指定埠優先，其餘為 <see cref="CommonPorts"/>。
    /// </summary>
    public static IReadOnlyList<string> BuildDeviceServiceCandidates(string address)
    {
        if (!TryParseHostInput(address, out var host, out var port, out var scheme, out var path))
        {
            return [];
        }

        // 使用者已提供明確服務路徑：信任輸入並置於最前。
        if (path.Length > 1)
        {
            return [Build(host, port ?? 80, scheme, path)];
        }

        var ports = new List<int>();
        if (port is not null)
        {
            ports.Add(port.Value);
        }

        foreach (var candidate in CommonPorts)
        {
            if (port is null || candidate != port.Value)
            {
                ports.Add(candidate);
            }
        }

        var results = new List<string>(ports.Count * DeviceServicePaths.Count);
        foreach (var candidatePort in ports)
        {
            results.Add(Build(host, candidatePort, scheme, DeviceServicePaths[0]));
        }

        foreach (var candidatePort in ports)
        {
            foreach (var pathTemplate in DeviceServicePaths.Skip(1))
            {
                results.Add(Build(host, candidatePort, scheme, pathTemplate));
            }
        }

        return results;
    }

    /// <summary>以已知 device service 位址推導某類服務之候選 XAddr。</summary>
    public static IReadOnlyList<string> BuildServiceCandidates(string deviceXAddr, IReadOnlyList<string> paths)
    {
        if (!TryParseHostInput(deviceXAddr, out var host, out var port, out var scheme, out _))
        {
            return [];
        }

        var results = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            results.Add(Build(host, port ?? DefaultPort(scheme), scheme, path));
        }

        return results;
    }

    /// <summary>
    /// 校正設備回報之服務 XAddr：主機與本次連線不同時改用連線位址；
    /// XAddr 使用預設埠而 device service 位於非預設埠時，採用實際服務埠。
    /// 無法解析時原樣回傳（交由呼叫端判斷）。
    /// </summary>
    public static string NormalizeServiceXAddr(string? xAddr, string deviceXAddr)
    {
        if (string.IsNullOrWhiteSpace(xAddr))
        {
            return string.Empty;
        }

        var raw = xAddr.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var service) ||
            !Uri.TryCreate(deviceXAddr, UriKind.Absolute, out var device))
        {
            return raw;
        }

        if (service.Host.Equals(device.Host, StringComparison.OrdinalIgnoreCase))
        {
            // 同一主機：XAddr 未帶服務埠而 device service 位於非預設埠時，採用實際服務埠。
            return service.IsDefaultPort && !device.IsDefaultPort
                ? Build(TrimBrackets(service.Host), device.Port, service.Scheme, service.PathAndQuery)
                : raw;
        }

        var builder = new UriBuilder(service)
        {
            Host = TrimBrackets(device.Host),
            Port = service.IsDefaultPort ? device.Port : service.Port,
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>取出使用者輸入之位址主機部分（可為 IP、IPv6 或主機名稱；無法解析時回傳 null）。</summary>
    public static string? ExtractHost(string? address) =>
        TryParseHostInput(address, out var host, out _, out _, out _) ? host : null;

    /// <summary>展開環境變數／使用者輸入之位址；回傳主機、指定埠（未指定為 null）、scheme 與路徑。</summary>
    internal static bool TryParseHostInput(
        string? address,
        out string host,
        out int? port,
        out string scheme,
        out string path)
    {
        host = string.Empty;
        port = null;
        scheme = "http";
        path = "/";
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var value = address.Trim();

        // 無 scheme 之下沉 IPv6（::1）需補上中括號才可解析。
        if (!value.Contains("://", StringComparison.Ordinal) && CountColons(value) > 1 && !value.StartsWith('['))
        {
            value = $"[{value}]";
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"http://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        host = TrimBrackets(uri.Host);
        scheme = uri.Scheme;
        path = uri.AbsolutePath.Length > 1 || uri.Query.Length > 0 ? uri.PathAndQuery : "/";
        port = uri.IsDefaultPort ? null : uri.Port;
        return host.Length > 0;
    }

    /// <summary>Uri.Host 之 IPv6 會含中括號，建構位址前需去除以免重複加括號。</summary>
    private static string TrimBrackets(string host) =>
        host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

    private static int CountColons(string value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (character == ':')
            {
                count++;
            }
        }

        return count;
    }

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static string Build(string host, int port, string scheme, string path)
    {
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var portPart = port == DefaultPort(scheme)
            ? string.Empty
            : $":{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return $"{scheme}://{authority}{portPart}{path}";
    }
}
