using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// ONVIF 設備服務（HTTP SOAP）：GetDeviceInformation／GetSystemDateAndTime／
/// GetProfiles／GetStreamUri 與 PTZ 動作。
/// <para>多廠牌相容對應：WS-Security UsernameToken（PasswordDigest／PasswordText）與
/// HTTP Digest／Basic 認證自動切換、SOAP 1.1／1.2 與 Fault 位置容忍、
/// GetCapabilities 路徑精確解析與 Media／PTZ 路徑候選探測、XAddr 位址校正。</para>
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
    private readonly OnvifClientOptions _options;
    private readonly SemaphoreSlim _capabilityLock = new(1, 1);

    private OnvifWsSecurityMode _wsSecurityMode;
    private OnvifHttpChallenge? _httpChallenge;
    private string? _challengeIdentity;

    /// <summary>profile token → PTZ node token；由 <see cref="GetProfilesAsync"/> 從 PTZConfiguration 取得。</summary>
    private readonly Dictionary<string, string> _ptzNodesByProfile = new(StringComparer.Ordinal);

    /// <summary>設備曾以 ter:Namespace 拒絕 NodeToken，之後一律不帶（多節點設備才需要該元素）。</summary>
    private bool _ptzNodeTokenRejected;

    /// <summary>以設備 XAddr（慣例 http://ip/onvif/device_service）建立服務端點。</summary>
    public OnvifDeviceService(string deviceXAddr, string? username = null, string? password = null)
        : this(deviceXAddr, username, password, new HttpClientHandler())
    {
    }

    /// <summary>以設備 XAddr 與連線選項建立服務端點（認證模式、逾時、重試等）。</summary>
    public OnvifDeviceService(
        string deviceXAddr,
        string? username,
        string? password,
        OnvifClientOptions? options)
        : this(deviceXAddr, username, password, new HttpClientHandler(), options)
    {
    }

    /// <summary>供注入自訂 HttpMessageHandler（單元測試／進階用途）。</summary>
    public OnvifDeviceService(
        string deviceXAddr,
        string? username,
        string? password,
        HttpMessageHandler handler,
        OnvifClientOptions? options = null)
    {
        DeviceXAddr = deviceXAddr;
        _username = username;
        _password = password;
        _options = options ?? OnvifClientOptions.Default;
        _wsSecurityMode = _options.AuthMode switch
        {
            OnvifAuthMode.None => OnvifWsSecurityMode.None,
            OnvifAuthMode.WsSecurityText => OnvifWsSecurityMode.PasswordText,
            _ => string.IsNullOrEmpty(username)
                ? OnvifWsSecurityMode.None
                : OnvifWsSecurityMode.PasswordDigest,
        };

        _http = new HttpClient(handler) { Timeout = _options.Timeout };
        _http.DefaultRequestHeaders.Add("User-Agent", "HeliVMS/0.1 (ONVIF/ver10)");
    }

    public string DeviceXAddr { get; }

    /// <summary>Media 服務位址（首次呼叫 GetProfiles 前以 GetCapabilities 解析，必要時探測候選路徑）。</summary>
    public string? MediaXAddr { get; private set; }

    /// <summary>PTZ 服務位址（首次呼叫 PTZ 動作前以 GetCapabilities 解析）。</summary>
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
        catch (OnvifException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取得媒體 Profile（含 VideoEncoderConfiguration 之解析度／編碼資訊與 RTSP 位址）。
    /// 串流位址解析受 <see cref="OnvifClientOptions.StreamUriBudget"/> 與
    /// <see cref="OnvifClientOptions.MaxStreamUris"/> 保護；未解析者可稍後以
    /// <see cref="GetStreamUriAsync"/> 單獨取得。
    /// </summary>
    public async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMediaXAddrAsync(cancellationToken);
        var body = await PostAsync(
            new XElement(Trt + "GetProfiles"), MediaXAddr,
            TrtAction("GetProfiles"), cancellationToken);

        var parsed = ParseProfiles(body);
        if (parsed.Count == 0)
        {
            return [];
        }

        foreach (var profile in parsed)
        {
            if (profile.PtzNodeToken.Length > 0)
            {
                _ptzNodesByProfile[profile.Token] = profile.PtzNodeToken;
            }
        }

        var streamUris = new Dictionary<string, string>(parsed.Count, StringComparer.Ordinal);
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(_options.StreamUriBudget);

            var resolved = 0;
            foreach (var profile in parsed)
            {
                if (resolved >= _options.MaxStreamUris || budget.IsCancellationRequested)
                {
                    break;
                }

                var uri = await TryGetStreamUriAsync(profile.Token, budget.Token);
                if (!string.IsNullOrEmpty(uri))
                {
                    streamUris[profile.Token] = uri;
                    resolved++;
                }
            }
        }

        var classified = OnvifProfileSelection.Classify(
            parsed.Select(profile => new OnvifProfile
            {
                Token = profile.Token,
                Name = profile.Name,
                Width = profile.Width,
                Height = profile.Height,
                Encoding = profile.Encoding,
                VideoSourceToken = profile.VideoSourceToken,
                PtzNodeToken = profile.PtzNodeToken,
                Role = profile.Role,
                StreamUri = streamUris.GetValueOrDefault(profile.Token) ?? string.Empty,
            }));

        return OnvifProfileSelection.Order(classified);
    }

    /// <summary>取得指定 Profile 之 RTSP 串流位址（首次呼叫前先解析 Media 服務位址）。</summary>
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
        catch (OnvifException)
        {
        }

        return null;
    }

    /// <summary>確保已解析 PTZ 服務位址（GetCapabilities Category=All，再回退 Category=PTZ）。</summary>
    public async Task EnsurePtzCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (PtzXAddr is not null)
        {
            return;
        }

        await _capabilityLock.WaitAsync(cancellationToken);
        try
        {
            if (PtzXAddr is not null)
            {
                return;
            }

            var xAddr = await TryResolveCapabilityAsync("PTZ", ["All", "PTZ"], cancellationToken);
            if (string.IsNullOrEmpty(xAddr))
            {
                // 不支援 GetCapabilities 之設備：探測常見 PTZ 路徑
                xAddr = await ProbeServicePathAsync(
                    OnvifEndpointResolver.PtzServicePaths,
                    new XElement(Tptz + "GetPresets", new XElement(Tptz + "ProfileToken", string.Empty)),
                    TptzAction("GetPresets"),
                    cancellationToken);
            }

            PtzXAddr = xAddr ?? string.Empty;

            // 僅有 XAddr 並不足以證明 PTZ 可用：部分實機（實測 VIVOTEK SD9368-EHL FW 1.2201.35.01）
            // 會宣告 PTZ 服務，卻對所有 PTZ 動作回 ter:Namespace。此處實際驗證一次，
            // 不可用時清空位址讓 HasPtz 為 false，避免 UI 提供無法使用的 PTZ 功能。
            if (PtzXAddr.Length > 0 && !await VerifyPtzEndpointAsync(PtzXAddr, cancellationToken))
            {
                PtzXAddr = string.Empty;
            }
        }
        finally
        {
            _capabilityLock.Release();
        }
    }

    /// <summary>
    /// 以 GetServiceCapabilities 驗證 PTZ 端點真的接受 PTZ 動作（無參數，最小化相依）。
    /// 暫時性錯誤（逾時／連線）不視為不支援，保留端點讓實際操作回報真實錯誤，避免誤記為無 PTZ。
    /// </summary>
    private async Task<bool> VerifyPtzEndpointAsync(string ptzXAddr, CancellationToken cancellationToken)
    {
        try
        {
            _ = await PostAsync(
                new XElement(Tptz + "GetServiceCapabilities"),
                ptzXAddr,
                TptzAction("GetServiceCapabilities"),
                cancellationToken,
                retryable: false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OnvifException ex) when (ex.IsTransient)
        {
            return true;
        }
        catch (OnvifException)
        {
            return false;
        }
    }

    /// <summary>取得 PTZ 目前位置（GetStatus）。</summary>
    public async Task<PtzStatus> GetPtzStatusAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        var body = await PostPtzAsync("GetStatus", profileToken, node =>
            new XElement(Tptz + "GetStatus",
                new XElement(Tptz + "ProfileToken", profileToken),
                node),
            cancellationToken);

        var position = DescendantAnyNs(body, "Position");
        return ParsePtzStatus(position ?? DescendantAnyNs(body, "PTZStatus"));
    }

    /// <summary>
    /// 送出 PTZ 動作。NodeToken 在 ptz.wsdl 中為選用元素，但部分實機（實測 VIVOTEK SD9368-EHL）
    /// 會回 ter:Namespace 拒絕；此時自動改為不帶 NodeToken 重試一次，並記住結果避免後續重複失敗。
    /// </summary>
    private async Task<XElement> PostPtzAsync(
        string action,
        string profileToken,
        Func<XElement?, XElement> buildAction,
        CancellationToken cancellationToken,
        bool retryable = true)
    {
        var xAddr = await RequirePtzAsync(cancellationToken);
        var node = _ptzNodeTokenRejected ? null : PtzNodeElement(profileToken);

        try
        {
            return await PostAsync(buildAction(node), xAddr, TptzAction(action), cancellationToken, retryable);
        }
        catch (OnvifException ex) when (node is not null && IsNodeTokenRejected(ex))
        {
            _ptzNodeTokenRejected = true;
            return await PostAsync(buildAction(null), xAddr, TptzAction(action), cancellationToken, retryable);
        }
    }

    /// <summary>判斷設備是否因不支援選用元素（如 NodeToken）而回錯。</summary>
    private static bool IsNodeTokenRejected(OnvifException ex) =>
        ex.FaultCode is { } code &&
        (code.EndsWith("Namespace", StringComparison.OrdinalIgnoreCase) ||
         code.EndsWith("ActionNotSupported", StringComparison.OrdinalIgnoreCase) ||
         code.EndsWith("InvalidArg", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 產生 PTZ 請求的 NodeToken 元素（置於 ProfileToken 之後，符合 ptz.wsdl 元素順序）。
    /// 需先呼叫 <see cref="GetProfilesAsync"/> 取得 PTZConfiguration/NodeToken；未取得時回傳 null，
    /// 由設備自行套用預設節點（單節點攝影機可正常運作）。
    /// </summary>
    private XElement? PtzNodeElement(string profileToken)
    {
        if (string.IsNullOrWhiteSpace(profileToken))
        {
            return null;
        }

        return _ptzNodesByProfile.TryGetValue(profileToken, out var nodeToken) && nodeToken.Length > 0
            ? new XElement(Tptz + "NodeToken", nodeToken)
            : null;
    }

    /// <summary>確認 PTZ 服務可用；不支援時回傳可顯示之型別化錯誤。</summary>
    private async Task<string> RequirePtzAsync(CancellationToken cancellationToken)
    {
        await EnsurePtzCapabilityAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(PtzXAddr))
        {
            throw OnvifException.NotSupported("設備不支援 ONVIF PTZ。");
        }

        return PtzXAddr;
    }

    /// <summary>連續移動（Velocity 各軸 -1..1；約 4 秒後自動停止，避免設備持續運轉）。</summary>
    public async Task ContinuousMoveAsync(
        string profileToken,
        double pan,
        double tilt,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        _ = await PostPtzAsync("ContinuousMove", profileToken, node =>
            new XElement(Tptz + "ContinuousMove",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "Velocity",
                    new XElement(Ts + "PanTilt",
                        new XAttribute("x", pan.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("y", tilt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    new XElement(Ts + "Zoom",
                        new XAttribute("x", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new XElement(Tptz + "Timeout", "PT4S")),
            cancellationToken, retryable: false);
    }

    /// <summary>停止 PTZ 移動（PanTilt＋Zoom 都停止）。</summary>
    public async Task StopPtzAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        _ = await PostPtzAsync("Stop", profileToken, node =>
            new XElement(Tptz + "Stop",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "PanTilt", "true"),
                new XElement(Tptz + "Zoom", "true")),
            cancellationToken, retryable: false);
    }

    /// <summary>取得全部 PTZ 預設點。</summary>
    public async Task<IReadOnlyList<PtzPreset>> GetPtzPresetsAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        var body = await PostPtzAsync("GetPresets", profileToken, node =>
            new XElement(Tptz + "GetPresets",
                new XElement(Tptz + "ProfileToken", profileToken),
                node),
            cancellationToken);

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
        _ = await PostPtzAsync("GotoPreset", profileToken, node =>
            new XElement(Tptz + "GotoPreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "PresetToken", presetToken)),
            cancellationToken, retryable: false);
    }

    /// <summary>絕對移動（AbsoluteMove：設定絕對 Pan/Tilt/Zoom 位置）。</summary>
    public async Task AbsoluteMoveAsync(
        string profileToken,
        double pan,
        double tilt,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        _ = await PostPtzAsync("AbsoluteMove", profileToken, node =>
            new XElement(Tptz + "AbsoluteMove",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "Position",
                    new XElement(Ts + "PanTilt",
                        new XAttribute("x", pan.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("y", tilt.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    new XElement(Ts + "Zoom",
                        new XAttribute("x", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))))),
            cancellationToken, retryable: false);
    }

    /// <summary>返回 Home 位置（Home）。</summary>
    public async Task HomeAsync(string profileToken, CancellationToken cancellationToken = default)
    {
        _ = await PostPtzAsync("Home", profileToken, node =>
            new XElement(Tptz + "Home",
                new XElement(Tptz + "ProfileToken", profileToken),
                node),
            cancellationToken, retryable: false);
    }

    /// <summary>移除 PTZ 預設點（RemovePreset）。</summary>
    public async Task RemovePtzPresetAsync(
        string profileToken,
        string presetToken,
        CancellationToken cancellationToken = default)
    {
        _ = await PostPtzAsync("RemovePreset", profileToken, node =>
            new XElement(Tptz + "RemovePreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "PresetToken", presetToken)),
            cancellationToken, retryable: false);
    }

    /// <summary>將目前位置儲存為預設點（SetPreset），回傳預設點 Token。</summary>
    public async Task<string> SetPtzPresetAsync(string profileToken, string presetName, CancellationToken cancellationToken = default)
    {
        var body = await PostPtzAsync("SetPreset", profileToken, node =>
            new XElement(Tptz + "SetPreset",
                new XElement(Tptz + "ProfileToken", profileToken),
                node,
                new XElement(Tptz + "PresetName", presetName)),
            cancellationToken, retryable: false);

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

    private static List<OnvifProfile> ParseProfiles(XElement body)
    {
        var profiles = new List<OnvifProfile>();
        foreach (var element in DescendantsAnyNs(body, "Profiles"))
        {
            var token = (string?)element.Attribute("token") ?? string.Empty;
            if (token.Length == 0)
            {
                continue;
            }

            var encoder = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            var sourceConfiguration = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "VideoSourceConfiguration");
            var ptzConfiguration = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "PTZConfiguration");

            profiles.Add(new OnvifProfile
            {
                Token = token,
                Name = (string?)element.ElementAnyNs("Name")
                    ?? (string?)encoder?.ElementAnyNs("Name")
                    ?? (string?)element.ElementAnyNs("VideoSourceConfiguration")?.Attribute("token")
                    ?? token,
                Width = ReadResolution(encoder, "Width"),
                Height = ReadResolution(encoder, "Height"),
                Encoding = ((string?)encoder?.ElementAnyNs("Encoding") ?? string.Empty).Trim(),
                VideoSourceToken = (string?)sourceConfiguration?.Attribute("token") ?? string.Empty,
                PtzNodeToken = ((string?)ptzConfiguration?.ElementAnyNs("NodeToken") ?? string.Empty).Trim(),
            });
        }

        return profiles;
    }

    private static int ParseInt32(string? raw) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>
    /// 讀取編碼器解析度。ONVIF 規範將 Width/Height 置於 VideoEncoderConfiguration/Resolution 之下，
    /// 但部分廠商直接掛在編碼器層級，故兩處都取；不回落 VideoSourceConfiguration/Bounds（那是感測器尺寸，
    /// 對縮圖子碼流會顯示錯誤解析度）。
    /// </summary>
    private static int ReadResolution(XElement? encoder, string localName)
    {
        if (encoder is null)
        {
            return 0;
        }

        var resolution = encoder.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Resolution");

        return ParseInt32((string?)resolution?.ElementAnyNs(localName) ?? (string?)encoder.ElementAnyNs(localName));
    }

    private async Task<string?> TryGetStreamUriAsync(string profileToken, CancellationToken cancellationToken)
    {
        try
        {
            return await GetStreamUriAsync(profileToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (OnvifException)
        {
            // 單一 profile 失敗不影響其餘（部分 NVR 之無效／停用 profile 會回 Fault）。
            return null;
        }
    }

    /// <summary>將設備回傳之 RTSP 主機名替換為連線用 IP（避免 DNS 不可解析）。</summary>
    private static string NormalizeUriHost(string value, string deviceXAddr)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return value;
        }

        if (!Uri.TryCreate(deviceXAddr, UriKind.Absolute, out var device) ||
            parsed.Host.Equals(device.Host, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return new UriBuilder(parsed) { Host = device.Host }.Uri.AbsoluteUri;
    }

    private async Task EnsureMediaXAddrAsync(CancellationToken cancellationToken)
    {
        if (MediaXAddr is not null)
        {
            return;
        }

        await _capabilityLock.WaitAsync(cancellationToken);
        try
        {
            if (MediaXAddr is not null)
            {
                return;
            }

            var xAddr = await TryResolveCapabilityAsync("Media", ["Media", "All"], cancellationToken);
            if (string.IsNullOrEmpty(xAddr))
            {
                // 不支援（或回應不含）GetCapabilities 之設備：實際探測常見 Media 路徑
                xAddr = await ProbeServicePathAsync(
                    OnvifEndpointResolver.MediaServicePaths,
                    new XElement(Trt + "GetProfiles"),
                    TrtAction("GetProfiles"),
                    cancellationToken);
            }

            if (string.IsNullOrEmpty(xAddr))
            {
                throw OnvifException.NotSupported("設備未提供 ONVIF Media 服務，無法取得串流 Profile。");
            }

            MediaXAddr = xAddr;
        }
        finally
        {
            _capabilityLock.Release();
        }
    }

    /// <summary>依序以各 Category 呼叫 GetCapabilities，取出指定能力之 XAddr。</summary>
    private async Task<string?> TryResolveCapabilityAsync(
        string category,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken)
    {
        foreach (var requested in categories)
        {
            XElement body;
            try
            {
                body = await PostAsync(
                    new XElement(Td + "GetCapabilities", new XElement(Td + "Category", requested)),
                    null,
                    TdAction("GetCapabilities"),
                    cancellationToken);
            }
            catch (OnvifException ex) when (ex.Code is OnvifErrorCode.NotSupported or OnvifErrorCode.DeviceFault or OnvifErrorCode.BadResponse)
            {
                // 部分舊固件僅接受 Category=All，或完全不支援此類別
                continue;
            }

            var xAddr = ReadCapabilityXAddr(body, category);
            if (!string.IsNullOrWhiteSpace(xAddr))
            {
                return OnvifEndpointResolver.NormalizeServiceXAddr(xAddr, DeviceXAddr);
            }
        }

        return null;
    }

    /// <summary>以真實請求探測服務端點候選路徑（回傳第一個可用之 XAddr）。</summary>
    private async Task<string?> ProbeServicePathAsync(
        IReadOnlyList<string> paths,
        XElement probeAction,
        string actionUri,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in OnvifEndpointResolver.BuildServiceCandidates(DeviceXAddr, paths))
        {
            try
            {
                _ = await PostAsync(probeAction, candidate, actionUri, cancellationToken, retryable: false);
                return candidate;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OnvifException ex) when (ex.Code is OnvifErrorCode.NotSupported
                or OnvifErrorCode.BadResponse
                or OnvifErrorCode.DeviceFault
                or OnvifErrorCode.Connection
                or OnvifErrorCode.Timeout)
            {
            }
        }

        return null;
    }

    /// <summary>由 GetCapabilities 回應中取出指定類別之 XAddr（依元素結構，不受命名空間前綴影響）。</summary>
    internal static string? ReadCapabilityXAddr(XElement body, string category)
    {
        var capabilities = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "Capabilities");
        var node = capabilities?.Elements().FirstOrDefault(e => e.Name.LocalName == category)
            ?? body.Descendants().FirstOrDefault(e => e.Name.LocalName == category);
        if (node is null)
        {
            return null;
        }

        var xAddr = (string?)node.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")
            ?? (string?)node.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr");
        return string.IsNullOrWhiteSpace(xAddr) ? null : xAddr.Trim();
    }

    private string ResolveUrl(string? actionXAddr)
    {
        var target = actionXAddr ?? DeviceXAddr;
        return target.Length > 0 && target[0] == '/'
            ? new Uri(new Uri(DeviceXAddr), target).AbsoluteUri
            : target;
    }

    private async Task<XElement> PostAsync(
        XElement action,
        string? actionXAddr,
        string actionUri,
        CancellationToken cancellationToken,
        bool retryable = true)
    {
        var url = ResolveUrl(actionXAddr);
        var budget = new RetryBudget(retryable ? Math.Max(_options.RetryCount, 0) : 0);
        var authSwitched = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var envelope = BuildEnvelope(action, actionUri).ToString(SaveOptions.DisableFormatting);
            using var response = await SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml"),
                    };
                    ApplyAuthorization(request, url);
                    return request;
                },
                url,
                budget,
                cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
            {
                // 廠牌差異：部分設備以 HTTP Digest／Basic 認證（非 WS-Security）
                if (!authSwitched && TryAdoptHttpChallenge(response))
                {
                    authSwitched = true;
                    continue;
                }

                throw OnvifException.Unauthorized(
                    $"設備拒絕認證（HTTP {(int)response.StatusCode}）：請確認帳號與密碼。");
            }

            if (IsTransientStatus(response.StatusCode) && budget.Remaining > 0)
            {
                budget.Remaining--;
                await DelayBeforeRetryAsync(cancellationToken);
                continue;
            }

            var parsed = ParseSoapResponse(text, response.StatusCode);

            // 廠牌差異：部分設備僅接受 WS-Security PasswordText（Digest 會回認證失敗 Fault）
            if (!authSwitched &&
                _wsSecurityMode == OnvifWsSecurityMode.PasswordDigest &&
                IsWsAuthenticationFault(parsed.Fault) &&
                TryDowngradeToPasswordText())
            {
                authSwitched = true;
                continue;
            }

            if (parsed.Fault is not null)
            {
                throw MapFault(parsed.Fault);
            }

            return parsed.Body;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        string url,
        RetryBudget budget,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // 每次嘗試皆需新的 HttpRequestMessage（HttpClient 不允許重送同一實例）
            using var request = createRequest();
            try
            {
                return await _http.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                if (budget.Remaining <= 0)
                {
                    throw new OnvifException(OnvifErrorCode.Timeout, $"ONVIF 請求逾時：{url}", ex);
                }

                budget.Remaining--;
            }
            catch (HttpRequestException ex)
            {
                if (budget.Remaining <= 0)
                {
                    throw new OnvifException(OnvifErrorCode.Connection, $"無法連線至設備：{url}", ex);
                }

                budget.Remaining--;
            }

            await DelayBeforeRetryAsync(cancellationToken);
        }
    }

    /// <summary>暫時性錯誤之剩餘重試次數（async 方法不可使用 ref 參數，故以類別承載）。</summary>
    private sealed class RetryBudget
    {
        public RetryBudget(int remaining) => Remaining = remaining;

        public int Remaining { get; set; }
    }

    private async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        if (_options.RetryDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.RetryDelay, cancellationToken);
        }
    }

    private void ApplyAuthorization(HttpRequestMessage message, string url)
    {
        if (_username is null || _httpChallenge is null || !AllowsHttpAuth())
        {
            return;
        }

        var value = _httpChallenge.BuildAuthorization(
            HttpMethod.Post.Method,
            new Uri(url).PathAndQuery,
            _username,
            _password ?? string.Empty);
        var separator = value.IndexOf(' ', StringComparison.Ordinal);
        message.Headers.Authorization = separator < 0
            ? new AuthenticationHeaderValue(value)
            : new AuthenticationHeaderValue(value[..separator], value[(separator + 1)..]);
    }

    private bool AllowsHttpAuth() =>
        _options.AuthMode is OnvifAuthMode.Auto or OnvifAuthMode.HttpDigest or OnvifAuthMode.HttpBasic;

    private bool TryAdoptHttpChallenge(HttpResponseMessage response)
    {
        if (_username is null || !AllowsHttpAuth())
        {
            return false;
        }

        if (!OnvifHttpChallenge.TryCreate(response, out var challenge) || challenge is null)
        {
            return false;
        }

        if (string.Equals(_challengeIdentity, challenge.Identity, StringComparison.Ordinal))
        {
            return false;
        }

        _challengeIdentity = challenge.Identity;
        _httpChallenge = challenge;
        return true;
    }

    private bool TryDowngradeToPasswordText()
    {
        if (_username is null || !AllowsHttpAuth())
        {
            return false;
        }

        _wsSecurityMode = OnvifWsSecurityMode.PasswordText;
        return true;
    }

    /// <summary>解析 SOAP 回應（容忍 SOAP 1.1／1.2 與 Fault 位於 Envelope 層之實作）。</summary>
    internal static (XElement Body, XElement? Fault) ParseSoapResponse(string text, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new OnvifException(
                IsSuccess(statusCode) ? OnvifErrorCode.BadResponse : StatusToErrorCode(statusCode),
                IsSuccess(statusCode)
                    ? "ONVIF 回應為空（設備可能中斷連線或非 ONVIF 產品）。"
                    : StatusToErrorMessage(statusCode));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new OnvifException(
                IsSuccess(statusCode) ? OnvifErrorCode.BadResponse : StatusToErrorCode(statusCode),
                IsSuccess(statusCode)
                    ? "ONVIF 回應非 SOAP／XML 格式（設備可能為非 ONVIF 產品或路徑錯誤）。"
                    : StatusToErrorMessage(statusCode),
                ex);
        }

        var envelope = doc.Root;
        if (envelope is null || envelope.Name.LocalName != "Envelope")
        {
            throw new OnvifException(
                IsSuccess(statusCode) ? OnvifErrorCode.BadResponse : StatusToErrorCode(statusCode),
                IsSuccess(statusCode)
                    ? "ONVIF 回應不含 SOAP Envelope。"
                    : StatusToErrorMessage(statusCode));
        }

        // SOAP 1.1 之 Fault 直接位於 Envelope 之下
        var fault = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        var body = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (body is null)
        {
            if (fault is not null)
            {
                return (new XElement("Body"), fault);
            }

            throw new OnvifException(
                IsSuccess(statusCode) ? OnvifErrorCode.BadResponse : StatusToErrorCode(statusCode),
                IsSuccess(statusCode)
                    ? "ONVIF 回應不含 SOAP Body。"
                    : StatusToErrorMessage(statusCode));
        }

        fault ??= body.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        return (body, fault);
    }

    private static bool IsWsAuthenticationFault(XElement? fault) =>
        fault is not null && TryGetFaultCode(fault, out var code) &&
        (code.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
         code.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase) ||
         code.Contains("InvalidToken", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccess(HttpStatusCode statusCode) => (int)statusCode is >= 200 and < 300;

    /// <summary>非成功狀態碼之錯誤分類：路徑不存在視為不支援、5xx 視為連線／暫時性問題。</summary>
    private static OnvifErrorCode StatusToErrorCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            or HttpStatusCode.BadGateway => OnvifErrorCode.NotSupported,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.InternalServerError => OnvifErrorCode.Connection,
        _ => OnvifErrorCode.BadResponse,
    };

    private static string StatusToErrorMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => "設備端不存在該 ONVIF 服務路徑（HTTP 404）。",
        HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed
            => "設備不支援此 ONVIF 操作（服務路徑或方法不符）。",
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout
            => $"ONVIF 請求逾時（HTTP {(int)statusCode}）。",
        HttpStatusCode.TooManyRequests => "設備回應過多請求（HTTP 429），請稍後再試。",
        _ => $"ONVIF 請求失敗：HTTP {(int)statusCode} {statusCode}。",
    };

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.InternalServerError;

    private static OnvifException MapFault(XElement fault)
    {
        TryGetFaultCode(fault, out var code);
        var text = string.Join(
            "；",
            fault.Descendants()
                .Where(e => e.Name.LocalName == "Text")
                .Select(e => e.Value.Trim())
                .Where(e => e.Length > 0));
        var detail = text.Length > 0 ? text : code.Length > 0 ? code : "設備回報 SOAP Fault";

        if (code.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("InvalidToken", StringComparison.OrdinalIgnoreCase))
        {
            return OnvifException.Unauthorized($"設備拒絕認證：{detail}", code);
        }

        if (code.Contains("ActionNotSupported", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("CannotProcess", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("NoSuchObject", StringComparison.OrdinalIgnoreCase))
        {
            return OnvifException.NotSupported($"設備不支援此操作：{detail}", code);
        }

        return new OnvifException(OnvifErrorCode.DeviceFault, $"ONVIF 錯誤：{detail}") { FaultCode = code };
    }

    private static bool TryGetFaultCode(XElement fault, out string code)
    {
        code = string.Empty;

        // SOAP 1.2：Subcode/Value 為實際錯誤碼（ter:NotAuthorized 等）
        var subcode = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Subcode");
        if (subcode is not null)
        {
            code = (subcode.Elements().FirstOrDefault(e => e.Name.LocalName == "Value") ?? subcode).Value.Trim();
            if (code.Length > 0)
            {
                return true;
            }
        }

        // SOAP 1.1：faultcode 僅為 Sender/Receiver，實際錯誤碼置於 detail
        var detail = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "detail");
        var detailValue = detail?
            .Descendants()
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
        if (!string.IsNullOrEmpty(detailValue))
        {
            code = detailValue;
            return true;
        }

        code = (fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Value")
            ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultcode"))?.Value.Trim() ?? string.Empty;
        return code.Length > 0;
    }

    private XDocument BuildEnvelope(XElement action, string actionUri)
    {
        var header = new XElement(Env + "Header",
            new XElement(Wsa + "Action", actionUri));

        header.Add(new XElement(Wsa + "To", DeviceXAddr));

        var security = OnvifWsSecurity.Build(_username, _password, _wsSecurityMode);
        if (security is not null)
        {
            header.Add(security);
        }

        return new XDocument(
            new XElement(Env + "Envelope",
                new XAttribute(XNamespace.Xmlns + "env", Env.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "td", Td.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "trt", Trt.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "tns", Ts.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "tptz", Tptz.NamespaceName),
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
