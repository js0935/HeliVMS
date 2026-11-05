namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 探索結果的身分（用來去重）。
/// </summary>
/// <remarks>
/// <para>
/// 同一台設備可能被多個來源找到：多網卡各自送出多播、掃描時主機名與 IP 同時命中、或設備
/// 本身有多個網路介面。清單上重複出現會讓操作員以為有兩台，而重複加入會替同一台攝影機
/// 開出第二個頻道（時間軸上各錄一半）。
/// </para>
/// <para>
/// 判準：WS-Discovery 回的裝置 UUID 最可信；沒有 UUID 時（HTTP 掃描的結果）退回 XAddr。
/// </para>
/// </remarks>
public static class OnvifDiscoveryIdentity
{
    /// <summary>去重用的鍵（大小寫不敏感）。</summary>
    public static string KeyOf(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        return device.EndpointAddress.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
            ? device.EndpointAddress
            : device.HttpXAddr ?? device.EndpointAddress;
    }
}
