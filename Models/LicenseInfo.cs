using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class LicenseInfo
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; set; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "HeliVMS Free";

    [JsonPropertyName("licensee")]
    public string Licensee { get; set; } = "";

    [JsonPropertyName("licenseType")]
    public string LicenseType { get; set; } = "免費版";

    [JsonPropertyName("maxCameras")]
    public int MaxCameras { get; set; } = 4;

    [JsonPropertyName("expiryDate")]
    public string? ExpiryDate { get; set; }

    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = "";

    [JsonPropertyName("activatedAt")]
    public DateTime ActivatedAt { get; set; }

    /// <summary>License invalidation reason (e.g. hardware change), set only in service layer, not serialized to JSON</summary>
    [JsonIgnore]
    public string? InvalidationReason { get; set; }

    [JsonIgnore]
    public bool IsValid => string.IsNullOrEmpty(InvalidationReason) && !string.IsNullOrEmpty(LicenseKey);

    [JsonIgnore]
    public bool IsExpired
    {
        get
        {
            if (string.IsNullOrEmpty(ExpiryDate)) return false;
            if (DateTime.TryParse(ExpiryDate, out var expiry))
                return expiry < DateTime.Now;
            return false;
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(InvalidationReason))
                return InvalidationReason;
            return IsValid ? (IsExpired ? "已過期" : "已授權") : "未授權";
        }
    }
}
