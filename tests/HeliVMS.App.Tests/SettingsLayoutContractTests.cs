using System.Xml.Linq;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心內容區的可及性契約。
///
/// <para>
/// 現場症狀：儲存與通知兩頁的欄位比 1366x768 扣掉工作列後的可視高度還高，而內容區
/// 完全沒有捲軸。WPF 對超出範圍的內容是直接裁掉，不會出現半個控制項、也沒有任何提示，
/// 所以最下面那一排「套用」「清理」是看得到文字、點不到——而設定視窗是最大化且
/// <c>ResizeMode="NoResize"</c>，操作員連拉大視窗這條路都沒有。
/// </para>
/// <para>
/// 這裡用 XML 解析而不是比對原始碼字串：要守的是「這些頁面在視覺樹上位於一個可垂直
/// 捲動的祖先之下」，換行、註解或屬性順序都不該讓測試誤判。
/// </para>
/// </summary>
public sealed class SettingsLayoutContractTests
{
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void 儲存與通知頁在低解析度下可以捲到最下面那一排按鈕()
    {
        var scroll = ContentScroll();

        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));

        var names = DescendantNames(scroll);
        Assert.Contains("PageStorage", names);
        Assert.Contains("PageNotify", names);

        // 清單最下面的按鈕就是被裁掉的那一批：它們必須跟頁面在同一個捲動區裡，
        // 否則捲軸出現與否都救不到它們。
        Assert.Contains("ApplyRecordingsDiskButton", names);
        Assert.Contains("CleanupButton", names);
        Assert.Contains("NotifyApplyButton", names);
    }

    [Fact]
    public void 設定內容區不依賴星號高度且不做水平捲動()
    {
        var scroll = ContentScroll();

        // ScrollViewer 量測子元素時高度是無限大，Height="*" 的分割列會塌成 0：
        // 頁面一旦改成星號高度，包進捲動區就會整頁消失，而且沒有任何錯誤。
        var starRows = scroll.Descendants(XName.Get("RowDefinition", PresentationNs))
            .Where(row => (string?)row.Attribute("Height") == "*")
            .ToList();
        Assert.True(
            starRows.Count == 0,
            $"內容區用了 {starRows.Count} 個 Height=\"*\" 的分割列；包在 ScrollViewer 裡它們會塌成 0。改用固定高度或 Auto。");

        // 水平方向一旦可捲，頁面就量到無限寬，TextWrapping 永遠不換行，
        // 整個頁面會被拉成一條超寬的橫列。
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void 左側類別導覽不會跟著內容一起捲走()
    {
        var root = Settings();
        var settingsNav = root.Descendants()
            .Single(e => (string?)e.Attribute(XName.Get("Name", XamlNs)) == "SettingsNav");

        // 導覽列被包進捲動區會跟著內容一起滑走：換到長頁面後就看不到分類，
        // 只能先把內容捲回頂端才能換頁。
        Assert.DoesNotContain(ContentScroll(root).Descendants(), e => e == settingsNav);
    }

    private static XElement ContentScroll(XElement? root = null)
    {
        var scroll = (root ?? Settings()).Descendants(XName.Get("ScrollViewer", PresentationNs))
            .FirstOrDefault(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "SettingsContentScroll");
        Assert.True(
            scroll is not null,
            "設定中心內容區沒有捲軸：儲存與通知頁在 1366x768 上比可視高度還高，最下面的按鈕會點不到。");
        return scroll!;
    }

    private static HashSet<string?> DescendantNames(XElement root)
        => root.Descendants()
            .Select(e => (string?)e.Attribute(XName.Get("Name", XamlNs)))
            .Where(name => name is not null)
            .ToHashSet();

    private static XElement Settings()
    {
        var path = Path.Combine(SourceContract.RepoRoot(), Xaml.Replace('/', Path.DirectorySeparatorChar));
        return XDocument.Load(path).Root!;
    }
}
