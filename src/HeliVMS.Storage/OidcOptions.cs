using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeliVMS.Storage;

/// <summary>
/// OIDC 提供者設定（M50，§14.7 #1）。序列化後存於 <c>auth_providers.config_json</c>。
/// <c>JwksJson</c> 為內嵌 JWKS（離線可用）；<c>JwksUri</c> 為遠端 JWKS 端點（二擇一）。
/// </summary>
public sealed record OidcOptions
{
    public string Issuer { get; init; } = string.Empty;

    /// <summary>預期對象（aud）；留空時採用 <see cref="ClientId"/>。</summary>
    public string Audience { get; init; } = string.Empty;

    public string? ClientId { get; init; }

    /// <summary>授權碼流程（M100，§14.7 #1）：授權端點 URL。</summary>
    public string? AuthorizeUri { get; init; }

    /// <summary>授權碼流程（M100）：token 端點 URL。</summary>
    public string? TokenUri { get; init; }

    /// <summary>授權碼流程（M100）：回呼 URL（須與 IdP 註冊一致）。</summary>
    public string? RedirectUri { get; init; }

    /// <summary>授權碼流程（M100）：機密型用戶端之密鑰（選填；PKCE 下可省略）。</summary>
    public string? ClientSecret { get; init; }

    public string UsernameClaim { get; init; } = "preferred_username";

    public string RoleClaim { get; init; } = "roles";

    /// <summary>Admin 群組清單（與權杖群組交集即 admin，否則用 <see cref="DefaultRole"/>）。</summary>
    public string[] AdminGroups { get; init; } = Array.Empty<string>();

    public string DefaultRole { get; init; } = "viewer";

    public int ClockSkewSeconds { get; init; } = 60;

    public string? JwksJson { get; init; }

    public string? JwksUri { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static OidcOptions FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new OidcOptions()
            : JsonSerializer.Deserialize<OidcOptions>(json, JsonOptions) ?? new OidcOptions();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
