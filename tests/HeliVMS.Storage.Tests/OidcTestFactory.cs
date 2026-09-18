using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage.Tests;

/// <summary>M50 測試用：自簽 RSA、JWKS 與 JWT 產生器（免網路）。</summary>
internal static class OidcTestFactory
{
    public static (RSA Rsa, string Kid, string Jwks) NewKey(string kid = "test-key")
    {
        var rsa = RSA.Create(2048);
        return (rsa, kid, RsaJwk.ToJwksJson(kid, rsa));
    }

    public static OidcOptions Options(string jwks) => new()
    {
        Issuer = "https://idp.example.com",
        Audience = "helivms",
        JwksJson = jwks,
        UsernameClaim = "preferred_username",
        RoleClaim = "roles",
        AdminGroups = new[] { "vms-admins" },
        DefaultRole = "viewer",
        ClockSkewSeconds = 60,
    };

    public static string Sign(RSA rsa, string kid, IDictionary<string, object?> payload, string alg = "RS256")
    {
        var header = Base64Url.EncodeUtf8(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["alg"] = alg,
            ["typ"] = "JWT",
            ["kid"] = kid,
        }));
        var body = Base64Url.EncodeUtf8(JsonSerializer.Serialize(payload));
        var input = Encoding.ASCII.GetBytes($"{header}.{body}");
        var signature = alg == "none"
            ? Array.Empty<byte>()
            : rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{body}.{Base64Url.Encode(signature)}";
    }

    public static Dictionary<string, object?> Claims(
        long expOffsetSeconds = 300,
        long? nbfOffsetSeconds = null,
        object? roles = null,
        string issuer = "https://idp.example.com",
        object? audience = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audience ?? "helivms",
            ["sub"] = "user-123",
            ["preferred_username"] = "alice",
            ["name"] = "Alice Wang",
            ["roles"] = roles ?? new[] { "vms-viewers" },
            ["exp"] = now + expOffsetSeconds,
        };
        if (nbfOffsetSeconds is long nbf)
        {
            claims["nbf"] = now + nbf;
        }

        return claims;
    }
}
