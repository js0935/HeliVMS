namespace HeliVMS.Storage;

/// <summary>LDAP 使用者 DN 對映（M80，§14.7 #1 L0）：以既有 <see cref="LdapSettings"/>（M50 auth JSON）
/// 之 BaseDn 與預設 userPrincipalName/sAMAccountName 屬性產生使用者 DN，供 L1 bind 使用。</summary>
public static class LdapDn
{
    /// <summary>依 RFC 2253 對屬性值字元跳脫（逗號／等號／引號／加號／大小於號／分號／斜線、前導#、前導／後導空白）。</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var special = c is ',' or '=' or '"' or '+' or '<' or '>' or ';' or '\\' or '#'
                || (c == ' ' && (i == 0 || i == value.Length - 1));
            if (special)
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>產生使用者 DN：`sAMAccountName=Escape(username),BaseDn`（BaseDn 空白時僅回傳屬性等於值）。</summary>
    public static string BuildUserDn(LdapSettings settings, string username)
        => string.IsNullOrWhiteSpace(settings.BaseDn)
            ? $"sAMAccountName={Escape(username)}"
            : $"sAMAccountName={Escape(username)},{settings.BaseDn}";
}

/// <summary>LDAP 設定驗證（M80）：純規則、不回網路；回傳問題清單（空＝通過）。適用既有 M50 <see cref="LdapSettings"/>。</summary>
public static class LdapSettingsValidator
{
    public static IReadOnlyList<string> Validate(LdapSettings settings)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            problems.Add("LDAP 伺服器位址不可空白。");
        }

        if (settings.Port is < 1 or > 65535)
        {
            problems.Add("LDAP 埠須介於 1–65535。");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseDn))
        {
            problems.Add("基準 DN（BaseDn）不可空白。");
        }

        if (string.IsNullOrWhiteSpace(settings.UserFilter))
        {
            problems.Add("使用者搜尋過濾器不可空白。");
        }
        else if (!settings.UserFilter.Contains("{user}", StringComparison.Ordinal) &&
                 !settings.UserFilter.Contains("{0}", StringComparison.Ordinal))
        {
            problems.Add("使用者過濾器需含 {user} 或 {0} 佔位。");
        }

        return problems;
    }
}