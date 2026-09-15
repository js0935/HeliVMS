namespace HeliVMS.Devices.Onvif;

/// <summary>ONVIF 設備基本資訊（GetDeviceInformation／GetSystemDateAndTime）。</summary>
public sealed class OnvifDeviceInfo
{
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string HardwareId { get; init; } = string.Empty;

    /// <summary>系統日期時間（證據時間基準，§21.1）。</summary>
    public DateTimeOffset? SystemDateTimeUtc { get; init; }

    /// <summary>日期時間是否由本機（非 NTP 校正）提供。</summary>
    public bool? DateTimeIsLocal { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ? Manufacturer : $"{Manufacturer} {Model}".Trim();
}

/// <summary>媒體 Profile（VideoSource/StreamUri 聚合，§16.2）。</summary>
public sealed class OnvifProfile
{
    public string Token { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string StreamUri { get; init; } = string.Empty;
}

/// <summary>WS-Discovery 探測結果（一次 ProbeMatch）。</summary>
public sealed class DiscoveredDevice
{
    public string EndpointAddress { get; init; } = string.Empty;
    public IReadOnlyList<string> XAddrs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public string Types { get; init; } = string.Empty;

    /// <summary>取用於 ONVIF HTTP 的 XAddr（第 1 個 http(s) 條目）。</summary>
    public string? HttpXAddr => XAddrs.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                           a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 依 scope 判別常見廠牌（ONVIF scope 之「onvif://www.onvif.org/name/…」型，
    /// 僅供介面展示，不保證精確）。
    /// </summary>
    public string? NameHint =>
        Scopes.Select(ParseScope)
              .FirstOrDefault(v => v.StartsWith("name/", StringComparison.Ordinal))
              ?["name/".Length..];

    private static string ParseScope(string scope)
    {
        var idx = scope.IndexOf("onvif.org/", StringComparison.Ordinal);
        return idx < 0 ? scope : scope[(idx + "onvif.org/".Length)..];
    }
}

/// <summary>PTZ 目前位置（GetStatus；平面座標，範圍約 -1..1）。</summary>
public sealed class PtzStatus
{
    public double Pan { get; init; }
    public double Tilt { get; init; }
    public double Zoom { get; init; }
}

/// <summary>PTZ 預設點（Preset token＋名稱）。</summary>
public sealed class PtzPreset
{
    public string Token { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}