namespace HeliVMS.App.Tests;

/// <summary>
/// 登入鎖死防線的接線契約（M42／§18.6）。
///
/// <para>
/// <see cref="HeliVMS.Storage.AuthService"/> 提供 <c>HasEnabledAdmin</c>／
/// <c>WouldRemoveLastEnabledAdmin</c> 的判定（行為由 Storage 測試保證），但真正防鎖死的是
/// 「UI／啟動流程有沒有真的呼叫它」。桌面端沒有 WPF 執行期測試，少了這道契約，
/// 第 N 次重構就可能把守衛拿掉而全綠——回到「啟用後沒帳號可登入」的原始事故。
/// </para>
///
/// <para>
/// 與 <see cref="RbacEntryPointContractTests"/> 相同理由採來源掃描：WPF 視窗無法在
/// net10.0 測試專案中具現化，只能驗證原始碼把守衛接在對的位置。
/// </para>
/// </summary>
public sealed class AuthBootstrapContractTests
{
    private const string SettingsWindow = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string App = "src/HeliVMS.App/App.xaml.cs";

    [Fact]
    public void 啟用驗證前要檢查是否有可登入的管理員()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsWindow), "OnAuthEnabledChanged");

        Assert.Contains("HasEnabledAdmin", body);
    }

    [Fact]
    public void 停用使用者前要擋下最後一個管理員()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsWindow), "OnUserToggleClicked");

        Assert.Contains("WouldRemoveLastEnabledAdmin", body);
    }

    [Fact]
    public void 刪除使用者前要擋下最後一個管理員()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(SettingsWindow), "OnUserDeleteClicked");

        Assert.Contains("WouldRemoveLastEnabledAdmin", body);
    }

    [Fact]
    public void 啟動登入前要處理沒有管理員的舊資料()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(App), "PerformLogin");

        Assert.Contains("HasEnabledAdmin", body);
    }
}
