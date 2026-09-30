namespace HeliVMS.Storage;

/// <summary>
/// 頻道加入（ONVIF 精靈 → channels／devices）。憑證集中存放於 devices.password_encrypted（DPAPI），
/// channels 僅保存不含帳密的 RTSP 位址，實際拉流時才由 ChannelManager 透過
/// <c>HeliVMS.Shared.RtspStreamResolver</c> 組合。
/// </summary>
public static class ChannelEnrollment
{
    /// <summary>
    /// 將精靈結果寫入資料庫：建立或沿用該 IP 的設備記錄，並讓頻道與其關聯（PTZ／RTSP 共用憑證）。
    /// 精靈未提供可用的 IP 或帳號時，僅建立頻道（相容手動輸入的 RTSP 網址）。
    /// 回傳新頻道 ID。
    /// </summary>
    public static int Enroll(
        SqliteStore store,
        string channelName,
        string streamUrl,
        string? deviceIp,
        int devicePort,
        string? deviceUsername,
        string? devicePassword,
        string? subStreamUrl = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var deviceId = ResolveDeviceId(store, channelName, deviceIp, devicePort, deviceUsername, devicePassword);
        return new ChannelRepository(store).Add(channelName, streamUrl, subStreamUrl, deviceId);
    }

    private static int? ResolveDeviceId(
        SqliteStore store,
        string channelName,
        string? deviceIp,
        int devicePort,
        string? deviceUsername,
        string? devicePassword)
    {
        if (string.IsNullOrWhiteSpace(deviceIp) || string.IsNullOrWhiteSpace(deviceUsername))
        {
            return null;
        }

        var devices = new DeviceRepository(store, new AuditLogRepository(store));

        // devices.ip 為 UNIQUE：同一台攝影機重複加入時沿用既有記錄，不另建一筆。
        // 但若帳號／密碼與既有記錄不符（手誤或舊版缺漏），就地修正，
        // 否則會沿用壞掉的憑證而拉不到畫面。
        if (devices.FindByIp(deviceIp) is { } existing)
        {
            if (CredentialsDiffer(devices, existing.Id, deviceUsername, devicePassword))
            {
                devices.SetRtspCredentials(existing.Id, deviceUsername, devicePassword ?? string.Empty);
            }

            return existing.Id;
        }

        var port = devicePort > 0 ? devicePort : 80;
        return devices.Add(
            channelName,
            deviceIp,
            port,
            deviceUsername,
            devicePassword ?? string.Empty,
            vendor: "onvif");
    }

    private static bool CredentialsDiffer(DeviceRepository devices, int deviceId, string username, string? password)
    {
        var (storedUser, storedPassword) = devices.GetRtspCredentials(deviceId);

        if (!string.Equals(storedUser, username, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.Equals(storedPassword, password ?? string.Empty, StringComparison.Ordinal);
    }
}
