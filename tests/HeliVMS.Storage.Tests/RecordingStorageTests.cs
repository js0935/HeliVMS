using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 錄影根目錄必須避開系統碟（通常是 C:）。
///
/// 這條規則守的是一個沒有錯誤訊息的失效：系統碟被錄影填滿之後，Windows 本身、
/// 事件記錄與所有暫存檔一起失效，症狀是「錄到一半整台當掉」，而日誌乾淨。
/// 因此挑碟的邏輯做成可注入的純函式，直接釘住各種磁碟組態下的結果。
/// </summary>
public sealed class RecordingStorageTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static RecordingStorage.Drive Drive(string root, long totalGb, bool isSystem = false)
        => new(root, IsReady: true, IsSystem: isSystem, TotalBytes: totalGb * GB);

    /// <summary>資料根目錄在系統碟時，錄影要改存非系統固定磁碟的專用目錄。</summary>
    [Fact]
    public void 資料根目錄在系統碟時錄影改存非系統碟的專用目錄()
    {
        var root = RecordingStorage.ResolveRoot(
            @"C:\HeliVMSData",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"D:\HeliVMSRecordings", root);
    }

    /// <summary>資料根目錄本來就在非系統碟時，沿用其下的 recordings，不必另開目錄。</summary>
    [Fact]
    public void 資料根目錄不在系統碟時沿用其下的錄影目錄()
    {
        var root = RecordingStorage.ResolveRoot(
            @"D:\HeliVMSData",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"D:\HeliVMSData\recordings", root);
    }

    /// <summary>有多個非系統磁碟時選擇容量最大者，讓錄影有最多空間可用。</summary>
    [Fact]
    public void 多個非系統碟時選擇容量最大的()
    {
        var root = RecordingStorage.ResolveRoot(
            @"C:\HeliVMSData",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 100), Drive(@"E:\", 900)],
            systemDrive: "C:");

        Assert.Equal(@"E:\HeliVMSRecordings", root);
    }

    /// <summary>容量相同時取磁碟代號較小者，確保桌面、Agent、WebApi 三個程序算出同一個根目錄。</summary>
    [Fact]
    public void 容量相同時選擇磁碟代號較小者()
    {
        var root = RecordingStorage.ResolveRoot(
            @"C:\HeliVMSData",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"F:\", 500), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"D:\HeliVMSRecordings", root);
    }

    /// <summary>沒有其他固定磁碟時退回原本路徑——寧可錄在系統碟，也不要完全不錄。</summary>
    [Fact]
    public void 沒有其他固定磁碟時退回原本路徑()
    {
        var root = RecordingStorage.ResolveRoot(
            @"C:\HeliVMSData",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true)],
            systemDrive: "C:");

        Assert.Equal(@"C:\HeliVMSData\recordings", root);
    }

    /// <summary>明確指定的錄影根目錄要優先於資料根目錄推導，即使位於非系統碟也照用。</summary>
    [Fact]
    public void 明確指定的錄影根目錄在非系統碟時優先採用()
    {
        var root = RecordingStorage.ResolveRoot(
            @"C:\HeliVMSData",
            explicitRoot: @"E:\CustomRecordings",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"E:\CustomRecordings", root);
    }

    /// <summary>
    /// 明確指定的錄影根目錄一律照用，即使位於系統碟——那是操作員的刻意覆寫。
    ///
    /// 這也讓遠端串流測試（測資放在系統碟的 %TEMP%）保持確定性：不會因為
    /// 開發機多一顆非系統碟，就把測試的錄影根目錄悄悄改到別的地方。
    /// </summary>
    [Fact]
    public void 明確指定的錄影根目錄一律照用即使位於系統碟()
    {
        var root = RecordingStorage.ResolveRoot(
            @"D:\HeliVMSData",
            explicitRoot: @"C:\Recordings",
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"C:\Recordings", root);
    }

    /// <summary>同一組磁碟輸入要得到同一個結果，避免不同程序各自挑到不同磁碟。</summary>
    [Fact]
    public void 相同磁碟組態得到相同結果()
    {
        Func<IReadOnlyList<RecordingStorage.Drive>> drives =
            () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 400), Drive(@"E:\", 400)];

        var first = RecordingStorage.ResolveRoot(@"C:\HeliVMSData", listDrives: drives, systemDrive: "C:");
        var second = RecordingStorage.ResolveRoot(@"C:\HeliVMSData", listDrives: drives, systemDrive: "C:");

        Assert.Equal(first, second);
        Assert.Equal(@"D:\HeliVMSRecordings", first);
    }

    /// <summary>
    /// 由環境解析時要讀 <c>HELIVMS_DATA</c> 當資料根目錄，並套用避開系統碟規則。
    ///
    /// 這條守的是 Agent 的實際失效：Agent 曾直接拿資料根目錄當錄影根目錄，錄到
    /// <c>&lt;資料根&gt;\chNNN</c>，桌面與回放卻在 <c>&lt;資料根&gt;\recordings\chNNN</c> 找，
    /// 頻道永遠顯示無錄影。任何一端沒走這個入口就會重現。
    /// </summary>
    [Fact]
    public void 由環境解析會讀資料根環境變數並避開系統碟()
    {
        var root = RecordingStorage.ResolveRootFromEnvironment(
            name => name == DataRootLayout.DataRootVariable ? @"C:\HeliVMSData" : null,
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"D:\HeliVMSRecordings", root);
    }

    /// <summary>由環境解析時，明確的錄影根目錄環境變數優先於資料根目錄推導。</summary>
    [Fact]
    public void 由環境解析時明確錄影根目錄優先()
    {
        var root = RecordingStorage.ResolveRootFromEnvironment(
            name => name switch
            {
                RecordingStorage.RootVariable => @"E:\CustomRecordings",
                DataRootLayout.DataRootVariable => @"C:\HeliVMSData",
                _ => null,
            },
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"E:\CustomRecordings", root);
    }

    /// <summary>由環境解析時，資料根目錄不在系統碟就沿用其下的 recordings。</summary>
    [Fact]
    public void 由環境解析時非系統碟資料根目錄沿用其下錄影目錄()
    {
        var root = RecordingStorage.ResolveRootFromEnvironment(
            name => name == DataRootLayout.DataRootVariable ? @"D:\HeliVMSData" : null,
            listDrives: () => [Drive(@"C:\", 500, isSystem: true), Drive(@"D:\", 500)],
            systemDrive: "C:");

        Assert.Equal(@"D:\HeliVMSData\recordings", root);
    }

    /// <summary>
    /// 磁碟清單要讓主要碟排在首位，且把磁碟代號換成實際錄影目錄。
    ///
    /// 順序即優先序：寫入端每段都從清單頭開始挑第一個還有空間的碟，主要碟必須先於其他碟，
    /// 否則「主要碟」就只是個沒有作用的標記，操作員會發現錄影一開始就跑在次要碟上。
    /// </summary>
    [Fact]
    public void 磁碟清單會把主要碟排到最前面()
    {
        var roots = RecordingStorage.DiskRoots(
        [
            new RecordingStorage.RecordingDiskDto { Root = @"D:\", Primary = false },
            new RecordingStorage.RecordingDiskDto { Root = @"E:\", Primary = true },
            new RecordingStorage.RecordingDiskDto { Root = @"F:\", Primary = false },
        ]);

        Assert.Equal(
            [@"E:\HeliVMSRecordings", @"D:\HeliVMSRecordings", @"F:\HeliVMSRecordings"],
            roots);
    }

    /// <summary>
    /// 磁碟代號有沒有尾部分隔符都必須正規化成同一個代號。
    ///
    /// 設定值裡兩種寫法都存在過：設定中心下拉選單存裸代號（<c>D:</c>），而由錄影根目錄
    /// 退回的舊路徑存 <c>Path.GetPathRoot</c> 的原樣（<c>D:\</c>）。兩者直接比字串會
    /// 對不到碟，設定中心的空間欄位整排顯示「無法讀取」。
    /// </summary>
    [Fact]
    public void 磁碟代號有無尾部分隔符都正規化成同一個代號()
    {
        Assert.Equal("D:", RecordingStorage.DriveRootOf(@"D:\"));
        Assert.Equal("D:", RecordingStorage.DriveRootOf("D:"));
        Assert.Equal(
            RecordingStorage.DriveRootOf(@"D:\"),
            RecordingStorage.DriveRootOf("D:"));
    }

    /// <summary>
    /// 裸磁碟代號要組出絕對錄影目錄。
    ///
    /// <c>D:x</c> 是「該磁碟目前目錄」的相對路徑（<c>GetFullPath</c> 會照相對路徑解析，
    /// 每個程序依啟動目錄解出不同位置）。錄影目錄必須是絕對路徑，不論設定存的是
    /// 裸代號還是帶分隔符的寫法。
    /// </summary>
    [Fact]
    public void 裸磁碟代號補上根分隔符後才組出絕對錄影目錄()
    {
        Assert.Equal(@"D:\HeliVMSRecordings", RecordingStorage.FolderForDrive("D:"));
        Assert.Equal(@"D:\HeliVMSRecordings", RecordingStorage.FolderForDrive(@"D:\"));
        Assert.Equal(@"E:\Data\HeliVMSRecordings", RecordingStorage.FolderForDrive(@"E:\Data"));
    }

    /// <summary>磁碟清單混用兩種寫法時，推出的錄影目錄仍是絕對路徑——寫入端不看設定格式。</summary>
    [Fact]
    public void 磁碟清單混用兩種寫法時仍得到絕對錄影目錄()
    {
        var roots = RecordingStorage.DiskRoots(
        [
            new RecordingStorage.RecordingDiskDto { Root = "D:", Primary = true },
            new RecordingStorage.RecordingDiskDto { Root = @"E:\", Primary = false },
        ]);

        Assert.Equal([@"D:\HeliVMSRecordings", @"E:\HeliVMSRecordings"], roots);
    }

    /// <summary>挑碟取第一個達到可用空間門檻的碟：主要碟還有空間就先用它，不會無故溢流。</summary>
    [Fact]
    public void 主要碟還有空間時不會溢流到下一顆()
    {
        var free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:"] = 5000,
            [@"E:"] = 9000,
        };

        Assert.Equal(@"D:", RecordingStorage.ChooseRoot([@"D:", @"E:"], minFreeBytes: 1000, root => free[root]));
    }

    /// <summary>主要碟低於門檻時，改挑下一顆還有足夠空間的碟——這是「滿了自動續錄」的核心。</summary>
    [Fact]
    public void 主要碟低於門檻時挑下一顆還有空間的碟()
    {
        var free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:"] = 100,
            [@"E:"] = 5000,
            [@"F:"] = 9000,
        };

        Assert.Equal(@"E:", RecordingStorage.ChooseRoot([@"D:", @"E:", @"F:"], minFreeBytes: 1000, root => free[root]));
    }

    /// <summary>
    /// 全部磁碟都低於門檻時，退回可用空間最大的那一顆，而不是停止錄影。
    ///
    /// 停錄是無聲失效：畫面照常、日誌乾淨，操作員要等到想回放才發現好幾天沒錄。寧可寫滿
    /// 剩下最多的那顆，也要讓錄影繼續並讓畫面顯示空間不足。
    /// </summary>
    [Fact]
    public void 全部磁碟都低於門檻時取空間最大者()
    {
        var free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:"] = 100,
            [@"E:"] = 900,
            [@"F:"] = 400,
        };

        Assert.Equal(@"E:", RecordingStorage.ChooseRoot([@"D:", @"E:", @"F:"], minFreeBytes: 1000, root => free[root]));
    }

    /// <summary>可用空間剛好等於門檻時仍可寫入（門檻是「≥」，否則邊界值會反覆換碟）。</summary>
    [Fact]
    public void 可用空間剛好等於門檻時仍選該碟()
    {
        Assert.Equal(@"D:", RecordingStorage.ChooseRoot([@"D:"], minFreeBytes: 1000, _ => 1000));
    }

    /// <summary>沒有可挑的磁碟時回空值，由呼叫端退回單一根本目錄。</summary>
    [Fact]
    public void 沒有可挑的磁碟時回空值()
    {
        Assert.Null(RecordingStorage.ChooseRoot([], minFreeBytes: 1000, _ => 0));
    }

    /// <summary>最小可用空間設定的解析：合法值照用，未設定或壞值退回預設。</summary>
    [Fact]
    public void 最小可用空間未設定或壞值時退回預設()
    {
        Assert.Equal(RecordingStorage.DefaultMinFreeBytes, RecordingStorage.ParseMinFreeBytes(null));
        Assert.Equal(RecordingStorage.DefaultMinFreeBytes, RecordingStorage.ParseMinFreeBytes("not-a-number"));
        Assert.Equal(123L, RecordingStorage.ParseMinFreeBytes("123"));
    }

    /// <summary>
    /// 讀取端要看到「所有」磁碟（磁碟清單＋退回根，去重），寫入端只看優先序。
    ///
    /// 若讀取端只認單一根目錄，次要碟上的片段會被配額清理漏掉、遠端回放也會被判成超範圍，
    /// 症狀是「明明有錄，回放清單卻空一半」。
    /// </summary>
    [Fact]
    public void 讀取端根目錄涵蓋所有設定磁碟與退回根()
    {
        WithTempStore(store =>
        {
            new SettingsRepository(store).Set(
                RecordingStorage.DisksSettingKey,
                RecordingStorage.SerializeDisks(
                [
                    new RecordingStorage.RecordingDiskDto { Root = @"D:\", Primary = true },
                    new RecordingStorage.RecordingDiskDto { Root = @"E:\", Primary = false },
                ]));

            var roots = RecordingStorage.ConfiguredRoots(store, @"C:\fallback\recordings");

            Assert.Equal(
                [@"D:\HeliVMSRecordings", @"E:\HeliVMSRecordings", @"C:\fallback\recordings"],
                roots);
        });
    }

    /// <summary>選擇器在主要碟還有空間時挑主要碟，主要碟滿了就改挑下一顆——設定讀取即時生效。</summary>
    [Fact]
    public void 選擇器在主要碟寫滿後改挑下一顆()
    {
        WithTempStore(store =>
        {
            new SettingsRepository(store).Set(
                RecordingStorage.DisksSettingKey,
                RecordingStorage.SerializeDisks(
                [
                    new RecordingStorage.RecordingDiskDto { Root = @"D:\", Primary = true },
                    new RecordingStorage.RecordingDiskDto { Root = @"E:\", Primary = false },
                ]));

            const long enough = 5L * 1024 * 1024 * 1024;
            var free = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [@"D:\HeliVMSRecordings"] = enough,
                [@"E:\HeliVMSRecordings"] = enough,
            };
            var selector = RecordingStorage.CreateRootSelector(store, @"C:\fallback", root => free[root]);

            Assert.Equal(@"D:\HeliVMSRecordings", selector());

            // 主要碟掉到預設門檻（2 GiB）以下之後，下一個區段就該換到次要碟。
            free[@"D:\HeliVMSRecordings"] = 1L * 1024 * 1024 * 1024;
            Assert.Equal(@"E:\HeliVMSRecordings", selector());
        });
    }

    /// <summary>尚未設定磁碟清單時選擇器退回單一根目錄，維持既有單碟行為。</summary>
    [Fact]
    public void 未設定磁碟清單時選擇器退回根目錄()
    {
        WithTempStore(store =>
        {
            var selector = RecordingStorage.CreateRootSelector(store, @"C:\HeliVMSData\recordings", _ => 0);
            Assert.Equal(@"C:\HeliVMSData\recordings", selector());
        });
    }

    private static void WithTempStore(Action<SqliteStore> body)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"helivms-disks-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteStore(path);
            store.Initialize();
            body(store);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                }
            }
        }
    }
}
