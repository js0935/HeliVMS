namespace HeliVMS.Storage;

/// <summary>
/// IdP 群組／角色對映本地 RBAC（M50，§14.7 #1）。
/// </summary>
public static class RoleMapper
{
    /// <summary>
    /// 權杖群組與 adminGroups 有交集→admin，否則採 defaultRole（僅認 admin，其餘視為 viewer）。
    /// </summary>
    public static string Map(
        IReadOnlyList<string> values,
        IReadOnlyList<string> adminGroups,
        string defaultRole)
    {
        var fallback = string.Equals(defaultRole, "admin", StringComparison.OrdinalIgnoreCase)
            ? "admin"
            : "viewer";

        if (values.Count == 0 || adminGroups.Count == 0)
        {
            return fallback;
        }

        foreach (var value in values)
        {
            foreach (var group in adminGroups)
            {
                if (string.Equals(group, value, StringComparison.OrdinalIgnoreCase))
                {
                    return "admin";
                }
            }
        }

        return fallback;
    }
}
