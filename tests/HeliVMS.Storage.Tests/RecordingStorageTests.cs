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
}
