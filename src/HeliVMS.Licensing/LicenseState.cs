namespace HeliVMS.Licensing;

/// <summary>
/// 授權驗證狀態。
/// </summary>
public enum LicenseStatus
{
    /// <summary>未提供授權檔。</summary>
    NotPresent,

    /// <summary>有效。</summary>
    Valid,

    /// <summary>已過期。</summary>
    Expired,

    /// <summary>格式不正確。</summary>
    Invalid,

    /// <summary>簽章驗證失敗（竄改／金鑰不符）。</summary>
    Tampered,

    /// <summary>機器綁定不符合。</summary>
    MachineMismatch,
}

/// <summary>
/// 授權驗證結果。
/// </summary>
public sealed record LicenseState(
    LicenseStatus Status,
    LicensePayload? Payload,
    string? Message)
{
    public bool IsValid => Status == LicenseStatus.Valid;
}