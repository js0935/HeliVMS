using System.Security.Cryptography;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Licensing;

/// <summary>
/// 產品端授權驗證服務（§19：驗證流程 = 格式 → 簽章 → 到期 → 機器綁定）。
/// </summary>
public sealed class LicenseManager
{
    private readonly RSA _publicKey;

    public LicenseManager()
    {
        _publicKey = RsaPem.ParsePublicKey(EmbeddedPublicKey.Value);
    }

    /// <summary>預設授權檔路徑（使用者設定區，非 Program Files）。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS",
            "license.lic");

    /// <summary>驗證授權字串。</summary>
    public LicenseState Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new LicenseState(LicenseStatus.NotPresent, null, "未提供授權");
        }

        if (!LicenseSerializer.TryVerify(token, _publicKey, out var payload, out var error))
        {
            return new LicenseState(LicenseStatus.Invalid, null, error ?? "授權驗證失敗");
        }

        if (payload is null)
        {
            return new LicenseState(LicenseStatus.Invalid, null, "授權內容為空");
        }

        if (payload.ExpiresUtc.HasValue && DateTime.UtcNow > payload.ExpiresUtc.Value)
        {
            return new LicenseState(
                LicenseStatus.Expired,
                payload,
                $"授權已於 {payload.ExpiresUtc.Value:u} 到期");
        }

        if (!string.IsNullOrWhiteSpace(payload.Machine) &&
            !string.Equals(payload.Machine, MachineIdProvider.GetFingerprint(), StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseState(
                LicenseStatus.MachineMismatch,
                payload,
                $"機器綁定不符（授權：{payload.Machine}，本機：{MachineIdProvider.GetFingerprint()}）");
        }

        return new LicenseState(LicenseStatus.Valid, payload, null);
    }

    /// <summary>驗證授權檔。</summary>
    public LicenseState ValidateFile(string path)
        => Validate(File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty);

    /// <summary>驗證預設授權檔（%LOCALAPPDATA%\HeliVMS\license.lic）。</summary>
    public LicenseState ValidateDefault() => ValidateFile(DefaultPath);
}