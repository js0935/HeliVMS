using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>
/// LDAP 綁定抽象（M50，§14.7 #1）：實際目錄連線（AD／OpenLDAP）由部署端實作；
/// 本服務僅協調設定、過濾器與角色對映，使核心邏輯無網路依賴即可測試。
/// </summary>
public interface ILdapBinder
{
    /// <summary>以使用者帳密綁定並回傳其群組（名稱或 DN）；驗證失敗回 <c>null</c>。</summary>
    IReadOnlyList<string>? Bind(LdapSettings settings, string username, string password);
}

/// <summary>
/// 企業身份驗證（M50，§14.7 #1）：OIDC ID Token 驗證與 LDAP 綁定，並對映本地 RBAC。
/// </summary>
public sealed class EnterpriseAuthService
{
    private readonly AuthProviderRepository _providers;

    public EnterpriseAuthService(SqliteStore store)
    {
        _providers = new AuthProviderRepository(store);
    }

    public IReadOnlyList<AuthProviderRecord> ListProviders() => _providers.List();

    public IReadOnlyList<AuthProviderRecord> ListEnabledOidc() => _providers.ListEnabled(AuthProviderRepository.KindOidc);

    public IReadOnlyList<AuthProviderRecord> ListEnabledLdap() => _providers.ListEnabled(AuthProviderRepository.KindLdap);

    /// <summary>
    /// 驗證 OIDC ID Token。JWKS 來源優先內嵌 <c>JwksJson</c>，否則遠端 <c>JwksUri</c>。
    /// </summary>
    public OidcResult AuthenticateOidc(AuthProviderRecord provider, string token, DateTime? utcNow = null)
    {
        if (!string.Equals(provider.Kind, AuthProviderRepository.KindOidc, StringComparison.Ordinal))
        {
            return OidcResult.Fail("提供者種類不是 OIDC");
        }

        var options = OidcOptions.FromJson(provider.ConfigJson);
        IReadOnlyDictionary<string, RSA> keys;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.JwksJson))
            {
                keys = RsaJwk.ParseJwks(options.JwksJson!);
            }
            else if (!string.IsNullOrWhiteSpace(options.JwksUri))
            {
                keys = JwksFetcher.Fetch(options.JwksUri!);
            }
            else
            {
                return OidcResult.Fail("提供者未設定 JWKS");
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or HttpRequestException)
        {
            return OidcResult.Fail($"JWKS 載入失敗（{ex.Message}）");
        }

        if (keys.Count == 0)
        {
            return OidcResult.Fail("提供者 JWKS 無可用金鑰");
        }

        return OidcValidator.Validate(token, options, keys, utcNow ?? DateTime.UtcNow);
    }

    /// <summary>以 LDAP 綁定驗證帳密並對映角色。</summary>
    public AuthResult AuthenticateLdap(
        AuthProviderRecord provider,
        string username,
        string password,
        ILdapBinder binder)
    {
        if (!string.Equals(provider.Kind, AuthProviderRepository.KindLdap, StringComparison.Ordinal))
        {
            return AuthResult.Fail("提供者種類不是 LDAP");
        }

        var settings = LdapSettings.FromJson(provider.ConfigJson);
        var groups = binder.Bind(settings, username, password);
        if (groups is null)
        {
            return AuthResult.Fail("LDAP 驗證失敗");
        }

        var role = RoleMapper.Map(groups, settings.AdminGroups, settings.DefaultRole);
        return AuthResult.Success(role, username);
    }
}

/// <summary>遠端 JWKS 取得（M50，§14.7 #1）。</summary>
public static class JwksFetcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static IReadOnlyDictionary<string, RSA> Fetch(string uri)
        => RsaJwk.ParseJwks(Http.GetStringAsync(uri).GetAwaiter().GetResult());
}
