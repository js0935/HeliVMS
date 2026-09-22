using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>授權碼流程語意欄位（M100）：由 Begin 產生、Complete 消耗。</summary>
public sealed record OidcAuthorizationRequest(
    int ProviderId,
    string State,
    string Verifier,
    string CodeChallenge,
    string RedirectUri,
    DateTime ExpiresUtc);

/// <summary>token 端點交換回應（M100）。</summary>
public sealed record OidcTokenResponse(bool Ok, string? IdToken, string? AccessToken, string Error)
{
    public static OidcTokenResponse Success(string idToken, string? accessToken)
        => new(true, idToken, accessToken, string.Empty);

    public static OidcTokenResponse Fail(string error) => new(false, null, null, error);
}

/// <summary>OIDC 授權碼登入結果（M100，§14.7 #1）。</summary>
public sealed record OidcLoginResult(
    bool Succeeded,
    string? SessionId,
    string? RedirectUri,
    string Role,
    string? DisplayName,
    string Error)
{
    public static OidcLoginResult Redirect(string redirectUri)
        => new(false, null, redirectUri, "viewer", null, string.Empty);

    public static OidcLoginResult Success(string sessionId, string role, string? displayName)
        => new(true, sessionId, null, role, displayName, string.Empty);

    public static OidcLoginResult Fail(string error)
        => new(false, null, null, "viewer", null, error);
}

/// <summary>token 端點交換介面（M100）：測試注入 fake；正式為 <see cref="HttpTokenEndpointClient"/>。</summary>
public interface ITokenEndpointClient
{
    OidcTokenResponse Exchange(
        string tokenUri,
        string code,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier);
}

/// <summary>以 form-urlencoded POST /token 交換授權碼（M100；RFC 6749 §4.1.3）。</summary>
public sealed class HttpTokenEndpointClient : ITokenEndpointClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public OidcTokenResponse Exchange(
        string tokenUri,
        string code,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("client_id", clientId),
            new("code_verifier", codeVerifier),
        };
        if (!string.IsNullOrEmpty(clientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        }

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = Http.Send(new HttpRequestMessage(HttpMethod.Post, tokenUri) { Content = content });
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                return OidcTokenResponse.Fail($"token 端點錯誤：{err.GetString()}");
            }

            var idToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null;
            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            return string.IsNullOrWhiteSpace(idToken)
                ? OidcTokenResponse.Fail("token 端點未傳回 id_token")
                : OidcTokenResponse.Success(idToken!, accessToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return OidcTokenResponse.Fail($"token 端點連線失敗（{ex.Message}）");
        }
    }
}

/// <summary>
/// OIDC 授權碼＋PKCE 登入流程（M100，§14.7 #1 尾段）：<see cref="Begin"/> 產生授權 URL
/// （response_type=code、state、S256 code_challenge）並暫存待決 <see cref="OidcAuthorizationRequest"/>
/// （單次消費、10 分鐘內有效）；<see cref="Complete"/> 以 code 交換 /token、驗證 id_token
/// （沿用 <see cref="EnterpriseAuthService.AuthenticateOidc"/>）、對映本地 RBAC、同步 users 鏡像
/// （密碼為不可驗證記號，同 M99）並簽發登入 session。state 一次性與 PKCE code_verifier 防止
/// CSRF／授權碼挾持。
/// </summary>
public sealed class OidcLoginFlow
{
    /// <summary>待決授權請求有效期間（預設 10 分鐘）。</summary>
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    /// <summary>簽發 session 有效期間（預設 24 小時）。</summary>
    public static readonly TimeSpan SessionTtlDefault = TimeSpan.FromHours(24);

    private const string Scope = "openid profile email";

    private readonly AuthProviderRepository _providers;
    private readonly UserRepository _users;
    private readonly LoginSessionRepository _sessions;
    private readonly EnterpriseAuthService _auth;
    private readonly ITokenEndpointClient _tokenClient;
    private readonly ConcurrentDictionary<string, OidcAuthorizationRequest> _pending = new();

    public OidcLoginFlow(SqliteStore store, ITokenEndpointClient? tokenClient = null)
    {
        _providers = new AuthProviderRepository(store);
        _users = new UserRepository(store);
        _sessions = new LoginSessionRepository(store);
        _auth = new EnterpriseAuthService(store);
        _tokenClient = tokenClient ?? new HttpTokenEndpointClient();
    }

    /// <summary>產生授權 URL（Brower 導向 IdP）並暫存待決請求；回 <see cref="OidcLoginResult.Redirect"/>。</summary>
    public OidcLoginResult Begin(AuthProviderRecord provider, DateTime? utcNow = null)
    {
        if (!string.Equals(provider.Kind, AuthProviderRepository.KindOidc, StringComparison.Ordinal))
        {
            return OidcLoginResult.Fail("提供者種類不是 OIDC");
        }

        var options = OidcOptions.FromJson(provider.ConfigJson);
        if (string.IsNullOrWhiteSpace(options.AuthorizeUri) || string.IsNullOrWhiteSpace(options.RedirectUri))
        {
            return OidcLoginResult.Fail("提供者未設定授權/回呼端點");
        }

        var now = utcNow ?? DateTime.UtcNow;
        PurgeExpired(now);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        _pending[state] = new OidcAuthorizationRequest(
            provider.Id, state, verifier, challenge, options.RedirectUri!, now + PendingTtl);

        var clientIdPart = $"client_id={Uri.EscapeDataString(options.ClientId ?? string.Empty)}";
        var scopePart = $"scope={Uri.EscapeDataString(Scope)}";
        var uri = $"{options.AuthorizeUri}?response_type=code&{clientIdPart}&redirect_uri={Uri.EscapeDataString(options.RedirectUri!)}&{scopePart}&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";
        return OidcLoginResult.Redirect(uri);
    }

    /// <summary>以回呼 code＋state 完成登入：交換 token→驗證 id_token→鏡像→簽發 session。</summary>
    public OidcLoginResult Complete(string code, string state, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        PurgeExpired(now);

        if (!_pending.TryRemove(state, out var request) || request.ExpiresUtc <= now)
        {
            return OidcLoginResult.Fail("授權請求無效或已過期（state 已被使用或逾時）");
        }

        var provider = _providers.Get(request.ProviderId);
        if (provider is null)
        {
            return OidcLoginResult.Fail("授權提供者已不存在");
        }

        var options = OidcOptions.FromJson(provider.ConfigJson);
        var exchanged = _tokenClient.Exchange(
            options.TokenUri ?? string.Empty,
            code,
            request.RedirectUri,
            options.ClientId ?? string.Empty,
            options.ClientSecret,
            request.Verifier);
        if (!exchanged.Ok)
        {
            return OidcLoginResult.Fail(exchanged.Error);
        }

        var validated = _auth.AuthenticateOidc(provider, exchanged.IdToken!, now);
        if (!validated.Ok)
        {
            return OidcLoginResult.Fail($"ID 權杖驗證失敗：{validated.Error}");
        }

        var mirror = _users.GetByUsername(validated.Username);
        var userId = mirror?.Id ?? _users.CreateUser(
            validated.Username, LdapLoginBroker.NoLocalPasswordHash, validated.Role, validated.DisplayName);
        if (mirror is not null)
        {
            _users.SetRole(userId, validated.Role);
            _users.SetDisplayName(userId, validated.DisplayName ?? validated.Username);
        }

        _users.RecordLoginSuccess(userId, now);
        var sessionId = _sessions.Create(
            userId, validated.Username, validated.Role, AuthProviderRepository.KindOidc, SessionTtlDefault, now);
        return OidcLoginResult.Success(sessionId, validated.Role, validated.DisplayName ?? validated.Username);
    }

    private void PurgeExpired(DateTime now)
    {
        foreach (var (state, request) in _pending)
        {
            if (request.ExpiresUtc <= now)
            {
                _pending.TryRemove(state, out _);
            }
        }
    }
}