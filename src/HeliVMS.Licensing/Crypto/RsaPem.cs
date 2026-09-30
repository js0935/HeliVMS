using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Licensing.Crypto;

/// <summary>
/// RSA PEM 金鑰解析工具（§19：RSA-2048 非對稱簽章）。
/// 支援 PEM 格式：SPKI 公鑰（BEGIN PUBLIC KEY）、PKCS#8 私鑰（BEGIN PRIVATE KEY）。
/// </summary>
public static class RsaPem
{
    public static RSA ParsePublicKey(string publicKeyPem)
    {
        var der = DecodePemBody(publicKeyPem, "PUBLIC KEY");
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(der, out _);
        return rsa;
    }

    public static RSA ParsePrivateKey(string privateKeyPem)
    {
        var der = DecodePemBody(privateKeyPem, "PRIVATE KEY");
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(der, out _);
        return rsa;
    }

    /// <summary>由金鑰匯出 SPKI 公鑰 PEM（<c>BEGIN PUBLIC KEY</c>）。</summary>
    public static string ToPublicPem(RSA key)
    {
        return ToPem("PUBLIC KEY", key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>由金鑰匯出 PKCS#8 私鑰 PEM（<c>BEGIN PRIVATE KEY</c>）。</summary>
    public static string ToPrivatePem(RSA key)
    {
        return ToPem("PRIVATE KEY", key.ExportPkcs8PrivateKey());
    }

    private static string ToPem(string label, byte[] der)
    {
        var base64 = Convert.ToBase64String(der);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN ").Append(label).Append("-----\n");
        for (var i = 0; i < base64.Length; i += 64)
        {
            sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        sb.Append("-----END ").Append(label).Append("-----\n");
        return sb.ToString();
    }

    private static byte[] DecodePemBody(string pem, string label)
    {
        var body = new StringBuilder();
        foreach (var line in pem.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal) ||
                trimmed.StartsWith("-----END", StringComparison.Ordinal) ||
                trimmed.Length == 0)
            {
                continue;
            }

            body.Append(trimmed);
        }

        if (body.Length == 0)
        {
            throw new ArgumentException($"PEM 不含 [{label}] 內容", nameof(pem));
        }

        try
        {
            return Convert.FromBase64String(body.ToString());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"PEM [{label}] base64 解碼失敗", nameof(pem), ex);
        }
    }
}