using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 快照一鍵遮蔽契約（M116，§14.7 #5）。
///
/// <para>
/// <c>SnapshotRedactionService</c>／<c>SnapshotRedactor.Apply</c> 能把已記錄的遮蔽區域燒錄進
/// 靜態快照，但全 repo 沒有任何呼叫端——連錄製遮蔽區域的 UI 都沒有。這份契約把事件中心入口、
/// 區域記錄（<c>RedactionRepository</c> 的 snapshot 來源）與套用路徑釘住。
/// </para>
/// </summary>
public sealed class SnapshotRedactContractTests
{
    private const string EventCode = "src/HeliVMS.App/EventCenterWindow.xaml.cs";
    private const string EventXaml = "src/HeliVMS.App/EventCenterWindow.xaml";
    private const string RedactCode = "src/HeliVMS.App/SnapshotRedactWindow.xaml.cs";
    private const string RedactXaml = "src/HeliVMS.App/SnapshotRedactWindow.xaml";

    [Fact]
    public void 事件中心有快照遮蔽入口且接線()
    {
        Assert.Equal("OnRedactSnapshotClicked", WiredHandler(EventXaml, "SnapshotRedactButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(EventCode), "OnRedactSnapshotClicked");
        Assert.Contains("new SnapshotRedactWindow(", handler);
    }

    [Fact]
    public void 快照遮蔽窗記錄並套用快照來源的遮蔽區域()
    {
        var code = SourceContract.Read(RedactCode);
        Assert.Contains("new RedactionRepository(", code);
        Assert.Contains("RedactionSources.Snapshot", code);
        Assert.Contains(".QueryBySource(RedactionSources.Snapshot", code);
        Assert.Contains("new SnapshotRedactionService(", code);
        Assert.Contains(".Redact(bytes, _eventId)", code);
    }

    [Fact]
    public void 快照遮蔽窗有預覽與區域控制項()
    {
        var xaml = SourceContract.Read(RedactXaml);
        foreach (var element in new[] { "SnapPreview", "RegionList", "RegionX", "RegionW", "AddRegionButton", "RemoveRegionButton", "ApplyRedactButton" })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }
    }

    private static string WiredHandler(string relativePath, string element)
    {
        var xaml = SourceContract.Read(relativePath);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\bClick=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
