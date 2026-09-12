namespace HeliVMS.Licensing;

/// <summary>
/// 授權乘載資料（HELVMS-v2 格式的 payload 本體）。
/// </summary>
public sealed class LicensePayload
{
    /// <summary>格式版本。</summary>
    public int Ver { get; set; } = 2;

    /// <summary>授權唯一識別碼。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>綁定機器指紋（§19：RSA 授權含機器綁定）。</summary>
    public string Machine { get; set; } = string.Empty;

    /// <summary>發行時間（UTC）。</summary>
    public DateTime IssuedUtc { get; set; }

    /// <summary>到期時間（UTC，null 表示永久）。</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>授權通道數上限。</summary>
    public int Cameras { get; set; }

    /// <summary>啟用功能旗標（core、ai、gis、io...）。</summary>
    public string[] Features { get; set; } = Array.Empty<string>();

    /// <summary>發行者。</summary>
    public string Issuer { get; set; } = "禾秝軟體開發團隊";
}