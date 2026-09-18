using System.Security.Cryptography;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// JWKS（JSON Web Key Set）解析（M50，§14.7 #1）：RSA 公開金鑰（n／e）→ <see cref="RSA"/>。
/// </summary>
public static class RsaJwk
{
    public static RSA FromModulusExponent(string modulusBase64Url, string exponentBase64Url)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64Url.Decode(modulusBase64Url),
            Exponent = Base64Url.Decode(exponentBase64Url),
        });
        return rsa;
    }

    /// <summary>解析 JWKS；僅取 <c>kty=RSA</c> 且含 n／e 的金鑰（key＝kid，缺 kid 則以序號命名）。</summary>
    public static IReadOnlyDictionary<string, RSA> ParseJwks(string jwksJson)
    {
        var result = new Dictionary<string, RSA>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            return result;
        }

        using var doc = JsonDocument.Parse(jwksJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var index = 0;
        foreach (var key in keys.EnumerateArray())
        {
            if (!string.Equals(OidcToken.GetString(key, "kty"), "RSA", StringComparison.Ordinal))
            {
                continue;
            }

            var n = OidcToken.GetString(key, "n");
            var e = OidcToken.GetString(key, "e");
            if (n is null || e is null)
            {
                continue;
            }

            var kid = OidcToken.GetString(key, "kid") ?? $"kid-{index}";
            result[kid] = FromModulusExponent(n, e);
            index++;
        }

        return result;
    }

    /// <summary>由 RSA 金鑰匯出 JWKS 單鍵 JSON（harness／測試用）。</summary>
    public static string ToJwksJson(string kid, RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        var json = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid,
                    n = Base64Url.Encode(p.Modulus!),
                    e = Base64Url.Encode(p.Exponent!),
                },
            },
        });
        return json;
    }
}
