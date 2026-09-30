using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>驗證多廠牌認證相容：WWW-Authenticate 挑戰解析與 Digest／Basic 計算、WS-Security 形式。</summary>
public class OnvifAuthTests
{
    private const string DigestHeader =
        "Digest realm=\"IPC-Camera\", nonce=\"dcd98b7102dd2f0e\", qop=\"auth\", algorithm=MD5, opaque=\"5ccc069c403ebaf9\", stale=FALSE";

    [Fact]
    public void TryParse_DigestChallenge_ReadsAllParameters()
    {
        var challenge = OnvifHttpChallenge.TryParse(DigestHeader);

        Assert.NotNull(challenge);
        Assert.True(challenge!.IsDigest);
        Assert.Equal("IPC-Camera", challenge.Realm);
        Assert.Equal("dcd98b7102dd2f0e", challenge.Nonce);
        Assert.Equal("5ccc069c403ebaf9", challenge.Opaque);
        Assert.Equal("auth", challenge.Qop);
        Assert.Equal("MD5", challenge.Algorithm);
    }

    [Fact]
    public void TryParse_ToleratesUnquotedValuesAndTrailingFlags()
    {
        var challenge = OnvifHttpChallenge.TryParse("Digest realm=Cam, nonce=abc123, qop=auth, stale");

        Assert.NotNull(challenge);
        Assert.Equal("Cam", challenge!.Realm);
        Assert.Equal("abc123", challenge.Nonce);
        Assert.Equal("auth", challenge.Qop);
    }

    [Fact]
    public void TryParse_QopListWithoutAuth_IsNotSupported()
    {
        var challenge = OnvifHttpChallenge.TryParse("Digest realm=\"R\", nonce=\"n\", qop=\"auth-int\"");

        Assert.NotNull(challenge);
        Assert.Equal(string.Empty, challenge!.Qop);
    }

    [Fact]
    public void TryParse_UnsupportedScheme_IsNull()
    {
        Assert.Null(OnvifHttpChallenge.TryParse("Negotiate"));
        Assert.Null(OnvifHttpChallenge.TryParse("Digest"));
        Assert.Null(OnvifHttpChallenge.TryParse(null));
    }

    [Fact]
    public void BuildAuthorization_Digest_ComputesRfc2617Response()
    {
        var challenge = OnvifHttpChallenge.TryParse(DigestHeader)!;

        var header = Parse(challenge.BuildAuthorization("POST", "/onvif/device_service", "admin", "secret"));

        Assert.Equal("Digest", header.Scheme);
        Assert.Equal("admin", header.Parameter["username"]);
        Assert.Equal("IPC-Camera", header.Parameter["realm"]);
        Assert.Equal("dcd98b7102dd2f0e", header.Parameter["nonce"]);
        Assert.Equal("/onvif/device_service", header.Parameter["uri"]);
        Assert.Equal("auth", header.Parameter["qop"]);
        Assert.Equal("5ccc069c403ebaf9", header.Parameter["opaque"]);

        var ha1 = Md5("admin:IPC-Camera:secret");
        var ha2 = Md5("POST:/onvif/device_service");
        var expected = Md5($"{ha1}:dcd98b7102dd2f0e:{header.Parameter["nc"]}:{header.Parameter["cnonce"]}:auth:{ha2}");
        Assert.Equal(expected, header.Parameter["response"]);
    }

    [Fact]
    public void BuildAuthorization_Digest_IncrementsNonceCount()
    {
        var challenge = OnvifHttpChallenge.TryParse(DigestHeader)!;

        var first = Parse(challenge.BuildAuthorization("POST", "/onvif/device_service", "admin", "secret"));
        var second = Parse(challenge.BuildAuthorization("POST", "/onvif/device_service", "admin", "secret"));

        Assert.Equal("00000001", first.Parameter["nc"]);
        Assert.Equal("00000002", second.Parameter["nc"]);
        Assert.NotEqual(first.Parameter["cnonce"], second.Parameter["cnonce"]);
    }

    [Fact]
    public void BuildAuthorization_WithoutQop_UsesLegacyResponse()
    {
        var challenge = OnvifHttpChallenge.TryParse("Digest realm=\"R\", nonce=\"n\"")!;

        var header = Parse(challenge.BuildAuthorization("POST", "/x", "u", "p"));

        var expected = Md5($"{Md5("u:R:p")}:n:{Md5("POST:/x")}");
        Assert.Equal(expected, header["response"]);
        Assert.Null(header["qop"]);
        Assert.Null(header["nc"]);
    }

    [Fact]
    public void BuildAuthorization_Sha256_UsesStrongerDigest()
    {
        var challenge = OnvifHttpChallenge.TryParse(
            "Digest realm=\"R\", nonce=\"n\", qop=\"auth\", algorithm=SHA-256")!;

        var header = Parse(challenge.BuildAuthorization("POST", "/x", "u", "p"));

        var ha1 = Sha256("u:R:p");
        var ha2 = Sha256("POST:/x");
        var expected = Sha256($"{ha1}:n:{header.Parameter["nc"]}:{header.Parameter["cnonce"]}:auth:{ha2}");
        Assert.Equal(expected, header.Parameter["response"]);
    }

    [Fact]
    public void BuildAuthorization_Md5Sess_IncludesCnonceInHa1()
    {
        var challenge = OnvifHttpChallenge.TryParse(
            "Digest realm=\"R\", nonce=\"n\", qop=\"auth\", algorithm=MD5-sess")!;

        var header = Parse(challenge.BuildAuthorization("POST", "/x", "u", "p"));

        var ha1 = Md5($"{Md5("u:R:p")}:n:{header.Parameter["cnonce"]}");
        var ha2 = Md5("POST:/x");
        var expected = Md5($"{ha1}:n:{header.Parameter["nc"]}:{header.Parameter["cnonce"]}:auth:{ha2}");
        Assert.Equal(expected, header.Parameter["response"]);
    }

    [Fact]
    public void BuildAuthorization_Basic_EncodesCredentials()
    {
        var challenge = OnvifHttpChallenge.TryParse("Basic realm=\"IPC\"")!;

        var value = challenge.BuildAuthorization("POST", "/x", "admin", "secret");

        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret"))}", value);
    }

    [Fact]
    public void TryCreate_PrefersDigestWhenMultipleChallenges()
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Basic realm=\"A\"");
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", DigestHeader);

        Assert.True(OnvifHttpChallenge.TryCreate(response, out var challenge));
        Assert.True(challenge!.IsDigest);
    }

    [Fact]
    public void WsSecurity_Digest_UsesPasswordDigestType()
    {
        var security = OnvifWsSecurity.Build("admin", "secret", OnvifWsSecurityMode.PasswordDigest)!;

        var document = XDocument.Parse(security.ToString());
        var password = document.Descendants().Single(e => e.Name.LocalName == "Password");
        Assert.Equal(OnvifWsSecurity.PasswordDigestType, password.Attribute("Type")?.Value);
        Assert.Equal(
            Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{document.Descendants().Single(e => e.Name.LocalName == "Nonce").Value}" +
                $"{document.Descendants().Single(e => e.Name.LocalName == "Created").Value}secret"))),
            password.Value);
    }

    [Fact]
    public void WsSecurity_Text_UsesPasswordTextType()
    {
        var security = OnvifWsSecurity.Build("admin", "secret", OnvifWsSecurityMode.PasswordText)!;

        var document = XDocument.Parse(security.ToString());
        var password = document.Descendants().Single(e => e.Name.LocalName == "Password");
        Assert.Equal(OnvifWsSecurity.PasswordTextType, password.Attribute("Type")?.Value);
        Assert.Equal("secret", password.Value);
    }

    [Fact]
    public void WsSecurity_NoCredentialsOrNoneMode_ReturnsNull()
    {
        Assert.Null(OnvifWsSecurity.Build(null, "secret", OnvifWsSecurityMode.PasswordDigest));
        Assert.Null(OnvifWsSecurity.Build("admin", null, OnvifWsSecurityMode.PasswordDigest));
        Assert.Null(OnvifWsSecurity.Build("admin", "secret", OnvifWsSecurityMode.None));
    }

    private static ParsedDigest Parse(string value)
    {
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var scheme = value[..space];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value[(space + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                parameters[part[..equals].Trim()] = part[(equals + 1)..].Trim().Trim('"');
            }
        }

        return new ParsedDigest(scheme, parameters);
    }

    private static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ParsedDigest(string Scheme, IReadOnlyDictionary<string, string> Parameter)
    {
        public string? this[string key] => Parameter.TryGetValue(key, out var value) ? value : null;
    }
}
