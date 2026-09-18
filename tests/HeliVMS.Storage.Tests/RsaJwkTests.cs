using System.Security.Cryptography;

namespace HeliVMS.Storage.Tests;

/// <summary>M50（§14.7 #1）：JWKS／Base64URL／角色對映測試。</summary>
public class RsaJwkTests
{
    [Fact]
    public void ToJwksJson_ThenParse_RoundTripsKey()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey("kid-1");

        var keys = RsaJwk.ParseJwks(jwks);

        Assert.True(keys.ContainsKey("kid-1"));
        Assert.Equal(rsa.ExportParameters(false).Modulus, keys["kid-1"].ExportParameters(false).Modulus);
    }

    [Fact]
    public void ParseJwks_MultipleKeys_IndexedByKid()
    {
        var (_, _, a) = OidcTestFactory.NewKey("a");
        var (_, _, b) = OidcTestFactory.NewKey("b");
        var combined = Combine(a, b);

        var keys = RsaJwk.ParseJwks(combined);

        Assert.Equal(2, keys.Count);
        Assert.True(keys.ContainsKey("a"));
        Assert.True(keys.ContainsKey("b"));
    }

    [Fact]
    public void ParseJwks_NonRsaKeysSkipped()
    {
        var keys = RsaJwk.ParseJwks("""{"keys":[{"kty":"EC","crv":"P-256","kid":"ec-1"}]}""");

        Assert.Empty(keys);
    }

    [Fact]
    public void ParseJwks_MissingKeysProperty_ReturnsEmpty()
    {
        Assert.Empty(RsaJwk.ParseJwks("""{"foo":"bar"}"""));
        Assert.Empty(RsaJwk.ParseJwks(string.Empty));
    }

    [Fact]
    public void Base64Url_RoundTripsAndIsPaddedFree()
    {
        var data = new byte[] { 0xfb, 0xff, 0x3e, 0x01, 0x02 };

        var encoded = Base64Url.Encode(data);

        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(data, Base64Url.Decode(encoded));
    }

    [Fact]
    public void Base64Url_InvalidLength_Throws()
    {
        Assert.Throws<FormatException>(() => Base64Url.Decode("abcde"));
    }

    private static string Combine(string a, string b)
    {
        static string KeyObject(string jwks)
        {
            var start = jwks.IndexOf('[') + 1;
            var end = jwks.LastIndexOf(']');
            return jwks[start..end];
        }

        return "{\"keys\":[" + KeyObject(a) + "," + KeyObject(b) + "]}";
    }
}

/// <summary>M50：IdP 群組 → 本地角色對映。</summary>
public class RoleMapperTests
{
    [Fact]
    public void IntersectingGroup_ReturnsAdmin()
    {
        Assert.Equal("admin", RoleMapper.Map(new[] { "vms-admins" }, new[] { "vms-admins" }, "viewer"));
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        Assert.Equal("admin", RoleMapper.Map(new[] { "VMS-ADMINS" }, new[] { "vms-admins" }, "viewer"));
    }

    [Fact]
    public void NoIntersection_ReturnsDefaultRole()
    {
        Assert.Equal("viewer", RoleMapper.Map(new[] { "guests" }, new[] { "vms-admins" }, "viewer"));
    }

    [Fact]
    public void NoAdminGroups_ReturnsDefaultRole()
    {
        Assert.Equal("viewer", RoleMapper.Map(new[] { "vms-admins" }, Array.Empty<string>(), "viewer"));
    }

    [Fact]
    public void DefaultRoleAdmin_IsHonored()
    {
        Assert.Equal("admin", RoleMapper.Map(Array.Empty<string>(), new[] { "vms-admins" }, "admin"));
    }

    [Fact]
    public void UnknownDefaultRole_NormalizesToViewer()
    {
        Assert.Equal("viewer", RoleMapper.Map(Array.Empty<string>(), new[] { "vms-admins" }, "superuser"));
    }
}
