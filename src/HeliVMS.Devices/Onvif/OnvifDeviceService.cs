using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// ONVIF 設備服務（HTTP SOAP）：GetDeviceInformation／GetSystemDateAndTime／
/// GetProfiles／GetStreamUri。支援 WS-Security UsernameToken（Digest 或明碼）。
/// </summary>
public sealed class OnvifDeviceService : IDisposable
{
    internal static readonly XNamespace Env = "http://www.w3.org/2003/05/soap-envelope";
    internal static readonly XNamespace Td = "http://www.onvif.org/ver10/device/wsdl";
    internal static readonly XNamespace Trt = "http://www.onvif.org/ver10/media/wsdl";
    internal static readonly XNamespace Ts = "http://www.onvif.org/ver10/schema";
    internal static readonly XNamespace Tptz = "http://www.onvif.org/ver10/ptz/wsdl";
    internal static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";
    internal static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    internal static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    private readonly HttpClient _http;
    private readonly string? _username;
    private readonly string? _password;

    /// <summary>以設備 XAddr（慣例 http://ip/onvif/device_service）建立服務端點。</summary>
    public OnvifDeviceService(string deviceXAddr, string? username = null, string? password = null)
        : this(deviceXAddr, username, password, new HttpClientHandler())
    {
    }

    /// <summary>供注入自訂 HttpMessageHandler（單元測試／進階用途）。</summary>
    public OnvifDeviceService(
        string deviceXAddr,
        string? username,
        string? password,
        HttpMessageHandler handler)
    {
        DeviceXAddr = deviceXAddr;
        _username = username;
        _password = password;

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "HeliVMS/0.1 (ONVIF/ver10)");
    }

    public string DeviceXAddr { get; }

    /// <summary>Media 服務位址（首次呼叫 GetProfiles 前以 GetCapabilities 解析）。</summary>
    public string? MediaXAddr { get; private set; }

    /// <summary>PTZ 服務位址（首次呼叫 PTZ 動作前以 GetCapabilities Category=All 解析）。</summary>
    public string? PtzXAddr { get; private set; }

    /// <summary>設備是否具備 PTZ 能力（需先呼叫 <see cref="EnsurePtzCapabilityAsync"/>）。</summary>
    public bool HasPtz => !string.IsNullOrWhiteSpace(PtzXAddr);

    /// <summary>取得設備基本資訊與系統時間。</summary>
    public async Task<OnvifDeviceInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var body = await PostAsync(
            new XElement(Td + "GetDeviceInformation"), null,
            TdAction("GetDeviceInformation"), cancellationToken);

        var info = DescendantAnyNs(body, "GetDeviceInformationResponse");
        var dateTimeUtc = await GetSystemDateTimeUtcAsync(cancellationToken);

        return new OnvifDeviceInfo
        {
            Manufacturer = (string?)info?.ElementAnyNs("Manufacturer") ?? string.Empty,
            Model = (string?)info?.ElementAnyNs("Model") ?? string.Empty,
            FirmwareVersion = (string?)info?.ElementAnyNs("FirmwareVersion") ?? string.Empty,
            SerialNumber = (string?)info?.ElementAnyNs("SerialNumber") ?? string.Empty,
            HardwareId = (string?)info?.ElementAnyNs("HardwareId") ?? string.Empty,
            SystemDateTimeUtc = dateTimeUtc?.UtcDateTime,
        };
    }

    /// <summary>取得系統日期時間（UTC）；失敗回傳 null——僅證據輔助，不阻斷流程。</summary>
    public async Task<DateTimeOffset?> GetSystemDateTimeUtcAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await PostAsync(
                new XElement(Td + "GetSystemDateAndTime"), null,
                TdAction("GetSystemDateAndTime"), cancellationToken);

            var utc = DescendantAnyNs(body, "UTCDateTime");
            if (utc is null)
            {
                return null;
            }

            var date = utc.ElementAnyNs("Date")!;
            var time = utc.ElementAnyNs("Time")!;
            return new DateTimeOffset(
                (int)date.ElementAnyNs("Year")!,
                (int)date.ElementAnyNs("Month")!,
                (int)date.ElementAnyNs("Day")!,
                (int)time.ElementAnyNs("Hour")!,
                (int)time.ElementAnyNs("Minute")!,
                (int)time.ElementAnyNs("Second")!,
                TimeSpan.Zero);
        }
        catch (System.Net.Http.HttpRequestException)
        {
            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>取得全部媒體 Profile（token+name+RTSP 位址）。</summary>
    public async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMediaXAddrAsync(cancellationToken);
        var body = await PostAsync(
            new XElement(Trt + "GetProfiles"), MediaXAddr,
            TrtAction("GetProfiles"), cancellationToken);

        var profiles = new List<OnvifProfile>();
        foreach (var profile in DescendantsAnyNs(body, "Profiles"))
        {
            var token = (string?)profile.Attribute("token") ?? string.Empty;
            if (token.Length == 0)
            {
                continue;
            }

            profiles.Add(new OnvifProfile
            {
                Token = token,
                Name = (string?)profile.ElementAnyNs("Name") ?? token,
                StreamUri = await TryGetStreamUriAsync(token, cancellationToken) ?? string.Empty,
            });
        }

        return profiles;
    }

    /// <summary>解析 Media 服務位址（依 Profile XAddr 自 GetStreamUri 取得 RTSP 端點）。</summary>
    public async Task<string?> GetStreamUriAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        await EnsureMediaXAddrAsync(cancellationToken);
        try
        {
            var body = await PostAsync(
                new XElement(Trt + "GetStreamUri",
                    new XElement(Trt + "StreamSetup",
                        new XElement(Ts + "Stream", "RTP-Unicast"),
                        new XElement(Ts + "Transport",
                            new XElement(Ts + "Protocol", "RTSP"))),
                    new XElement(Trt + "ProfileToken", profileToken)),
                MediaXAddr,
                TrtAction("GetStreamUri"), cancellationToken);

            var uri = DescendantAnyNs(body, "Uri");
            if (uri is not null)
            {
                // 設備可能回傳主機名而非 IP——替換為本服務連線之 IP
                return NormalizeUriHost(uri.Value, DeviceXAddr);
            }
        }
        catch (System.Net.Http.HttpRequestException)
        {
        }
        catch (System.Xml.XmlException)
        {
        }

        return null;
    }

    /// <summary>確保已解析 PTZ 服務位址（GetCapabilities Category=All；不覆用 Media 快取）。</summary>
    public async Task EnsurePtzCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (PtzXAddr is not null)
        {
            return;
        }

        try
        {
            var body = await PostAsync(
                new XElement(Td + "GetCapabilities", new XElement(Td + "Category", "All")), null,
                TdAction("GetCapabilities"), cancellationToken);

            var ptzCaps = DescendantAnyNs(body, "Capabilities")?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "PTZ");
            if (ptzCaps is null)
            {
                ptzCaps = DescendantAnyNs(body, "PTZ");
            }

            PtzXAddr = (string?)ptzCaps?.ElementAnyNs("XAddr") ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            PtzXAddr = string.Empty;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            PtzXAddr = string.Empty;
        }
        catch (System.Xml.XmlException)
        {
            PtzXAddr = string.Empty;
        }
    }

    /// <summary>取得 PTZ 目前位置（GetStatus）。</summary>
    public async Task<PtzStatus> GetPtzStatusAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        var body = await PostAsync(
            new XElement(Tptz + "GetStatus", new XElement(Tptz + "ProfileToken", profileToken)),
            PtzXAddr,
            TptzAction("GetStatus"), cancellationToken);

        var position = DescendantAnyNs(body, "Position");
        return ParsePtzStatus(position ?? DescendantAnyNs(body, "PTZStatus"));
    }

    /// <summary>連續移動（Velocity 各軸 -1..1；約 4 秒後自動停止，避免設備持續運轉）。</summary>
    public async Task ContinuousMoveAsync(
        string profileToken,
        double pan,
        double tilt,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "ContinuousMove",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "Velocity",
                    new XElement(Ts + "PanTilt",
                        new XAttribute("x", pan.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("y", tilt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    new XElement(Ts + "Zoom",
                        new XAttribute("x", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new XElement(Tptz + "Timeout", "PT4S")),
            PtzXAddr,
            TptzAction("ContinuousMove"), cancellationToken);
    }

    /// <summary>停止 PTZ 移動（PanTilt＋Zoom 都停止）。</summary>
    public async Task StopPtzAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "Stop",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "PanTilt", "true"),
                new XElement(Tptz + "Zoom", "true")),
            PtzXAddr,
            TptzAction("Stop"), cancellationToken);
    }

    /// <summary>取得全部 PTZ 預設點。</summary>
    public async Task<IReadOnlyList<PtzPreset>> GetPtzPresetsAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        var body = await PostAsync(
            new XElement(Tptz + "GetPresets", new XElement(Tptz + "ProfileToken", profileToken)),
            PtzXAddr,
            TptzAction("GetPresets"), cancellationToken);

        var presets = new List<PtzPreset>();
        foreach (var preset in DescendantsAnyNs(body, "Preset"))
        {
            var token = (string?)preset.Attribute("token") ?? string.Empty;
            if (token.Length == 0)
            {
                continue;
            }

            presets.Add(new PtzPreset
            {
                Token = token,
                Name = (string?)preset.ElementAnyNs("Name") ?? token,
            });
        }

        return presets;
    }

    /// <summary>移至指定預設點（GotoPreset）。</summary>
    public async Task GotoPtzPresetAsync(string profileToken, string presetToken, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "GotoPreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "PresetToken", presetToken)),
            PtzXAddr,
            TptzAction("GotoPreset"), cancellationToken);
    }

    /// <summary>絕對移動（AbsoluteMove：設定絕對 Pan/Tilt/Zoom 位置）。</summary>
    public async Task AbsoluteMoveAsync(
        string profileToken,
        double pan,
        double tilt,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "AbsoluteMove",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "Position",
                    new XElement(Ts + "PanTilt",
                        new XAttribute("x", pan.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("y", tilt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    new XElement(Ts + "Zoom",
                        new XAttribute("x", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))))),
            PtzXAddr,
            TptzAction("AbsoluteMove"), cancellationToken);
    }

    /// <summary>返回 Home 位置（Home）。</summary>
    public async Task HomeAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "Home",
                new XElement(Tptz + "ProfileToken", profileToken)),
            PtzXAddr,
            TptzAction("Home"), cancellationToken);
    }

    /// <summary>移除 PTZ 預設點（RemovePreset）。</summary>
    public async Task RemovePtzPresetAsync(
        string profileToken,
        string presetToken,
        CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        _ = await PostAsync(
            new XElement(Tptz + "RemovePreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "PresetToken", presetToken)),
            PtzXAddr,
            TptzAction("RemovePreset"), cancellationToken);
    }

    /// <summary>將目前位置儲存為預設點（SetPreset），回傳預設點 Token。</summary>
    public async Task<string> SetPtzPresetAsync(string profileToken, string presetName, CancellationToken cancellationToken = default)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        var body = await PostAsync(
            new XElement(Tptz + "SetPreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                new XElement(Tptz + "PresetName", presetName)),
            PtzXAddr,
            TptzAction("SetPreset"), cancellationToken);

        return (string?)DescendantAnyNs(body, "PresetToken") ?? string.Empty;
    }

    private static PtzStatus ParsePtzStatus(XElement? position)
    {
        var panTilt = position?.Descendants().FirstOrDefault(e => e.Name.LocalName == "PanTilt");
        var zoom = position?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Zoom");
        return new PtzStatus
        {
            Pan = ParseAxis((string?)panTilt?.Attribute("x")),
            Tilt = ParseAxis((string?)panTilt?.Attribute("y")),
            Zoom = ParseAxis((string?)zoom?.Attribute("x")),
        };
    }

    private static double ParseAxis(string? raw)
    {
        if (double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) &&
            !double.IsNaN(value))
        {
            return value;
        }

        return 0;
    }

    private async Task<string?> TryGetStreamUriAsync(string profileToken, CancellationToken cancellationToken)
    {
        var uri = await GetStreamUriAsync(profileToken, cancellationToken);
        return uri;
    }

    /// <summary>將設備回傳之 RTSP 主機名替換為連線用 IP（避免 DNS 不可解析）。</summary>
    private static string NormalizeUriHost(string value, string deviceXAddr)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return value;
        }

        var host = new Uri(deviceXAddr).Host;
        return parsed.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            ? value
            : new UriBuilder(parsed) { Host = host }.Uri.AbsoluteUri;
    }

    private async Task EnsureMediaXAddrAsync(CancellationToken cancellationToken)
    {
        if (MediaXAddr is not null)
        {
            return;
        }

        try
        {
            var body = await PostAsync(
                new XElement(Td + "GetCapabilities", new XElement(Td + "Category", "Media")), null,
                TdAction("GetCapabilities"), cancellationToken);

            MediaXAddr = (string?)DescendantAnyNs(body, "XAddr");
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.Net.Http.HttpRequestException)
        {
        }
        catch (System.Xml.XmlException)
        {
        }

        if (string.IsNullOrEmpty(MediaXAddr))
        {
            // 不支援 GetCapabilities 的設備：依個案慣用路徑
            var baseUri = new Uri(DeviceXAddr).GetLeftPart(UriPartial.Authority);
            MediaXAddr = new Uri(new Uri(baseUri + "/"), "onvif/Media").AbsoluteUri;
        }
    }

    private async Task<XElement> PostAsync(
        XElement action,
        string? actionXAddr,
        string actionUri,
        CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope(action, actionUri).ToString(SaveOptions.DisableFormatting);
        var url = (actionXAddr ?? DeviceXAddr)[0] == '/'
            ? new Uri(new Uri(DeviceXAddr), actionXAddr ?? DeviceXAddr).AbsoluteUri
            : actionXAddr ?? DeviceXAddr;

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml"),
        };

        var response = await _http.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length == 0)
        {
            throw new InvalidOperationException($"ONVIF 請求失敗：HTTP {(int)response.StatusCode}");
        }

        var doc = XDocument.Parse(text, LoadOptions.None);
        var body = doc.Root?.Element(Env + "Body");
        if (body is null)
        {
            throw new InvalidOperationException("ONVIF 回應不含 SOAP Body");
        }

        var fault = DescendantAnyNs(body, "Fault");
        if (fault is not null)
        {
            var reason = string.Join(
                "；",
                fault.Descendants().Where(e => e.Name.LocalName == "Text").Select(t => t.Value));
            throw new InvalidOperationException(string.IsNullOrEmpty(reason) ? "ONVIF 設備回報 Fault" : $"ONVIF Fault：{reason}");
        }

        return body;
    }

    private XDocument BuildEnvelope(XElement action, string actionUri)
    {
        var header = new XElement(Env + "Header",
            new XElement(Wsa + "Action", actionUri));

        header.Add(new XElement(Wsa + "To", DeviceXAddr));

        if (_username is not null)
        {
            var created = DateTime.UtcNow.AddMinutes(-2);
            var nonce = RandomNumberGenerator.GetBytes(8);
            var createdText = created.ToString("yyyy-MM-ddT'HH:mm:ss'Z", System.Globalization.CultureInfo.InvariantCulture);
            var nonceText = Convert.ToBase64String(nonce);
            var digest = Convert.ToBase64String(SHA1.HashData(
                Encoding.UTF8.GetBytes(nonceText + createdText + _password)));

            header.Add(
                new XElement(Wsse + "Security",
                    new XElement(Wsse + "UsernameToken",
                        new XElement(Wsse + "Username", _username),
                        new XElement(Wsse + "Password",
                            new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"),
                            digest),
                        new XElement(Wsse + "Nonce",
                            new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"),
                            nonceText),
                        new XElement(Wsu + "Created", createdText))));
        }

        return new XDocument(
            new XElement(Env + "Envelope",
                new XAttribute(XNamespace.Xmlns + "env", Env.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "td", Td.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "trt", Trt.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "tns", Ts.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wsa", Wsa.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wse", Wsse.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wsu", Wsu.NamespaceName),
                header,
                new XElement(Env + "Body", action)));
    }

    private static string TdAction(string action) => $"http://www.onvif.org/ver10/device/wsdl/{action}";
    private static string TrtAction(string action) => $"http://www.onvif.org/ver10/media/wsdl/{action}";
    private static string TptzAction(string action) => $"http://www.onvif.org/ver10/ptz/wsdl/{action}";

    public void Dispose() => _http.Dispose();

    private static XElement? DescendantAnyNs(XElement element, string localName) =>
        element.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> DescendantsAnyNs(XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName == localName);
}

internal static class XElementAnyNsExtensions
{
    /// <summary>依本名（LocalName）找後代元素，與命名空間無關。</summary>
    public static XElement? ElementAnyNs(this XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
}