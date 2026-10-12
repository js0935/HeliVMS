namespace HeliVMS.Storage;

/// <summary>
/// 資料根目錄與其下各種路徑的單一解析來源（§2.1）。
///
/// 桌面程式（<c>HeliVMS.App</c>）與 Web 服務（<c>HeliVMS.WebApi</c>）必須指向
/// <b>同一個</b>資料根目錄與同一個資料庫，這是「長期監控錄影」能成立的最低條件：
/// 排程要由桌面的排程器執行，Web 寫的排程得被它看到；兩邊的保留清理要看到同一份
/// 區段索引，否則各自刪檔而索引互不同步，磁碟上的實況與資料庫對不上，回放會出現
/// 「索引有、檔案沒有」的洞。
///
/// 這些常數與解析規則以前在兩個專案各寫一份，那種重複遲早會漂移：
/// 改了一邊、另一邊還在用舊值，而且沒有任何測試會紅。現在只有這裡能改。
/// </summary>
public static class DataRootLayout
{
    /// <summary>預設資料根目錄。</summary>
    public const string DefaultRoot = @"C:\HeliVMSData";

    /// <summary>覆寫資料根目錄的環境變數／組態鍵。</summary>
    public const string DataRootVariable = "HELIVMS_DATA";

    /// <summary>資料庫檔名。</summary>
    public const string DatabaseFileName = "index.db";

    /// <summary>錄影目錄名。</summary>
    public const string RecordingsDirName = "recordings";

    /// <summary>快照目錄名。</summary>
    public const string SnapshotsDirName = "snapshots";

    /// <summary>
    /// 解析資料根目錄：先看 <see cref="DataRootVariable"/>，再用
    /// <see cref="DefaultRoot"/>；預設位置不可寫時降級到本機應用程式資料夾。
    /// </summary>
    /// <param name="readVariable">
    /// 讀取變數／組態的委派。測試注入假的讀取器，不必真的設環境變數——
    /// 設了又 unset 會讓測試互相干擾，而且失敗時很難看出是誰留下的。
    /// </param>
    public static string ResolveRoot(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        var configured = read(DataRootVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        try
        {
            Directory.CreateDirectory(DefaultRoot);
            return DefaultRoot;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeliVMS");
        }
    }

    /// <summary>該根目錄下的主資料庫路徑。</summary>
    public static string Database(string dataRoot) => Path.Combine(dataRoot, DatabaseFileName);

    /// <summary>該根目錄下的錄影目錄。</summary>
    public static string Recordings(string dataRoot) => Path.Combine(dataRoot, RecordingsDirName);

    /// <summary>該根目錄下的快照目錄。</summary>
    public static string Snapshots(string dataRoot) => Path.Combine(dataRoot, SnapshotsDirName);

    /// <summary>直接指定資料庫路徑的組態鍵，優先於一切推導。</summary>
    public const string DatabaseVariable = "HELIVMS_DB";

    /// <summary>
    /// 解析主資料庫路徑：先看 <see cref="DatabaseVariable"/>，否則用資料根目錄下的
    /// <see cref="DatabaseFileName"/>。
    ///
    /// 放在這裡而不是 WebApi 的啟動程式碼，是為了讓它可測：桌面與 Web 服務指向不同
    /// 資料庫這種失效不會拋例外、不會記錄錯誤，只會讓「設定存進去了但機器沒在錄」，
    /// 屬於沒有測試就不會有人發現的那一类。
    /// </summary>
    public static string ResolveDatabasePath(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var explicitPath = read(DatabaseVariable);
        return string.IsNullOrWhiteSpace(explicitPath)
            ? Database(ResolveRoot(read))
            : explicitPath;
    }
}