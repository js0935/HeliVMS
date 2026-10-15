namespace HeliVMS.App.Tests;

/// <summary>
/// 網路頁 NTP 對時契約（M305；校時引擎沿用 M68 的 <c>NtpClient</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 「對時」若只是查得到時間卻沒真的調整系統時鐘，操作員會在頁面上看到合理的偏移量、
/// 以為校時成功，但錄影時間戳仍照舊——證據時序就對不上。所以這裡把三件不能斷的事釘住：
/// 頁面有控制項且接了處理器、處理器真的呼叫校時（不是只寫設定）、以及校時路徑會去啟用
/// <c>SeSystemtimePrivilege</c>（不啟用的話 <c>SetSystemTime</c> 一定失敗）。
/// </para>
/// </remarks>
public sealed class NtpTimeSyncContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string NtpSync = "src/HeliVMS.App/Services/NtpSync.cs";
    private const string SystemClock = "src/HeliVMS.App/Services/SystemClock.cs";

    [Fact]
    public void 網路頁有NTP對時控制項且接了立即對時處理器()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "NtpSectionTitle", "NtpEnabledBox", "NtpServerBox",
                     "NtpSyncButton", "NtpStatusText", "NtpHintText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Contains("Click=\"OnNtpSyncClicked\"", xaml);
    }

    [Fact]
    public void 立即對時會寫入設定並呼叫NTP引擎校時()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnNtpSyncClicked");
        Assert.NotEqual(string.Empty, handler);

        Assert.Contains("NtpSync.EnabledKey", handler);
        Assert.Contains("NtpSync.ServerKey", handler);

        // 關鍵：要真的去對時，不能只把伺服器字串存起來。
        Assert.Contains("NtpSync.SyncAsync", handler);
    }

    [Fact]
    public void 對時會嘗試校正系統時鐘且會啟用SeSystemtimePrivilege()
    {
        var sync = SourceContract.Read(NtpSync);
        Assert.NotEqual(string.Empty, sync);

        // 查到的伺服器時間必須寫回系統時鐘；否則對時對證據時序毫無意義。
        Assert.Contains("SystemClock.TrySetUtcNow", sync);

        // 一般權杖預設沒有校時權限，必須先啟用；否則 SetSystemTime 直接失敗。
        Assert.Contains("SeSystemtimePrivilege", SourceContract.Read(SystemClock));
    }

    [Fact]
    public void 主視窗依設定自動定期對時並於關閉時停掉()
    {
        var code = SourceContract.Read(MainWindow);

        var start = SourceContract.BodyOf(code, "StartNtpSync");
        Assert.NotEqual(string.Empty, start);
        Assert.Contains("NtpSync.EnabledKey", start);

        // 關閉時要停掉計時器，否則視窗關了還留著一個每 6 小時打一次網路的排程。
        Assert.Contains("_ntpTimer?.Stop();", code);
    }
}
