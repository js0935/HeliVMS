using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 桌面外殼（<c>App.xaml</c>／<c>App.xaml.cs</c>）與共用狀態標籤的多語系收尾契約（M293 §14.1 #7）。
/// </summary>
/// <remarks>
/// <para>
/// §14.1 #7 一直寫著「其餘 19 個視窗仍硬編繁中」，實際盤點後視窗 XAML 幾乎都已在地化，
/// 真正的殘留只有三處：全域輸入框範本裡兩顆「⌨」呼叫鍵的 <c>ToolTip="螢幕虛擬鍵盤"</c>、
/// 事件中心顯示事件狀態時呼叫固定中文的 <c>AlarmEventStatus.Label</c>（切換語系會殘留中文），
/// 以及「登入驗證開著卻沒有可用 admin」的復原警告（唯一刻意保留的中文，見下）。
/// </para>
/// <para>
/// 視窗 XAML 由 <see cref="WpfI18nContractTests"/> 釘住；這裡補上外殼與程式碼後置，
/// 否則新增的硬編字串（尤其在程式碼後置裡）不會有任何測試轉紅。
/// </para>
/// </remarks>
public sealed class AppShellI18nContractTests
{
    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//[^\r\n]*");
    private static readonly Regex Han = new(@"[\u4e00-\u9fff]");

    [Fact]
    public void 應用程式資源字典不含硬編中文()
    {
        // App.xaml 的註解保留了設計決策（深色主題、白底白字病根），可留中文；
        // 但標記（屬性值、文字內容）不得再硬編中文。
        var xaml = XmlComment.Replace(SourceContract.Read("src/HeliVMS.App/App.xaml"), string.Empty);
        Assert.DoesNotMatch(Han, xaml);
    }

    [Fact]
    public void 外殼程式碼後置不含硬編中文()
    {
        var code = BlockComment.Replace(SourceContract.Read("src/HeliVMS.App/App.xaml.cs"), string.Empty);
        code = LineComment.Replace(code, string.Empty);

        // 唯一允許的中文：登入驗證開著卻沒有可用 admin 時的復原警告
        // （%TEMP%\helivms-auth-bootstrap.log）。那是給營運人員的診斷訊息、不是 UI，
        // 刻意保持中文以免他們誤以為程式崩潰；行為由 AuthBootstrapLockoutTests 另行守住。
        code = code
            .Replace("auth.enabled=1 但沒有任何啟用中的 admin；", string.Empty)
            .Replace("已略過登入以允許復原，請進設定新增 admin 或停用登入驗證。", string.Empty);

        Assert.DoesNotMatch(Han, code);
    }

    [Fact]
    public void 虛擬鍵盤呼叫鍵提示已在地化()
    {
        Assert.DoesNotContain(
            "ToolTip=\"螢幕虛擬鍵盤\"",
            SourceContract.Read("src/HeliVMS.App/App.xaml"));
        Assert.Contains(
            "call.ToolTip = Localizer.T(\"Osk.CallTip\")",
            SourceContract.Read("src/HeliVMS.App/App.xaml.cs"));
    }

    [Fact]
    public void 事件中心狀態標籤走多語鍵()
    {
        var code = SourceContract.Read("src/HeliVMS.App/EventCenterWindow.xaml.cs");
        Assert.DoesNotContain("AlarmEventStatus.Label(", code);
        Assert.Contains("Localizer.T(\"AlarmStatus.\"", code);
    }

    [Fact]
    public void 沒有任何視窗使用固定中文的共用狀態標籤()
    {
        // AlarmEventStatus.Label／AlarmPriority.Label 固定輸出繁中，UI 一律要走
        // AlarmStatus.<status>／AlarmPriority.<priority> 鍵，否則切語系就殘留中文。
        var windows = Directory.EnumerateFiles(
            Path.Combine(SourceContract.RepoRoot(), "src", "HeliVMS.App"),
            "*Window.xaml.cs",
            SearchOption.TopDirectoryOnly);

        foreach (var window in windows)
        {
            var code = LineComment.Replace(File.ReadAllText(window), string.Empty);
            Assert.DoesNotContain("AlarmEventStatus.Label(", code);
            Assert.DoesNotContain("AlarmPriority.Label(", code);
        }
    }
}
