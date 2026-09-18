namespace HeliVMS.Storage.Tests;

/// <summary>M50（§14.7 #1）：企業身份驗證協調（OIDC token／LDAP 綁定）。</summary>
public class EnterpriseAuthServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuthProviderRepository _repo;
    private readonly EnterpriseAuthService _service;

    public EnterpriseAuthServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ent-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AuthProviderRepository(_store);
        _service = new EnterpriseAuthService(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void AuthenticateOidc_EmbeddedJwks_ReturnsRoleAndUser()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var id = _repo.Add("Corp IdP", "oidc", options.ToJson());
        var provider = _repo.Get(id)!;
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(roles: new[] { "vms-admins" }));

        var result = _service.AuthenticateOidc(provider, token);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("admin", result.Role);
        Assert.Equal("alice", result.Username);
    }

    [Fact]
    public void AuthenticateOidc_ExpiredToken_Fails()
    {
        var (rsa, kid, jwks) = OidcTestFactory.NewKey();
        var options = OidcTestFactory.Options(jwks);
        var id = _repo.Add("Corp IdP", "oidc", options.ToJson());
        var provider = _repo.Get(id)!;
        var token = OidcTestFactory.Sign(rsa, kid, OidcTestFactory.Claims(expOffsetSeconds: -600));

        var result = _service.AuthenticateOidc(provider, token);

        Assert.False(result.Ok);
    }

    [Fact]
    public void AuthenticateOidc_NoJwksConfigured_Fails()
    {
        var id = _repo.Add("Bare", "oidc", new OidcOptions { Issuer = "https://idp" }.ToJson());
        var provider = _repo.Get(id)!;

        var result = _service.AuthenticateOidc(provider, "a.b.c");

        Assert.False(result.Ok);
        Assert.Contains("JWKS", result.Error);
    }

    [Fact]
    public void AuthenticateOidc_WrongKind_Fails()
    {
        var id = _repo.Add("AD", "ldap", "{}");
        var provider = _repo.Get(id)!;

        var result = _service.AuthenticateOidc(provider, "a.b.c");

        Assert.False(result.Ok);
        Assert.Contains("OIDC", result.Error);
    }

    [Fact]
    public void AuthenticateLdap_BinderSuccess_MapsAdmin()
    {
        var id = _repo.Add("Corp AD", "ldap", new LdapSettings
        {
            Host = "dc.corp",
            AdminGroups = new[] { "VMS Admins" },
        }.ToJson());
        var provider = _repo.Get(id)!;

        var result = _service.AuthenticateLdap(provider, "alice", "secret", new FakeBinder(new[] { "VMS Admins" }));

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Role);
    }

    [Fact]
    public void AuthenticateLdap_BinderRejects_Fails()
    {
        var id = _repo.Add("Corp AD", "ldap", new LdapSettings { Host = "dc.corp" }.ToJson());
        var provider = _repo.Get(id)!;

        var result = _service.AuthenticateLdap(provider, "alice", "wrong", new FakeBinder(null));

        Assert.False(result.Succeeded);
        Assert.Contains("LDAP", result.Error);
    }

    [Fact]
    public void AuthenticateLdap_WrongKind_Fails()
    {
        var id = _repo.Add("IdP", "oidc", "{}");
        var provider = _repo.Get(id)!;

        var result = _service.AuthenticateLdap(provider, "alice", "pw", new FakeBinder(new[] { "x" }));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ListEnabledOidc_OnlyReturnsEnabledOidc()
    {
        var (_, _, jwks) = OidcTestFactory.NewKey();
        _repo.Add("On", "oidc", OidcTestFactory.Options(jwks).ToJson());
        _repo.Add("Off", "oidc", OidcTestFactory.Options(jwks).ToJson(), enabled: false);
        _repo.Add("Ad", "ldap", "{}");

        var providers = _service.ListEnabledOidc();

        Assert.Single(providers);
        Assert.Equal("On", providers[0].Name);
    }

    private sealed class FakeBinder : ILdapBinder
    {
        private readonly IReadOnlyList<string>? _groups;

        public FakeBinder(IReadOnlyList<string>? groups) => _groups = groups;

        public IReadOnlyList<string>? Bind(LdapSettings settings, string username, string password) => _groups;
    }
}
