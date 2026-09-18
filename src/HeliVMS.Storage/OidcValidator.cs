using System.Security.Cryptography;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>OIDC 驗證結果（M50，§14.7 #1）。</summary>
public sealed record OidcResult(
    bool Ok,
    string Error,
    string Subject,
    string Username,
    string? DisplayName,
    string Role)
{
    public static OidcResult Fail(string error)
        => new(false, error, string.Empty, string.Empty, null, "viewer");
}

/// <summary>
/// OIDC ID Token 驗證（M50，§14.7 #1）：RS256 簽章、iss／aud／exp／nbf（容許 clock skew），
/// 並依角色宣告對映本地 RBAC。純函式（金鑰由呼叫端提供），CI 可測。
/// </summary>
public static class OidcValidator
{
    public static OidcResult Validate(
        string token,
        OidcOptions options,
        IReadOnlyDictionary<string, RSA> keys,
        DateTime utcNow)
    {
        OidcToken parsed;
        try
        {
            parsed = OidcToken.Parse(token);
        }
        catch (FormatException ex)
        {
            return OidcResult.Fail($"權杖格式錯誤：{ex.Message}");
        }

        if (!string.Equals(parsed.Header.Alg, "RS256", StringComparison.Ordinal))
        {
            return OidcResult.Fail($"不支援的簽章演算法：{parsed.Header.Alg}");
        }

        RSA? key = null;
        if (parsed.Header.Kid is { Length: > 0 } kid)
        {
            keys.TryGetValue(kid, out key);
        }
        else if (keys.Count == 1)
        {
            key = keys.Values.First();
        }

        if (key is null)
        {
            return OidcResult.Fail("找不到對應的簽章金鑰");
        }

        byte[] signature;
        try
        {
            signature = Base64Url.Decode(parsed.SignatureSegment);
        }
        catch (FormatException)
        {
            return OidcResult.Fail("簽章解碼失敗");
        }

        var signingInput = System.Text.Encoding.ASCII.GetBytes(parsed.SigningInput);
        if (!key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return OidcResult.Fail("簽章驗證失敗");
        }

        var payload = parsed.Payload;
        var issuer = OidcToken.GetString(payload, "iss");
        if (!string.Equals(Normalize(issuer), Normalize(options.Issuer), StringComparison.OrdinalIgnoreCase))
        {
            return OidcResult.Fail("簽發者（iss）不符");
        }

        var audience = options.Audience.Length > 0 ? options.Audience : options.ClientId ?? string.Empty;
        if (audience.Length > 0 && !AudienceMatches(payload, audience))
        {
            return OidcResult.Fail("對象（aud）不符");
        }

        var skew = TimeSpan.FromSeconds(Math.Max(0, options.ClockSkewSeconds));
        if (GetLong(payload, "exp") is long exp &&
            DateTimeOffset.FromUnixTimeSeconds(exp) + skew < utcNow)
        {
            return OidcResult.Fail("權杖已過期");
        }

        if (GetLong(payload, "nbf") is long nbf &&
            DateTimeOffset.FromUnixTimeSeconds(nbf) - skew > utcNow)
        {
            return OidcResult.Fail("權杖尚未生效");
        }

        var subject = OidcToken.GetString(payload, "sub") ?? string.Empty;
        var username = OidcToken.GetString(payload, options.UsernameClaim) ?? subject;
        var display = OidcToken.GetString(payload, "name");
        var role = RoleMapper.Map(GetGroups(payload, options.RoleClaim), options.AdminGroups, options.DefaultRole);
        return new OidcResult(true, string.Empty, subject, username, display, role);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).TrimEnd('/');

    private static bool AudienceMatches(JsonElement payload, string expected)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return false;
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => string.Equals(aud.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Array => aud.EnumerateArray().Any(x =>
                x.ValueKind == JsonValueKind.String &&
                string.Equals(x.GetString(), expected, StringComparison.Ordinal)),
            _ => false,
        };
    }

    internal static long? GetLong(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : null,
            _ => null,
        };
    }

    internal static IReadOnlyList<string> GetGroups(JsonElement payload, string claim)
    {
        if (!payload.TryGetProperty(claim, out var v))
        {
            return Array.Empty<string>();
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return (v.GetString() ?? string.Empty)
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (v.ValueKind == JsonValueKind.Array)
        {
            return v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();
        }

        return Array.Empty<string>();
    }
}
