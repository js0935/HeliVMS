namespace HeliVMS.Shared.Models;

/// <summary>設備資訊（§4 devices 表）。</summary>
public sealed class DeviceRecord
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Ip { get; init; } = string.Empty;

    public int Port { get; init; } = 80;

    /// <summary>ONVIF 帳號（僅 DeviceRepository.Get 帶入）。</summary>
    public string? Username { get; init; }

    /// <summary>ONVIF 密碼（DPAPI 加密存放；僅 DeviceRepository.Get 帶入原密文）。</summary>
    public string? PasswordEncrypted { get; init; }

    /// <summary>供應商（hikvision／dahua／onvif／generic）。</summary>
    public string Vendor { get; init; } = "generic";

    public bool Enabled { get; init; } = true;

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// 依時間範圍查詢區段的請求。
/// </summary>
public sealed record SegmentQuery(
    int ChannelId,
    DateTime FromUtc,
    DateTime ToUtc,
    string? Status = null);