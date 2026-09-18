using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeliVMS.Storage;

/// <summary>
/// LDAP 搜尋過濾器組裝（M50，§14.7 #1）：RFC 4515 escape 與樣板代入。
/// </summary>
public static class LdapFilter
{
    /// <summary>RFC 4515 escape：<c>\</c> <c>*</c> <c>(</c> <c>)</c> <c>NUL</c>。</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => ch.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>
    /// 以 <paramref name="username"/> 代入樣板的 <c>{user}</c>／<c>{0}</c> 佔位（先 escape）。
    /// 樣板不含佔位時丟 <see cref="ArgumentException"/>。
    /// </summary>
    public static string Build(string template, string username)
    {
        if (string.IsNullOrEmpty(template))
        {
            throw new ArgumentException("過濾器樣板不可為空", nameof(template));
        }

        if (!template.Contains("{user}", StringComparison.Ordinal) &&
            !template.Contains("{0}", StringComparison.Ordinal))
        {
            throw new ArgumentException("過濾器樣板需含 {user} 或 {0} 佔位", nameof(template));
        }

        var safe = Escape(username);
        return template
            .Replace("{user}", safe, StringComparison.Ordinal)
            .Replace("{0}", safe, StringComparison.Ordinal);
    }
}

/// <summary>LDAP 提供者設定（M50）：存於 <c>auth_providers.config_json</c>。</summary>
public sealed record LdapSettings
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 389;

    public string BaseDn { get; init; } = string.Empty;

    public string? BindDn { get; init; }

    public string? BindPassword { get; init; }

    public string UserFilter { get; init; } = "(&(objectClass=user)(sAMAccountName={user}))";

    public string[] AdminGroups { get; init; } = Array.Empty<string>();

    public string DefaultRole { get; init; } = "viewer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LdapSettings FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new LdapSettings()
            : JsonSerializer.Deserialize<LdapSettings>(json, JsonOptions) ?? new LdapSettings();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
