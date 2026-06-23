// ============================================================
// HeliVMS - 智慧監控管理系統
// 禾秝軟體開發團隊
// 代碼設計：洪俊士
// 版本：V1.0.0
// ============================================================

using HeliVMS.Models;

namespace HeliVMS.Services;

/// <summary>功能權限定義</summary>
public enum Permission {
    /// <summary>即時影像觀看</summary>
    LiveView,
    /// <summary>PTZ 雲台控制</summary>
    PTZControl,
    /// <summary>設備管理（新增/編輯/刪除攝影機）</summary>
    DeviceManagement,
    /// <summary>用戶管理（新增/編輯/刪除用戶）</summary>
    UserManagement,
    /// <summary>系統設定</summary>
    SystemSettings,
    /// <summary>錄影管理</summary>
    RecordingManagement,
    /// <summary>回放觀看</summary>
    Playback,
    /// <summary>ONVIF 掃描</summary>
    OnvifScan,
    /// <summary>匯入/匯出設定</summary>
    ImportExport,
    /// <summary>授權管理</summary>
    License,
}

/// <summary>授權管理服務 — 角色權限檢查</summary>
public interface IAuthorizationService {
    /// <summary>檢查目前用戶是否擁有指定權限</summary>
    bool HasPermission(Permission permission);

    /// <summary>檢查指定用戶是否擁有指定權限</summary>
    bool HasPermission(User? user, Permission permission);

    /// <summary>取得目前用戶的所有權限</summary>
    HashSet<Permission> GetPermissions(User? user = null);

    /// <summary>取得擁有指定權限的角色列表</summary>
    UserRole[] GetRolesWithPermission(Permission permission);

    /// <summary>要求指定權限，若無則擲回 UnauthorizedAccessException</summary>
    void RequirePermission(Permission permission);

    /// <summary>要求指定用戶擁有指定權限，若無則擲回 UnauthorizedAccessException</summary>
    void RequirePermission(User? user, Permission permission);

    /// <summary>權限存取被拒時觸發（傳遞訊息字串）</summary>
    event Action<string>? AccessDenied;
}
