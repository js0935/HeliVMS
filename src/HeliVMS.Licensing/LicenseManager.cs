using System.Security.Cryptography;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Licensing;

/// <summary>
/// 產品端授權驗證服務（§19：驗證流程 = 格式 → 簽章 → 到期 → 機器綁定）。
/// </summary>
public sealed class LicenseManager
{
    private readonly RSA _publicKey;
    private readonly Func<string>? _tokenSource;

    public LicenseManager()
        : this(EmbeddedPublicKey.Value)
    {
    }

    /// <summary>
    /// 以指定公鑰建立驗證服務。正式出貨與測試金鑰輪替用同一條驗證路徑——
    /// 換金鑰時只需換這支公鑰，不需改任何驗證邏輯（§19.1）。
    /// </summary>
    public LicenseManager(string publicKeyPem)
        : this(publicKeyPem, null)
    {
    }

    /// <summary>
    /// 以指定公鑰與授權碼來源建立驗證服務。
    /// </summary>
    /// <param name="publicKeyPem">驗證用公鑰。</param>
    /// <param name="tokenSource">
    /// 授權碼來源；<see langword="null"/> 表示讀 <see cref="DefaultPath"/>。
    /// </param>
    /// <remarks>
    /// 為什麼要有這個參數：授權碼來源原本是 <c>LicenseService</c> 裡一個 static 方法
    /// 直接讀死路徑，沒有任何注入點。於是「未匯入授權」情境的測試會去讀<em>開發者機器上</em>
    /// 真正的 <c>%LOCALAPPDATA%\HeliVMS\license.lic</c>——CI 乾淨所以綠，有人匯入授權的
    /// 開發機就紅，而且紅的原因（<c>Valid</c> vs <c>NotPresent</c>）看起來像授權邏輯壞掉。
    /// 把來源變成可注入，測試才能真正自足。
    /// </remarks>
    public LicenseManager(string publicKeyPem, Func<string>? tokenSource)
    {
        _publicKey = RsaPem.ParsePublicKey(publicKeyPem);
        _tokenSource = tokenSource;
    }

    /// <summary>預設授權檔路徑（使用者設定區，非 Program Files）。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS",
            "license.lic");

    /// <summary>讀取目前的預設授權碼；檔案不存在時回傳空字串。</summary>
    public string ReadDefaultToken()
        => (_tokenSource ?? ReadDefaultTokenFromFile)().Trim();

    private static string ReadDefaultTokenFromFile()
    {
        var path = DefaultPath;
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>驗證授權字串。</summary>
    /// <param name="token">
    /// 完整授權碼。支援兩種格式：
    /// <list type="bullet">
    ///   <item><c>AAAA-BBBB-CCCC-DDDD</c>（<see cref="HmacLicenseKey"/>，<c>Tools/LicenseKeyGen</c> 產製，預設路徑）</item>
    ///   <item><c>HELVMS-v2.{payload}.{sig}</c>（RSA-2048 簽章，舊格式）</item>
    /// </list>
    /// </param>
    /// <param name="nowUtc">
    /// 以此時間判斷到期（預設 <see cref="DateTime.UtcNow"/>）。
    /// 匯入時間戳記（可測）是必要的：時鐘回流防護要拿同一個「現在」去比對，
    /// 否則測試與實際判斷會各取一次時間而無法重現。
    /// </param>
    /// <remarks>
    /// 兩種格式的處置順序不可對調：<see cref="HmacLicenseKey.IsKeyFormat"/> 已經明確
    /// 排除 <see cref="LicenseSerializer.Prefix"/>，所以先判 HMAC 才不會讓
    /// <c>HELVMS-v2.*</c> 掉進 HMAC 的解碼錯誤訊息裡。
    /// </remarks>
    public LicenseState Validate(string token, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new LicenseState(LicenseStatus.NotPresent, null, "未提供授權");
        }

        var now = nowUtc ?? DateTime.UtcNow;

        if (HmacLicenseKey.IsKeyFormat(token))
        {
            return HmacLicenseKey.Validate(token, MachineIdProvider.GetDeviceCode(), now);
        }

        if (!LicenseSerializer.TryVerify(token, _publicKey, out var payload, out var error))
        {
            return new LicenseState(LicenseStatus.Invalid, null, error ?? "授權驗證失敗");
        }

        if (payload is null)
        {
            return new LicenseState(LicenseStatus.Invalid, null, "授權內容為空");
        }

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