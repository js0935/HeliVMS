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

    /// <summary>編碼寬度（由 VideoEncoderConfiguration 取得；0 表示設備未提供）。</summary>
    public int Width { get; init; }

    /// <summary>編碼高度（同上）。</summary>
    public int Height { get; init; }

    /// <summary>
    /// 解析度是否來自實際串流探測（ffprobe）而非 ONVIF 宣告值。
    /// 實測 VIVOTEK 多款韌體會宣告與實際不符的解析度（21 個 profile 中 11 個），故以探測值為準。
    /// </summary>
    public bool ResolutionIsProbed { get; init; }

    /// <summary>編碼格式（H264／H265／MJPEG…；空字串表示設備未提供）。</summary>
    public string Encoding { get; init; } = string.Empty;

    /// <summary>所屬影像來源 token（多sensor／NVR 機箱以 VideoSourceConfiguration 區分）。</summary>
    public string VideoSourceToken { get; init; } = string.Empty;

    /// <summary>
    /// PTZ 節點 token（取自 PTZConfiguration/NodeToken；空字串表示設備未提供或此 profile 無 PTZ）。
    /// 多節點 NVR／多攝影機機箱的 PTZ 動作必須帶此值，否則會作用到錯誤的鏡頭。
    /// </summary>
    public string PtzNodeToken { get; init; } = string.Empty;

    /// <summary>碼流角色（由 <see cref="OnvifProfileSelection.Classify"/> 判定；預設 Unknown）。</summary>
    public OnvifStreamRole Role { get; init; } = OnvifStreamRole.Unknown;

    /// <summary>是否為影像 profile（具 VideoEncoderConfiguration）。</summary>
    public bool IsVideo => Width > 0 || Height > 0 || Encoding.Length > 0;

    /// <summary>解析度標籤（供 UI 顯示；設備未提供時為空字串）。</summary>
    public string ResolutionLabel => Width > 0 && Height > 0
        ? $"{Width}×{Height}"
        : Width > 0 || Height > 0
            ? $"{Math.Max(Width, Height)}"
            : string.Empty;

    /// <summary>是否為子／次要碼流（<see cref="Role"/> 優先，否則以名稱／token 關鍵字判斷）。</summary>
    public bool IsSubStream => Role == OnvifStreamRole.Sub ||
        (Role == OnvifStreamRole.Unknown && OnvifProfileSelection.HasSubStreamKeyword(this));

    /// <summary>是否為主要碼流（多碼流設備之預設選擇）。</summary>
    public bool IsMainStream => !IsSubStream;

    /// <summary>傳回指定碼流角色之複本（Profile 為不可變資料）。</summary>
    public OnvifProfile WithRole(OnvifStreamRole role) => new()
    {
        Token = Token,
        Name = Name,
        StreamUri = StreamUri,
        Width = Width,
        Height = Height,
        Encoding = Encoding,
        VideoSourceToken = VideoSourceToken,
        PtzNodeToken = PtzNodeToken,
        ResolutionIsProbed = ResolutionIsProbed,
        Role = role,
    };

    /// <summary>傳回指定串流位址之複本（Profile 為不可變資料）。</summary>
    public OnvifProfile WithStreamUri(string streamUri) => new()
    {
        Token = Token,
        Name = Name,
        StreamUri = streamUri,
        Width = Width,
        Height = Height,
        Encoding = Encoding,
        VideoSourceToken = VideoSourceToken,
        PtzNodeToken = PtzNodeToken,
        ResolutionIsProbed = ResolutionIsProbed,
        Role = Role,
    };

    /// <summary>
    /// 傳回改用實際串流探測解析度之複本。部分攝影機的 ONVIF VideoEncoderConfiguration/Resolution
    /// 與實際串流不符，故以探測值覆寫 Width／Height，讓標籤與主／子碼流分類都依據真實輸出。
    /// </summary>
    public OnvifProfile WithProbedResolution(int width, int height) => new()
    {
        Token = Token,
        Name = Name,
        StreamUri = StreamUri,
        Width = width,
        Height = height,
        Encoding = Encoding,
        VideoSourceToken = VideoSourceToken,
        PtzNodeToken = PtzNodeToken,
        ResolutionIsProbed = true,
        Role = Role,
    };

    /// <summary>顯示用標籤（名稱＋解析度＋主/子標記）。</summary>
    public string DisplayLabel
    {
        get
        {
            var parts = new List<string>(4);
            if (Name.Length > 0)
            {
                parts.Add(Name);
            }

            if (ResolutionLabel.Length > 0)
            {
                parts.Add(ResolutionLabel);
            }

            if (Encoding.Length > 0)
            {
                parts.Add(Encoding);
            }

            parts.Add(IsSubStream ? "子碼流" : "主碼流");
            return string.Join(" · ", parts);
        }
    }
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