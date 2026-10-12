using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 回放→遮蔽匯出預填契約（M97，§14.7 #5）。
///
/// <para>
/// <c>RedactionWindow</c> 的建構子早就能接收頻道與時間範圍，卻從來沒有人帶值呼叫過，
/// 操作員必須先記下回放中的時間、再手動在遮蔽窗重挑一次，挑錯段是常見失誤。
/// 這份契約要求回放視窗提供入口，且入口真的把目前選取的頻道與區段傳進去。
/// </para>
/// </summary>
public sealed class PlaybackRedactionContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    [Fact]
    public void 回放視窗有遮蔽匯出入口且接線()
    {
        Assert.Equal("OnRedactFromPlaybackClicked", WiredHandler(Xaml, "RedactExportButton"));
    }

    [Fact]
    public void 遮蔽入口帶入目前頻道與區段()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(Code), "OnRedactFromPlaybackClicked");
        Assert.Contains("new RedactionWindow(", handler);
        Assert.Contains("ChannelCombo.SelectedItem", handler);
        Assert.Contains("SegmentList.SelectedItem", handler);
    }

    [Fact]
    public void 回放視窗有遮蔽疊圖畫布與預設開啟的開關()
    {
        var canvas = TagOf(Xaml, "RedactOverlay");
        Assert.Contains("Canvas", canvas);
        Assert.Contains("IsHitTestVisible=\"False\"", canvas);

        var checkbox = TagOf(Xaml, "RedactionOverlayCheck");
        Assert.Contains("CheckBox", checkbox);
        Assert.Contains("IsChecked=\"True\"", checkbox);
        Assert.Contains("Checked=\"OnRedactionOverlayToggled\"", checkbox);
    }

    [Fact]
    public void 回放載入目前片段時窗內的已登錄遮蔽區域()
    {
        var source = SourceContract.Read(Code);
        Assert.Contains("LoadRedactionRegions(seg);", SourceContract.BodyOf(source, "PlayFrom"));

        var loader = SourceContract.BodyOf(source, "LoadRedactionRegions");
        Assert.Contains("QueryByTime", loader);
        Assert.Contains("_redactions", loader);
    }

    [Fact]
    public void 疊圖以來源與顯示尺寸投影並受開關控制()
    {
        var layout = SourceContract.BodyOf(SourceContract.Read(Code), "LayoutRedactionOverlay");
        Assert.Contains("RedactionOverlay.Project(", layout);
        Assert.Contains("RedactionOverlayCheck.IsChecked", layout);
        Assert.Contains("PlayerFrame.ActualWidth", layout);
    }

    private static string TagOf(string relativePath, string element)
    {
        var xaml = SourceContract.Read(relativePath);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        return tag;
    }

    private static string WiredHandler(string relativePath, string element)
    {
        var tag = TagOf(relativePath, element);
        var handler = Regex.Match(tag, @"\bClick=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
