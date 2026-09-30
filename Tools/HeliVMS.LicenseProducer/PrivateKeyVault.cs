using System.Security.Cryptography;
using System.Text;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.LicenseProducer;

/// <summary>私鑰保護模式（§19.6：私鑰可選加密靜置，DPAPI／口令）。</summary>
public enum KeyProtectionMode
{
    /// <summary>明文 PKCS#8 PEM（僅限隔離簽發機；相容既有流程）。</summary>
    Plain = 0,

    /// <summary>口令保護：AES-256-GCM + PBKDF2-HMAC-SHA256（可攜、可離線）。</summary>
    Passphrase = 1,

    /// <summary>DPAPI CurrentUser（僅同一 Windows 帳號可解，不需口令）。</summary>
    Dpapi = 2,
}

/// <summary>私鑰載入／產生失敗（口令錯誤時與其他錯誤可區別，CLI 據此給不同提示與退出碼）。</summary>
public sealed class PrivateKeyAccessException : Exception
{
    public PrivateKeyAccessException(string message)
        : base(message)
    {
    }

    public PrivateKeyAccessException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 私鑰保護容器（§19.6「密碼與金鑰安全：私鑰可選加密靜置（DPAPI/口令）；與程式碼分開存放」）。
/// 私鑰永不進 git（.secrets/ 已排除）；容器為 base64 包裹的自描述二進位：
/// <code>
/// magic "HELVMSKEY"(9B) | mode(1B) | iterations(4B, BE) | salt(16B) | nonce(12B) | 密文(含 GCM tag 16B)
/// </code>
/// 口令模式的 salt 隨機、iterations 記錄於檔內以利日後調高；DPAPI 模式 salt／nonce 為 0、iterations=0。
/// </summary>
public static class PrivateKeyVault
{
    public const string BeginLabel = "-----BEGIN HELIVMS PROTECTED PRIVATE KEY-----";
    public const string EndLabel = "-----END HELIVMS PROTECTED PRIVATE KEY-----";

    /// <summary>PBKDF2 迭代次數（OWASP 2023 建議 PBKDF2-HMAC-SHA256 ≥ 600,000；此處取較低值兼顧簽發機效能）。</summary>
    public const int DefaultIterations = 210_000;

    public const int MinimumIterations = 50_000;

    private static readonly byte[] Magic = "HELVMSKEY"u8.ToArray();

    private static readonly byte[] DerAssociatedData = "HELVMSKEY"u8.ToArray();

    private static readonly byte[] Entropy =
        "HeliVMS-LicenseProducer-v1"u8.ToArray();

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 9 + 1 + 4 + SaltSize + NonceSize;

    /// <summary>文字是否為本容器格式（非明文 PKCS#8 PEM）。</summary>
    public static bool IsProtected(string text)
        => text.Contains(BeginLabel, StringComparison.Ordinal);

    /// <summary>
    /// 依保護模式輸出私鑰檔內容；<see cref="KeyProtectionMode.Plain"/> 回傳標準 PKCS#8 PEM。
    /// </summary>
    public static string Protect(RSA privateKey, KeyProtectionMode mode, string? passphrase, int iterations)
    {
        var der = privateKey.ExportPkcs8PrivateKey();

        switch (mode)
        {
            case KeyProtectionMode.Plain:
                return ToPem("PRIVATE KEY", der);

            case KeyProtectionMode.Passphrase:
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new PrivateKeyAccessException("口令模式需提供口令（--passphrase-env／--passphrase-file／--prompt-passphrase）。");
                }

                if (iterations < MinimumIterations)
                {
                    throw new PrivateKeyAccessException($"PBKDF2 迭代次數不可低於 {MinimumIterations}。");
                }

                var salt = RandomNumberGenerator.GetBytes(SaltSize);
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var key = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
                var cipher = new byte[der.Length];
                var tag = new byte[TagSize];
                using (var gcm = new AesGcm(key, TagSize))
                {
                    gcm.Encrypt(nonce, der, cipher, tag, DerAssociatedData);
                }

                return ToPem(
                    BeginLabel, EndLabel,
                    Concat(Magic, [(byte)KeyProtectionMode.Passphrase], BigEndian(iterations), salt, nonce, cipher, tag));

            case KeyProtectionMode.Dpapi:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PrivateKeyAccessException("DPAPI 僅支援 Windows；請改用口令模式（--passphrase-env）。");
                }

                var sealedDer = ProtectedData.Protect(der, Entropy, DataProtectionScope.CurrentUser);
                return ToPem(
                    BeginLabel, EndLabel,
                    Concat(Magic, [(byte)KeyProtectionMode.Dpapi], BigEndian(0), new byte[SaltSize + NonceSize], sealedDer));

            default:
                throw new PrivateKeyAccessException($"未知的私鑰保護模式 {(int)mode}。");
        }
    }

    /// <summary>
    /// 載入私鑰：明文 PKCS#8 PEM 或本保護容器。容器需對應的 <paramref name="passphrase"/>
    /// （DPAPI 模式可為 null）。口令錯誤／容器損毀一律回 <see cref="PrivateKeyAccessException"/>。
    /// </summary>
    public static RSA Open(string text, string? passphrase)
    {
        if (!IsProtected(text))
        {
            try
            {
                return RsaPem.ParsePrivateKey(text);
            }
            catch (ArgumentException ex)
            {
                throw new PrivateKeyAccessException("私鑰檔既非明文 PKCS#8 PEM 亦非受保護容器：" + ex.Message, ex);
            }
        }

        var body = DecodeBody(text, BeginLabel, EndLabel);
        if (body.Length < HeaderSize + 1)
        {
            throw new PrivateKeyAccessException("私鑰保護容器長度不足（檔案損毀或非本工具產生）。");
        }

        if (!body.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new PrivateKeyAccessException("私鑰保護容器 magic 不符（檔案損毀或非本工具產生）。");
        }

        var mode = (KeyProtectionMode)body[9];
        var iterations = ReadBigEndian(body.AsSpan(10, 4));
        var salt = body[14..(14 + SaltSize)];
        var nonce = body[(14 + SaltSize)..(14 + SaltSize + NonceSize)];
        var cipherAndTag = body[HeaderSize..];

        byte[] der;
        switch (mode)
        {
            case KeyProtectionMode.Passphrase:
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new PrivateKeyAccessException("私鑰為口令保護，需提供口令（--passphrase-env／--passphrase-file／--prompt-passphrase）。");
                }

                if (cipherAndTag.Length < TagSize)
                {
                    throw new PrivateKeyAccessException("私鑰保護容器密文長度不足（檔案損毀）。");
                }

                var key = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
                var cipher = cipherAndTag[..^TagSize];
                var tag = cipherAndTag[^TagSize..];
                der = new byte[cipher.Length];
                try
                {
                    using var gcm = new AesGcm(key, TagSize);
                    gcm.Decrypt(nonce, cipher, tag, der, DerAssociatedData);
                }
                catch (CryptographicException ex)
                {
                    throw new PrivateKeyAccessException("口令錯誤或私鑰檔已損毀（完整性驗證未通過）。", ex);
                }

                break;

            case KeyProtectionMode.Dpapi:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PrivateKeyAccessException("私鑰為 DPAPI 保護，僅能於產生該檔的 Windows 帳號解開。");
                }

                try
                {
                    der = ProtectedData.Unprotect(cipherAndTag, Entropy, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException ex)
                {
                    throw new PrivateKeyAccessException("DPAPI 解密失敗（檔案損毀，或非產生該檔的 Windows 帳號）。", ex);
                }

                break;

            default:
                throw new PrivateKeyAccessException($"未知的私鑰保護模式 {(int)mode}。");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(der, out _);
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new PrivateKeyAccessException("私鑰內容不是有效的 PKCS#8 資料。", ex);
        }

        return rsa;
    }

    /// <summary>
    /// 從私鑰檔文字載入；未受保護時回傳 null 供呼叫端判斷「本次未提供口令是否合理」。
    /// </summary>
    public static KeyProtectionMode? DetectMode(string text)
    {
        if (!IsProtected(text))
        {
            return null;
        }

        var body = DecodeBody(text, BeginLabel, EndLabel);
        if (body.Length < HeaderSize + 1 || !body.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new PrivateKeyAccessException("私鑰保護容器 magic 不符（檔案損毀或非本工具產生）。");
        }

        return (KeyProtectionMode)body[9];
    }

    private static byte[] DecodeBody(string text, string begin, string end)
    {
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        var stop = text.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < 0 || stop < start)
        {
            throw new PrivateKeyAccessException("私鑰保護容器標頭／標尾不完整。");
        }

        var body = new StringBuilder();
        foreach (var line in text[(start + begin.Length)..stop].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                body.Append(trimmed);
            }
        }

        try
        {
            return Convert.FromBase64String(body.ToString());
        }
        catch (FormatException ex)
        {
            throw new PrivateKeyAccessException("私鑰保護容器 base64 解碼失敗。", ex);
        }
    }

    private static string ToPem(string label, byte[] der) => ToPem(
        $"-----BEGIN {label}-----",
        $"-----END {label}-----",
        der);

    private static string ToPem(string begin, string end, byte[] body)
    {
        var base64 = Convert.ToBase64String(body);
        var sb = new StringBuilder(begin.Length + end.Length + base64.Length + 64);
        sb.Append(begin).Append('\n');
        for (var i = 0; i < base64.Length; i += 64)
        {
            sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        sb.Append(end).Append('\n');
        return sb.ToString();
    }

    private static byte[] BigEndian(int value)
        => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static int ReadBigEndian(ReadOnlySpan<byte> span)
        => (span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3];

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}