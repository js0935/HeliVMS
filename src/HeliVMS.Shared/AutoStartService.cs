namespace HeliVMS.Shared;

/// <summary>
/// 登入後自動啟動的儲存後端。抽出介面是為了讓
/// <see cref="AutoStartService"/> 能在不碰真實登錄檔的情況下被測——
/// 測試若真的寫 <c>HKCU\...\Run</c>，會污染執行測試的帳號，
/// 而且 CI 上沒有 GUI 登入環境，行為也不一樣。
/// </summary>
public interface IAutoStartStore
{
    /// <summary>讀取目前登錄的啟動命令；沒有設定時回傳 null。</summary>
    string? Read();

    /// <summary>寫入啟動命令（覆寫既有值）。</summary>
    void Write(string command);

    /// <summary>移除啟動命令；沒有設定時不動。</summary>
    void Delete();
}

/// <summary>
/// 開機自動啟動錄影監控。
///
/// 這是「長期監控錄影」與「開一個看監控的程式」差別最大的地方：錄影只存在於
/// <c>HeliVMS.App</c> 程序裡，沒有登入自啟的話，機器重開機之後就一路靜默到
/// 有人記得去點開程式為止。這期間沒有報警、沒有紀錄、沒有任何跡象。
///
/// 走 <c>HKCU\...\Run</c> 而不是工作排程或服務，是因為登入自啟不需要管理員權限、
/// 不需要安裝器，而且使用者可以在同一個設定頁把它關掉。
/// </summary>
public sealed class AutoStartService
{
    /// <summary>登錄檔值名稱。</summary>
    public const string RunValueName = "HeliVMS";

    /// <summary>登入後縮到系統匣的啟動參數。</summary>
    public const string TrayArgument = "--tray";

    private readonly IAutoStartStore _store;
    private readonly string _executablePath;

    public AutoStartService(IAutoStartStore store, string executablePath)
    {
        _store = store;
        _executablePath = executablePath;
    }

    /// <summary>是否已設定登入自啟。</summary>
    public bool IsEnabled() => !string.IsNullOrWhiteSpace(_store.Read());

    /// <summary>開啟或關閉登入自啟。</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _store.Write(BuildCommand(_executablePath));
        }
        else
        {
            _store.Delete();
        }
    }

    /// <summary>
    /// 組出登入後要執行的命令。
    ///
    /// 路徑一定要加引號：程式多半裝在 <c>C:\Program Files\HeliVMS\</c> 底下，
    /// 沒引號的話 Windows 會把它當成 <c>"C:\Program"</c> 然後開一個不存在的目錄，
    /// 使用者只會看到「開機自啟開了，但沒反應」。
    ///
    /// 固定帶 <see cref="TrayArgument"/>：登入時跳出的監控視窗等於宣告
    /// 「這台機器剛開機就有人在用」，而自啟的用途是無人值守錄影。
    /// </summary>
    public static string BuildCommand(string executablePath)
        => $"\"{executablePath}\" {TrayArgument}";

    /// <summary>
    /// 目前執行檔的路徑。
    ///
    /// 用 <see cref="Environment.ProcessPath"/> 而非
    /// <see cref="System.Reflection.Assembly.GetExecutingAssembly"/>：單檔發布
    /// （<c>PublishSingleFile</c>）時組件位置是空的，那樣會寫出一個指向
    /// 空字串的登錄項目——症狀是設定顯示「已啟用」，重開機什麼都沒發生。
    /// </summary>
    public static string CurrentExecutablePath()
        => Environment.ProcessPath ?? string.Empty;
}