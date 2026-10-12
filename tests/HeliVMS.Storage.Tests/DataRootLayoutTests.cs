using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 資料根目錄與路徑解析。
///
/// 這組測試守的是「桌面程式與 Web 服務必須看到同一套資料」。兩邊分開時不會有任何
/// 錯誤訊息：Web 寫的排程存進了它自己的資料庫，桌面排程器讀的是另一個，
/// 設定頁顯示儲存成功，機器上一台都沒錄；兩邊的保留清理也各拿一份區段索引，
/// 卻對同一個 recordings 目錄下手。這種失效只有在路徑解析本身被釘住時才擋得住。
/// </summary>
public sealed class DataRootLayoutTests
{
    /// <summary>設定了資料根目錄時，資料庫與錄影目錄都要落在它底下。</summary>
    [Fact]
    public void 設定資料根目錄時資料庫與錄影都在其下()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-root-{Guid.NewGuid():N}");

        var db = DataRootLayout.ResolveDatabasePath(name => name == DataRootLayout.DataRootVariable ? root : null);

        Assert.Equal(Path.Combine(root, "index.db"), db);
        Assert.Equal(Path.Combine(root, "recordings"), DataRootLayout.Recordings(root));
        Assert.Equal(Path.Combine(root, "snapshots"), DataRootLayout.Snapshots(root));
    }

    /// <summary>
    /// 明確指定資料庫時要優先於資料根目錄。
    ///
    /// 測試與進階部署靠這個鍵把資料庫指到別處；它要是被資料根目錄蓋掉，
    /// 測試會安靜地寫到真正的資料庫去。
    /// </summary>
    [Fact]
    public void 明確指定資料庫時優先於資料根目錄()
    {
        var db = Path.Combine(Path.GetTempPath(), $"custom-{Guid.NewGuid():N}.db");

        var resolved = DataRootLayout.ResolveDatabasePath(name => name switch
        {
            DataRootLayout.DatabaseVariable => db,
            DataRootLayout.DataRootVariable => Path.Combine(Path.GetTempPath(), "ignored"),
            _ => null,
        });

        Assert.Equal(db, resolved);
    }

    /// <summary>空白字串視為沒設定，不能讓路徑變成空白。</summary>
    [Fact]
    public void 空白設定視為沒設定()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-root-{Guid.NewGuid():N}");

        var db = DataRootLayout.ResolveDatabasePath(name => name switch
        {
            DataRootLayout.DatabaseVariable => "   ",
            DataRootLayout.DataRootVariable => root,
            _ => null,
        });

        Assert.Equal(Path.Combine(root, "index.db"), db);
    }

    /// <summary>
    /// 未設定時要落在預設根目錄，而不是暫存目錄。
    ///
    /// 這是修掉過的實際問題：Web 服務原本落到 %TEMP%\helivms-webapi.db，
    /// 與桌面的 C:\HeliVMSData\index.db 分道揚鑣。暫存目錄還會被系統清理工具刪掉，
    /// 使用者設定的排程會無聲消失。
    /// </summary>
    [Fact]
    public void 未設定時資料庫落在預設根目錄而非暫存目錄()
    {
        var db = DataRootLayout.ResolveDatabasePath(_ => null);

        Assert.Equal(Path.Combine(DataRootLayout.DefaultRoot, "index.db"), db);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), db, StringComparison.OrdinalIgnoreCase);
    }
}