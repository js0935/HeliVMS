using System.Security.Cryptography;

namespace HeliVMS.Storage.Tests;

/// <summary>M50（§14.7 #1）：OIDC ID Token 驗證測試（自簽 RSA＋離線 JWKS）。</summary>
public class OidcValidatorTests
{
    [Fact]
    public void ValidToken_WithAdminGroup_ReturnsAdmin()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(roles: new[] { "vms-admins", "vms-ops" }));
        var keys = RsaJwk.ParseJwks(jwks);

        var result = OidcValidator.Validate(token, options, keys, DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("admin", result.Role);
        Assert.Equal("user-123", result.Subject);
        Assert.Equal("alice", result.Username);
        Assert.Equal("Alice Wang", result.DisplayName);
    }

    [Fact]
    public void ValidToken_WithoutAdminGroup_FallsBackToDefaultRole()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(roles: new[] { "vms-viewers" }));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("viewer", result.Role);
    }

    [Fact]
    public void RolesAsSpaceSeparatedString_IsMapped()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(roles: "vms-viewers vms-admins"));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("admin", result.Role);
    }

    [Fact]
    public void WrongSignature_Fails()
    {
        var (_, kid, jwks) = OidcTestFactory.NewKey();
        var (otherRsa, _, _) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(otherRsa, kid, OidcTestFactory.Claims());

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("簽章", result.Error);
    }

    [Fact]
    public void AlgNone_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(), alg: "none");

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("演算法", result.Error);
    }

    [Fact]
    public void ExpiredToken_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(expOffsetSeconds: -120));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("過期", result.Error);
    }

    [Fact]
    public void ClockSkew_AllowsSlightlyExpiredToken()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(expOffsetSeconds: -30));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void NotYetValidToken_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(nbfOffsetSeconds: 120));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("尚未生效", result.Error);
    }

    [Fact]
    public void IssuerMismatch_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(issuer: "https://evil.example.com"));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("iss", result.Error);
    }

    [Fact]
    public void AudienceMismatch_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(audience: "other-client"));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("aud", result.Error);
    }

    [Fact]
    public void AudienceArray_ContainingExpected_Succeeds()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(audience: new object?[] { "other", "helivms" }));

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void ClientIdUsedAsAudienceWhenAudienceEmpty()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks) with { Audience = string.Empty, ClientId = "helivms" };
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims());

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void UnknownKid_Fails()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey("known-key");
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, "missing-key", OidcTestFactory.Claims());

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("金鑰", result.Error);
    }

    [Fact]
    public void MissingKid_SingleKey_StillValidates()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var token = OidcTestFactory.Sign(rsa, string.Empty, OidcTestFactory.Claims());

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void MalformedToken_Fails()
    {
        var (_, _, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);

        var result = OidcValidator.Validate("not-a-jwt", options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.False(result.Ok);
        Assert.Contains("格式", result.Error);
    }

    [Fact]
    public void UsernameFallsBackToSubjectWhenClaimMissing()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var claims = OidcTestFactory.Claims();
        claims.Remove("preferred_username");
        var token = OidcTestFactory.Sign(rsa, kid, claims);

        var result = OidcValidator.Validate(token, options, RsaJwk.ParseJwks(jwks), DateTime.UtcNow);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("user-123", result.Username);
    }
}
