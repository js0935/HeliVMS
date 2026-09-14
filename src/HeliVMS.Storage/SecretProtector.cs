using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// 密碼保護（§21.2 credential 集中保存）：DPAPI CurrentUser 加解密，
/// 與 devices 表 password_encrypted 同思路，避免明文落 sqlite。
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = { 0x48, 0x65, 0x4C, 0x69, 0x56, 0x4D, 0x53, 0x2D, 0x4E, 0x6F, 0x74, 0x69, 0x66, 0x79 };

    /// <summary>以 DPAPI 加密（回傳 Base64）；空字串直接回傳空白。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SecretProtector（DPAPI）僅支援 Windows。");
        }

        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// 以 DPAPI 解密；若無法解密（例如舊明文或無效資料）則原樣回傳，
    /// 相容既有未加密設定。
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        try
        {
            return UnprotectCore(stored);
        }
        catch (Exception)
        {
            return stored;
        }
    }

    private static string UnprotectCore(string stored)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SecretProtector（DPAPI）僅支援 Windows。");
        }

        var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}