using System.Security.Principal;
using HeliVMS.Shared;

namespace HeliVMS.App;

/// <summary>
/// 以 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 實作登入自啟。
///
/// 這是全專案唯一碰登錄檔的地方，而且是刻意壓在 WPF 專案裡：
/// <c>Microsoft.Win32.Registry</c> 只在 Windows target 上編譯得起來，
/// 而 <c>HeliVMS.Storage</c>／<c>HeliVMS.Shared</c> 是跨平台 TFM，
/// 把平台綁定塞進去會讓整個測試體系沒辦法在非 Windows 上跑。
/// 決策邏輯（要不要啟用、命令怎麼組）都在可測的 <see cref="AutoStartService"/>，
/// 這裡只剩「讀寫刪一個值」這種沒有判斷力的部分。
/// </summary>
public sealed class RegistryAutoStartStore : IAutoStartStore
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AutoStartService.RunValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(AutoStartService.RunValueName, command, Microsoft.Win32.RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(AutoStartService.RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 登錄檔寫入是否可能被拒絕。
    ///
    /// 群域或 kiosk 環境常把 HKCU\...\Run 鎖成唯讀，這時設定頁不該報錯給使用者看，
    /// 而是要說明「這台機器的政策不允許登入自啟」——使用者看到的是機器政策問題，
    /// 不是自己勾錯了。
    /// </summary>
    public static bool IsBlockedByPolicy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>是否以互動式帳號身分執行（服務帳號下自啟沒有意義）。</summary>
    public static bool IsInteractiveSession()
        => Environment.UserInteractive && !string.IsNullOrEmpty(WindowsIdentity.GetCurrent().Name);
}