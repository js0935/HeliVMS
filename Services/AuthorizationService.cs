// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using HeliVMS.Models;

namespace HeliVMS.Services;

public class AuthorizationService : IAuthorizationService
{
    private readonly IAuthenticationService _auth;

    private static readonly Dictionary<UserRole, HashSet<Permission>> RolePermissions = new()
    {
        [UserRole.Viewer] = new()
        {
            Permission.LiveView,
            Permission.Playback,
        },
        [UserRole.Operator] = new()
        {
            Permission.LiveView,
            Permission.PTZControl,
        },
        [UserRole.Admin] = new()
        {
            Permission.LiveView,
            Permission.Playback,
            Permission.PTZControl,
            Permission.DeviceManagement,
            Permission.UserManagement,
            Permission.SystemSettings,
            Permission.RecordingManagement,
            Permission.OnvifScan,
            Permission.ImportExport,
            Permission.License,
        },
        [UserRole.Maintenance] = new()
        {
            Permission.SystemSettings,
            Permission.LiveView,
        },
    };

    public event Action<string>? AccessDenied;

    public AuthorizationService(IAuthenticationService auth)
    {
        _auth = auth;
    }

    public bool HasPermission(Permission permission)
    {
        return HasPermission(_auth.CurrentUser, permission);
    }

    public bool HasPermission(User? user, Permission permission)
    {
        if (user is null || !user.IsEnabled) { return false; }

        // 如果用戶有個別權限設定，以個別設定為準（精準控制）
        if (user.Permissions.Count > 0)
        {
            return user.Permissions.Contains(permission.ToString());
        }

        // 否則回退到角色基礎權限
        if (RolePermissions.TryGetValue(user.Role, out var rolePerms) && rolePerms.Contains(permission))
            return true;

        return false;
    }

    public HashSet<Permission> GetPermissions(User? user = null)
    {
        var target = user ?? _auth.CurrentUser;
        if (target is null || !target.IsEnabled)
        {
            return new HashSet<Permission>();
        }

        var result = RolePermissions.TryGetValue(target.Role, out var rolePerms)
            ? new HashSet<Permission>(rolePerms)
            : new HashSet<Permission>();

        // 合併個別權限覆蓋
        foreach (var p in target.Permissions)
        {
            if (Enum.TryParse<Permission>(p, out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    public UserRole[] GetRolesWithPermission(Permission permission)
    {
        var result = new List<UserRole>(RolePermissions.Count);
        foreach (var kv in RolePermissions)
        {
            if (kv.Value.Contains(permission))
            {
                result.Add(kv.Key);
            }
        }
        return result.ToArray();
    }

    public void RequirePermission(Permission permission)
    {
        RequirePermission(_auth.CurrentUser, permission);
    }

    public void RequirePermission(User? user, Permission permission)
    {
        if (!HasPermission(user, permission))
        {
            var msg = $"權限不足：{user?.Username ?? "anonymous"} 缺少 {permission} 權限";
            AccessDenied?.Invoke(msg);
            throw new UnauthorizedAccessException(msg);
        }
    }
}
