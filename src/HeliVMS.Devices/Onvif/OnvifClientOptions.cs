namespace HeliVMS.Devices.Onvif;

/// <summary>
/// ONVIF 連線選項。預設值取 <see cref="Default"/>，針對多廠牌相容性設定：
/// 自動認證切換、暫時性錯誤重試，以及避免 NVR 大量 Profile 時停頓的串流位址解析預算。
/// </summary>
public sealed record OnvifClientOptions
{
    public static OnvifClientOptions Default { get; } = new();

    /// <summary>單次 SOAP 請求逾時（部分廠牌首次 GetCapabilities 較慢，必要時調大）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>認證模式；<see cref="OnvifAuthMode.Auto"/> 依設備回應自動切換（相容性最佳）。</summary>
    public OnvifAuthMode AuthMode { get; init; } = OnvifAuthMode.Auto;

    /// <summary>可重試請求（Get 系列）之暫時性錯誤重試次數；PTZ 等動作預設不重試。</summary>
    public int RetryCount { get; init; } = 1;

    /// <summary>重試間隔。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>GetProfiles 時解析串流位址之總時間預算（避免 NVR 數十個 profile 造成長時間停頓）。</summary>
    public TimeSpan StreamUriBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>GetProfiles 時最多解析幾筆串流位址；其餘以選取時即時解析（見 GetStreamUriAsync）。</summary>
    public int MaxStreamUris { get; init; } = 8;
}
