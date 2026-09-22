using System.Security.Cryptography;

namespace HeliVMS.Storage.Tests;

public class OidcLoginFlowTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private static DateTime T() => DateTime.UtcNow;

    private sealed class FakeTokenClient : ITokenEndpointClient
    {
        public string? IdToken { get; set; }
        public string? Error { get; set; }
        public string? LastCode { get; private set; }
        public string? LastVerifier { get; private set; }
        public string? LastUri { get; private set; }

        public OidcTokenResponse Exchange(
            string tokenUri,
            string code,
            string redirectUri,
            string clientId,
            string? clientSecret,
            string codeVerifier)
        {
            LastUri = tokenUri;
            LastCode = code;
            LastVerifier = codeVerifier;
            return Error is not null
                ? OidcTokenResponse.Fail(Error)
                : OidcTokenResponse.Success(IdToken!, "access-1");
        }
    }

    public OidcLoginFlowTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-oidclogin-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private (AuthProviderRecord Provider, FakeTokenClient Client) AddProvider(
        RSA rsa,
        string jwks,
        object? roles = null)
    {
        var options = OidcTestFactory.Options(jwks) with
        {
            ClientId = "helivms",
            AuthorizeUri = "https://idp.example.com/authorize",
            TokenUri = "https://idp.example.com/token",
            RedirectUri = "https://helivms.local/callback",
        };
        var id = new AuthProviderRepository(_store).Add("idp", AuthProviderRepository.KindOidc, options.ToJson());
        var client = new FakeTokenClient
        {
            IdToken = OidcTestFactory.Sign(rsa, "test-key", OidcTestFactory.Claims(roles: roles)),
        };
        return (new AuthProviderRepository(_store).Get(id)!, client);
    }

    [Fact]
    public void Begin_ProducesAuthorizeUrl_WithStateAndPkce()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, _) = AddProvider(rsa, jwks);
        var flow = new OidcLoginFlow(_store, new FakeTokenClient());

        var result = flow.Begin(provider, T());

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RedirectUri);
        var uri = result.RedirectUri!;
        Assert.Contains("response_type=code", uri);
        Assert.Contains("client_id=helivms", uri);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://helivms.local/callback"), uri);
        Assert.Contains("scope=" + Uri.EscapeDataString("openid profile email"), uri);
        Assert.Contains("code_challenge_method=S256", uri);
        Assert.Contains("code_challenge=", uri);
        Assert.Contains("state=", uri);
    }

    [Fact]
    public void Complete_ValidCode_IssuesSessionAndMirror()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());
        var state = ExtractState(begin.RedirectUri!);

        var result = flow.Complete("code-1", state, T());

        Assert.True(result.Succeeded);
        Assert.Equal("viewer", result.Role);
        Assert.Equal("Alice Wang", result.DisplayName);
        Assert.Equal("code-1", client.LastCode);
        Assert.Equal("https://idp.example.com/token", client.LastUri);
        Assert.NotEmpty(client.LastVerifier!);
        var sessions = new LoginSessionRepository(_store);
        Assert.True(sessions.IsActive(result.SessionId!, T()));
        var mirror = new UserRepository(_store).GetByUsername("alice")!;
        Assert.NotNull(mirror);
        Assert.Equal(LdapLoginBroker.NoLocalPasswordHash, mirror.PasswordHash);
    }

    [Fact]
    public void Complete_AdminRoles_Promotes()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks, roles: new[] { "vms-admins" });
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());

        var result = flow.Complete("code-2", ExtractState(begin.RedirectUri!), T());

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Role);
        var mirror = new UserRepository(_store).GetByUsername("alice")!;
        Assert.Equal("admin", mirror.Role);
    }

    [Fact]
    public void Complete_ReusesMirror_UpdatesRole()
    {
        var users = new UserRepository(_store);
        users.CreateUser("alice", LdapLoginBroker.NoLocalPasswordHash, "viewer", "Old Name");
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks, roles: new[] { "vms-admins" });
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());

        var result = flow.Complete("code-3", ExtractState(begin.RedirectUri!), T());

        Assert.True(result.Succeeded);
        var mirror = new UserRepository(_store).GetByUsername("alice")!;
        Assert.Equal("admin", mirror.Role);
        Assert.Equal("Alice Wang", mirror.DisplayName);
    }

    [Fact]
    public void Complete_WrongState_Rejected()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());
        _ = begin;

        var result = flow.Complete("code-x", "bogus-state", T());

        Assert.False(result.Succeeded);
        Assert.Contains("無效或已過期", result.Error);
        Assert.False(new LoginSessionRepository(_store).IsActive("whatever", T()));
    }

    [Fact]
    public void Complete_StateSingleUse_SecondCallRejected()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());
        var state = ExtractState(begin.RedirectUri!);

        Assert.True(flow.Complete("code-1", state, T()).Succeeded);
        var second = flow.Complete("code-1", state, T());

        Assert.False(second.Succeeded);
    }

    [Fact]
    public void Complete_ExpiredPending_Rejected()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());
        var state = ExtractState(begin.RedirectUri!);

        var result = flow.Complete("code-1", state, T().AddMinutes(11));

        Assert.False(result.Succeeded);
        Assert.Contains("無效或已過期", result.Error);
    }

    [Fact]
    public void Complete_TokenEndpointError_Fails()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        client.Error = "access_denied";
        client.IdToken = null;
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());

        var result = flow.Complete("code-1", ExtractState(begin.RedirectUri!), T());

        Assert.False(result.Succeeded);
        Assert.Contains("access_denied", result.Error);
    }

    [Fact]
    public void Complete_InvalidIdToken_Fails()
    {
        var (rsa, _, jwks) = OidcTestFactory.NewKey();
        var (provider, client) = AddProvider(rsa, jwks);
        client.IdToken = "not-a-jwt";
        var flow = new OidcLoginFlow(_store, client);
        var begin = flow.Begin(provider, T());

        var result = flow.Complete("code-1", ExtractState(begin.RedirectUri!), T());

        Assert.False(result.Succeeded);
        Assert.Contains("驗證失敗", result.Error);
    }

    private static string ExtractState(string authorizeUri)
    {
        var parsed = Uri.UnescapeDataString(authorizeUri);
        var idx = parsed.IndexOf("state=", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var state = parsed[(idx + "state=".Length)..];
        var amp = state.IndexOf('&');
        return amp >= 0 ? state[..amp] : state;
    }
}