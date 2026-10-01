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
        : this(EmbeddedPublicKey.Value)
    {
    }

    /// <summary>
    /// 以指定公鑰建立驗證服務。正式出貨與測試金鑰輪替用同一條驗證路徑——
    /// 換金鑰時只需換這支公鑰，不需改任何驗證邏輯（§19.1）。
    /// </summary>
    public LicenseManager(string publicKeyPem)
    {
        _publicKey = RsaPem.ParsePublicKey(publicKeyPem);
    }

    /// <summary>預設授權檔路徑（使用者設定區，非 Program Files）。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS",
            "license.lic");

    /// <summary>驗證授權字串。</summary>
    /// <param name="token">完整授權碼。</param>
    /// <param name="nowUtc">
    /// 以此時間判斷到期（預設 <see cref="DateTime.UtcNow"/>）。
    /// 匯入時間戳記（可測）是必要的：時鐘回流防護要拿同一個「現在」去比對，
    /// 否則測試與實際判斷會各取一次時間而無法重現。
    /// </param>
    public LicenseState Validate(string token, DateTime? nowUtc = null)
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

        var now = nowUtc ?? DateTime.UtcNow;
        if (payload.ExpiresUtc.HasValue && now > payload.ExpiresUtc.Value)
        {
            return new LicenseState(
                LicenseStatus.Expired,
                payload,
                $"授權已於 {payload.ExpiresUtc.Value:u} 到期");
        }

        if (!string.IsNullOrWhiteSpace(payload.Machine) && !IsThisMachine(payload.Machine))
        {
            return new LicenseState(
                LicenseStatus.MachineMismatch,
                payload,
                $"機器綁定不符（授權：{payload.Machine}，本機：{MachineIdProvider.GetDeviceCode()}）");
        }

        return new LicenseState(LicenseStatus.Valid, payload, null);
    }

    /// <summary>
    /// 比對授權綁定的機器碼。接受規範設備碼與舊版 MAC 派生指紋，
    /// 避免先前已簽發的授權因設備碼演算法統一而失效（§19.1）。
    /// </summary>
    private static bool IsThisMachine(string machine)
    {
        foreach (var code in MachineIdProvider.GetAcceptedCodes())
        {
            if (string.Equals(machine, code, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}