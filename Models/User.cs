// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public enum UserRole {
    Viewer = 0,
    Operator = 1,
    Admin = 2,
    Maintenance = 3
}

public class User {
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public UserRole Role { get; set; } = UserRole.Operator;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("isAutoLogin")]
    public bool IsAutoLogin { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Individual permission overrides — granted in addition to RBAC role permissions</summary>
    [JsonPropertyName("twoFactorSecret")]
    public string? TwoFactorSecret { get; set; }

    [JsonPropertyName("isTwoFactorEnabled")]
    public bool IsTwoFactorEnabled { get; set; }

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonIgnore]
    public string RoleDisplay => Role switch {
        UserRole.Admin => "管理員",
        UserRole.Operator => "操作員",
        UserRole.Viewer => "檢視者",
        UserRole.Maintenance => "維護工程師",
        _ => "未知"
    };
}
