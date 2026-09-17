namespace HeliVMS.App;

/// <summary>目前登入的使用者（M42，§18.6）。</summary>
public sealed record SessionUser(string Username, string Role, string? DisplayName);

/// <summary>
/// 登入狀態（M42，§18.6）。<c>auth.enabled=0</c> 時未登入，一律視為 admin（維持既有不受限行為）。
/// </summary>
public static class SessionContext
{
    public static SessionUser? CurrentUser { get; set; }

    /// <summary>是否已登入（auth.enabled=1 且登入成功）。</summary>
    public static bool IsSignedIn => CurrentUser is not null;

    /// <summary>是否具管理權限：未登入（auth 未啟用）或角色為 admin。</summary>
    public static bool IsAdmin => CurrentUser is null || CurrentUser.Role == "admin";
}