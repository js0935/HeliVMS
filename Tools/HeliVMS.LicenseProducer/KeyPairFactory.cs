using System.Security.Cryptography;
using System.Text;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.LicenseProducer;

/// <summary>產生的金鑰對（§19.6「金鑰對產生（2048/3072/4096 可選）」）。</summary>
/// <param name="PrivateKeyText">私鑰檔內容（依 <paramref name="Mode"/> 為明文 PEM 或保護容器）。</param>
/// <param name="PublicKeyPem">SPKI 公鑰 PEM（<c>BEGIN PUBLIC KEY</c>）。</param>
/// <param name="PublicKeySnippet">可直接貼入產品端 <c>Crypto/EmbeddedPublicKey.cs</c> 的 C# 程式碼片段。</param>
/// <param name="PublicKeySha256">SPKI DER 的 SHA-256（Base64URL），寫入稽核以識別金鑰輪替。</param>
/// <param name="KeySizeBits">金鑰長度。</param>
/// <param name="Mode">私鑰保護模式。</param>
public sealed record GeneratedKeyPair(
    string PrivateKeyText,
    string PublicKeyPem,
    string PublicKeySnippet,
    string PublicKeySha256,
    int KeySizeBits,
    KeyProtectionMode Mode);

/// <summary>金鑰對產生器（§19.6）。產出 <c>private.pem</c>／<c>public.key</c>／嵌入用程式碼片段。</summary>
public static class KeyPairFactory
{
    public const string PrivateFileName = "private.pem";
    public const string PublicFileName = "public.key";
    public const string SnippetFileName = "EmbeddedPublicKey.cs.txt";

    public static readonly int[] AllowedKeySizes = [2048, 3072, 4096];

    /// <summary>
    /// 產生 RSA 金鑰對。正式出貨建議 3072／4096；2048 僅供相容既有金鑰與教學。
    /// </summary>
    public static GeneratedKeyPair Generate(
        int keySizeBits,
        KeyProtectionMode mode,
        string? passphrase,
        int iterations = PrivateKeyVault.DefaultIterations)
    {
        if (Array.IndexOf(AllowedKeySizes, keySizeBits) < 0)
        {
            throw new PrivateKeyAccessException(
                $"金鑰長度僅支援 {string.Join('/', AllowedKeySizes)}（§19.6）。");
        }

        using var rsa = RSA.Create(keySizeBits);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var publicPem = RsaPem.ToPublicPem(rsa);
        var privateText = PrivateKeyVault.Protect(rsa, mode, passphrase, iterations);

        var snippet = string.Join(
            "\n",
            "public const string Value =",
            "    \"\"\"",
            Indent(publicPem),
            "    \"\"\";",
            string.Empty);

        return new GeneratedKeyPair(
            privateText,
            publicPem,
            snippet,
            IssuanceAuditTrail.Fingerprint(spki),
            keySizeBits,
            mode);
    }

    /// <summary>
    /// 將金鑰對寫入目錄：<c>private.pem</c>／<c>public.key</c>／<c>EmbeddedPublicKey.cs.txt</c>。
    /// 已存在的私鑰／公鑰檔不會被覆寫（避免誤蓋唯一簽發金鑰）。
    /// </summary>
    public static void WriteTo(GeneratedKeyPair pair, string directory)
    {
        Directory.CreateDirectory(directory);

        var privatePath = Path.Combine(directory, PrivateFileName);
        if (File.Exists(privatePath))
        {
            throw new PrivateKeyAccessException(
                $"私鑰檔已存在，為避免誤覆唯一簽發金鑰而中止：{privatePath}");
        }

        File.WriteAllText(privatePath, pair.PrivateKeyText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, PublicFileName), pair.PublicKeyPem, new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(directory, SnippetFileName), pair.PublicKeySnippet, new UTF8Encoding(false));
    }

    private static string Indent(string pem)
        => string.Join('\n', pem.TrimEnd('\n').Split('\n').Select(l => "    " + l));
}