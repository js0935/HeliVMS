// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

namespace HeliVMS.Models;

public class StorageLocation
{
    public string Path { get; set; } = "D:\\Recordings";
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    public long MaxSizeGB { get; set; }
}
