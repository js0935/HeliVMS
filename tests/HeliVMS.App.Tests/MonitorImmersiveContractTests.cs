namespace HeliVMS.App.Tests;

/// <summary>
/// 攝影機畫面全畫面（沉浸模式）契約：頂欄加了進入按鈕、牆上浮動離開按鈕、
/// 進入時隱藏「所有」非攝影機 UI 並把視窗推到真全螢幕、退出原樣還原視窗級狀態、
/// Esc 先退沉浸再退視窗全螢幕。來源掃描是既有慣例（WPF 無執行期測試）。
/// </summary>
public sealed class MonitorImmersiveContractTests
{
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 頂欄與牆上浮動鈕都有接線()
    {
        Assert.Equal("OnImmersiveClicked", WiredHandler("HeaderImmersiveButton"));
        Assert.Equal("OnImmersiveClicked", WiredHandler("ImmersiveExitButton"));

        // 浮動鈕預設隱藏——它是沉浸模式裡唯一可見的控制項，平時不能佔著牆面。
        var xaml = SourceContract.Read(Xaml);
        var at = xaml.IndexOf("x:Name=\"ImmersiveExitButton\"", StringComparison.Ordinal);
        var collapsed = xaml.IndexOf("Visibility=\"Collapsed\"", at, StringComparison.Ordinal);
        Assert.True(at >= 0 && collapsed > at && collapsed - at < 200, "ImmersiveExitButton 預設必須 Collapsed");

        // 離開鈕必須在「左上角」：右上角是 REC 錄影徽章的地盤——沉浸模式錄影中時
        // 兩顆都靠右上會疊在一起（使用者看得到的是兩個半透明元素互相覆蓋）。
        var tag = xaml[at..];
        Assert.Contains("HorizontalAlignment=\"Left\"", tag[..tag.IndexOf("/>")]);
        Assert.DoesNotContain("HorizontalAlignment=\"Right\"", tag[..tag.IndexOf("/>")]);
    }

    [Fact]
    public void 進入沉浸隱藏全部周邊UI並進視窗全螢幕()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "SetMonitorImmersive");
        foreach (var chrome in new[] { "TopBarBorder.Visibility", "GroupTabs.Visibility", "FooterBorder.Visibility" })
        {
            Assert.Contains(chrome, body);
        }

        Assert.Contains("MainAreaGrid.Margin = new Thickness(0)", body);
        Assert.Contains("SetWindowFullScreen(true)", body);

        // 進入前記住視窗級全螢幕狀態：本來就在視窗全螢幕的操作員退出沉浸時，
        // 不該意外縮回視窗模式。
        Assert.Contains("_windowFullScreenBeforeImmersive = _isWindowFullScreen", body);
        Assert.Contains("SetWindowFullScreen(_windowFullScreenBeforeImmersive)", body);
    }

    [Fact]
    public void 頂欄頁籤底欄有命名可供隱藏()
    {
        var xaml = SourceContract.Read(Xaml);
        foreach (var name in new[] { "TopBarBorder", "MainAreaGrid", "FooterBorder" })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }
    }

    [Fact]
    public void Esc先退沉浸再退視窗全螢幕()
    {
        // 順序錯了會留下隱藏的頂欄與頁籤：視窗回來了、操作介面卻不見了。
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnPreviewKeyDown");
        var immersiveAt = body.IndexOf("_monitorImmersive", StringComparison.Ordinal);
        var windowAt = body.IndexOf("SetWindowFullScreen(false)", StringComparison.Ordinal);
        Assert.True(immersiveAt >= 0, "Esc 分支必須處理沉浸模式");
        Assert.True(windowAt > immersiveAt, "Esc 必須先退沉浸再退視窗全螢幕");
    }

    [Fact]
    public void 全畫面的進出文字存在於三種語言()
    {
        var catalogue = SourceContract.Read(I18n);
        foreach (var key in new[] { "Main.ImmersiveOn", "Main.ImmersiveOnTip", "Main.ImmersiveOff", "Main.ImmersiveOffTip" })
        {
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(catalogue, $"\\[\"{key}\"\\]").Count);
        }
    }

    private static string WiredHandler(string buttonName)
    {
        var xaml = SourceContract.Read(Xaml);
        var at = xaml.IndexOf("x:Name=\"" + buttonName + "\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"找不到 {buttonName}");
        var match = System.Text.RegularExpressions.Regex.Match(xaml[at..], "Click=\"(\\w+)\"");
        Assert.True(match.Success, $"{buttonName} 沒有 Click 接線");
        return match.Groups[1].Value;
    }
}
