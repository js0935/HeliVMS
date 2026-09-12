using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeliVMS.Licensing;

/// <summary>
/// 授權序號序列化：`HELVMS-v2.{Base64Url(payload)}.{Base64Url(signature)}`
/// 簽章演算法：RSA-2048 / SHA-256 / PKCS#1 v1.5（§19 定案）。
/// </summary>
public static class LicenseSerializer
{
    public const string Prefix = "HELVMS-v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 以私鑰簽署 payload，產生授權字串（廠商用）。
    /// </summary>
    public static string Sign(LicensePayload payload, RSA privateKey)
    {
        var payloadBytes = SerializePayload(payload);
        var signature = privateKey.SignData(
            payloadBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{Prefix}.{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signature)}";
    }

    /// <summary>
    /// 以公鑰驗證授權字串，成功回傳 payload。
    /// </summary>
    public static bool TryVerify(string token, RSA publicKey, out LicensePayload? payload, out string? error)
    {
        payload = null;
        error = null;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            error = $"格式不符：應為 {Prefix}.{{payload}}.{{signature}}";
            return false;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            error = "Base64Url 解碼失敗";
            return false;
        }

        var validSignature = publicKey.VerifyData(
            payloadBytes,
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (!validSignature)
        {
            error = "簽章驗證失敗（內容遭竄改或金鑰不符）";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, JsonOptions)
                ?? throw new JsonException("payload 為 null");
            return true;
        }
        catch (JsonException ex)
        {
            error = "payload 剖析失敗：" + ex.Message;
            return false;
        }
    }

    internal static byte[] SerializePayload(LicensePayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
}