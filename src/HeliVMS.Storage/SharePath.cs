using System.IO;

namespace HeliVMS.Storage;

/// <summary>
/// 分享資源路徑邊界檢查（M51，§14.7 #4）：確保資源位於允許根目錄之下，阻擋 <c>..</c> 逃逸。
/// </summary>
public static class SharePath
{
    public static bool IsWithinRoot(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
