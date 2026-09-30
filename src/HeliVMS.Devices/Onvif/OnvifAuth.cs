using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace HeliVMS.Devices.Onvif;

/// <summary>
/// ONVIF 認證模式（多廠牌相容）。<see cref="Auto"/> 依設備回應自動切換：
/// 預設送出 WS-Security UsernameToken（PasswordDigest）；
/// 設備回 HTTP 401 Digest/Basic 挑戰時改用 HTTP 層認證；
/// 設備回 WS-Security 認證失敗 Fault 時改用 PasswordText（部分廠牌僅接受明碼）。
/// </summary>
public enum OnvifAuthMode
{
    /// <summary>自動偵測（預設；相容性最佳）。</summary>
    Auto,

    /// <summary>不送出任何認證資訊。</summary>
    None,

    /// <summary>WS-Security UsernameToken＋PasswordDigest（ONVIF Profile S 標準）。</summary>
    WsSecurityDigest,

    /// <summary>WS-Security UsernameToken＋PasswordText（部分 NVR／IPC 僅接受此模式）。</summary>
    WsSecurityText,

    /// <summary>HTTP Digest（部分廠牌改以 HTTP 層認證，不再使用 WS-Security）。</summary>
    HttpDigest,

    /// <summary>HTTP Basic。</summary>
    HttpBasic,
}

/// <summary>WS-Security UsernameToken 之密碼傳送形式。</summary>
internal enum OnvifWsSecurityMode
{
    None,
    PasswordDigest,
    PasswordText,
}

/// <summary>
/// 由 WWW-Authenticate 挑戰解析之 HTTP 認證參數，並可產生 Digest/Basic 之
/// <c>Authorization</c> 值。相容各廠牌常見之 realm／nonce／qop／algorithm 變化。
/// </summary>
internal sealed class OnvifHttpChallenge
{
    internal const string DigestScheme = "Digest";
    internal const string BasicScheme = "Basic";

    private OnvifHttpChallenge(string scheme, IReadOnlyDictionary<string, string> parameters)
    {
        Scheme = scheme;
        Realm = Get(parameters, "realm");
        Nonce = Get(parameters, "nonce");
        Opaque = Get(parameters, "opaque");
        Qop = SelectQop(Get(parameters, "qop"));
        Algorithm = (Get(parameters, "algorithm") ?? "MD5").Trim();
    }

    /// <summary>認證方案（Digest／Basic）。</summary>
    public string Scheme { get; }

    public string Realm { get; }

    public string Nonce { get; }

    public string Opaque { get; }

    /// <summary>選用之 qop（僅支援 auth；空字串表示 RFC 2069 舊制）。</summary>
    public string Qop { get; }

    /// <summary>摘要演算法（MD5／MD5-sess／SHA-256…）。</summary>
    public string Algorithm { get; }

    public bool IsDigest => string.Equals(Scheme, DigestScheme, StringComparison.OrdinalIgnoreCase);

    public bool IsBasic => string.Equals(Scheme, BasicScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>產生此挑戰的識別碼（用於避免重複套用同一挑戰造成迴圈）。</summary>
    public string Identity => $"{Scheme}|{Realm}|{Nonce}";

    /// <summary>解析回應之 WWW-Authenticate；取第一個可支援的挑戰（Digest 優先）。</summary>
    public static bool TryCreate(HttpResponseMessage response, out OnvifHttpChallenge? challenge)
    {
        challenge = null;
        var raw = new List<string>();
        if (response.Headers.TryGetValues("WWW-Authenticate", out var values))
        {
            raw.AddRange(values);
        }
        else if (response.Headers.WwwAuthenticate.FirstOrDefault() is AuthenticationHeaderValue typed)
        {
            raw.Add(typed.ToString());
        }

        OnvifHttpChallenge? fallback = null;
        foreach (var value in raw)
        {
            var parsed = TryParse(value);
            if (parsed is null)
            {
                continue;
            }

            if (parsed.IsDigest)
            {
                challenge = parsed;
                return true;
            }

            fallback ??= parsed;
        }

        challenge = fallback;
        return challenge is not null;
    }

    /// <summary>解析單一 WWW-Authenticate 標頭值（容忍無引號之參數值）。</summary>
    public static OnvifHttpChallenge? TryParse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var value = headerValue.Trim();
        var separator = value.IndexOfAny([' ', '\t']);
        if (separator <= 0)
        {
            return null;
        }

        var scheme = value[..separator];
        if (!scheme.Equals(DigestScheme, StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals(BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new OnvifHttpChallenge(
            scheme,
            ParseParameters(value[(separator + 1)..]));
    }

    /// <summary>產生 <c>Authorization</c> 標頭值；Digest 會遞增 nonce 計數。</summary>
    public string BuildAuthorization(
        string method,
        string pathAndQuery,
        string username,
        string password)
    {
        if (IsBasic)
        {
            return $"{BasicScheme} {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";
        }

        var cnonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var nonceCount = (++_nonceCount).ToString("x8", CultureInfo.InvariantCulture);

        var ha1 = Hash($"{username}:{Realm}:{password}");
        if (Algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
        {
            ha1 = Hash($"{ha1}:{Nonce}:{cnonce}");
        }

        var ha2 = Hash($"{method}:{pathAndQuery}");
        var response = Qop.Length > 0
            ? Hash($"{ha1}:{Nonce}:{nonceCount}:{cnonce}:{Qop}:{ha2}")
            : Hash($"{ha1}:{Nonce}:{ha2}");

        var parts = new List<string>(10)
        {
            $"username=\"{Escape(username)}\"",
            $"realm=\"{Escape(Realm)}\"",
            $"nonce=\"{Escape(Nonce)}\"",
            $"uri=\"{Escape(pathAndQuery)}\"",
            $"response=\"{response}\"",
        };

        if (Qop.Length > 0)
        {
            parts.Add($"qop={Qop}");
            parts.Add($"nc={nonceCount}");
            parts.Add($"cnonce=\"{Escape(cnonce)}\"");
        }

        if (Opaque.Length > 0)
        {
            parts.Add($"opaque=\"{Escape(Opaque)}\"");
        }

        parts.Add($"algorithm={Algorithm}");
        return $"{DigestScheme} {string.Join(", ", parts)}";
    }

    private int _nonceCount;

    private string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var name = Algorithm.Trim().ToUpperInvariant();
        if (name.EndsWith("-SESS", StringComparison.Ordinal))
        {
            name = name[..^"-SESS".Length];
        }

        var digest = name.Replace("-", string.Empty, StringComparison.Ordinal) switch
        {
            "SHA256" => SHA256.HashData(bytes),
            "SHA384" => SHA384.HashData(bytes),
            "SHA512" => SHA512.HashData(bytes),
            _ => MD5.HashData(bytes),
        };
        return Convert.ToHexStringLower(digest);
    }

    private static string SelectQop(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        foreach (var option in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (option.Equals("auth", StringComparison.OrdinalIgnoreCase))
            {
                return "auth";
            }
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> ParseParameters(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < raw.Length)
        {
            while (index < raw.Length && (char.IsWhiteSpace(raw[index]) || raw[index] == ','))
            {
                index++;
            }

            var keyStart = index;
            while (index < raw.Length && raw[index] != '=' && raw[index] != ',')
            {
                index++;
            }

            var key = raw[keyStart..index].Trim();
            if (index >= raw.Length || raw[index] == ',')
            {
                if (key.Length > 0)
                {
                    result[key] = string.Empty;
                }

                continue;
            }

            index++;

            string value;
            if (index < raw.Length && raw[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < raw.Length)
                {
                    if (raw[index] == '\\' && index + 1 < raw.Length)
                    {
                        builder.Append(raw[index + 1]);
                        index += 2;
                        continue;
                    }

                    if (raw[index] == '"')
                    {
                        index++;
                        break;
                    }

                    builder.Append(raw[index]);
                    index++;
                }

                value = builder.ToString();
            }
            else
            {
                var valueStart = index;
                while (index < raw.Length && raw[index] != ',')
                {
                    index++;
                }

                value = raw[valueStart..index].Trim();
            }

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>WS-Security UsernameToken 封包建構（PasswordDigest 或 PasswordText）。</summary>
internal static class OnvifWsSecurity
{
    internal const string PasswordDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    internal const string PasswordTextType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";
    internal const string Base64EncodingType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>依模式產生 <c>wse:Security</c> 元素；未提供帳密時回傳 null（空密碼仍會送出封包）。</summary>
    public static XElement? Build(
        string? username,
        string? password,
        OnvifWsSecurityMode mode)
    {
        if (string.IsNullOrEmpty(username) || password is null || mode == OnvifWsSecurityMode.None)
        {
            return null;
        }

        var created = DateTime.UtcNow.AddMinutes(-2);
        var createdText = created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var nonceText = Convert.ToBase64String(RandomNumberGenerator.GetBytes(8));

        var passwordType = mode == OnvifWsSecurityMode.PasswordText
            ? PasswordTextType
            : PasswordDigestType;
        var passwordValue = mode == OnvifWsSecurityMode.PasswordText
            ? password ?? string.Empty
            : Convert.ToBase64String(SHA1.HashData(
                Encoding.UTF8.GetBytes($"{nonceText}{createdText}{password}")));

        return new XElement(
            Ws + "Security",
            new XElement(
                Ws + "UsernameToken",
                new XElement(Ws + "Username", username),
                new XElement(Ws + "Password", new XAttribute("Type", passwordType), passwordValue),
                new XElement(Ws + "Nonce", new XAttribute("EncodingType", Base64EncodingType), nonceText),
                new XElement(Wsu + "Created", createdText)));
    }

    private static readonly XNamespace Ws = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
}
