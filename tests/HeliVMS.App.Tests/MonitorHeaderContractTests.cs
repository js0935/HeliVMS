using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 監控中心頂欄的入口清單，以及「顯示元件」開關移進設定中心的接線契約。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報「找不到回放」：頂欄同時放著「去哪」（回放／事件／地圖）與「畫面上要不要出現某個
/// 元件」（快速列／事件面板／警報窗／第二螢幕／AI 疊加），一排十幾顆按鈕，入口被淹沒。
/// 現在頂欄只剩七顆：全畫面／回放／跳台／事件／地圖／設定／離開；其餘五個開關收在
/// 設定中心「功能」頁的「監看牆顯示」區塊。
/// </para>
/// <para>
/// 桌面端沒有 WPF 執行期測試（<c>HeliVMS.App.Tests</c> 不編譯 <c>HeliVMS.App</c>），所以這裡讀
/// 原始碼把三件事釘死：頂欄恰好七顆且順序固定、被移走的控制項真的不在頂欄、設定頁那五個勾選
/// 各自接到正確的 <c>WallDisplayOption</c>。漏任何一節的症狀都是「按鈕在、按了沒反應」或
/// 「兩個地方都能改同一個狀態」，日誌乾淨。
/// </para>
/// </remarks>
public sealed class MonitorHeaderContractTests
{
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string OptionsCode = "src/HeliVMS.App/Services/WallDisplayOptions.cs";

    /// <summary>頂欄保留的入口，順序即畫面由左至右。</summary>
    private static readonly string[] HeaderButtons =
    [
        "HeaderImmersiveButton",
        "HeaderPlaybackButton",
        "WallSequenceToggle",
        "HeaderEventButton",
        "MapButton",
        "HeaderSettingsButton",
        "HeaderExitButton",
    ];

    /// <summary>已移到設定中心的顯示開關（原本是頂欄的五顆 ToggleButton／CheckBox）。</summary>
    private static readonly (string Control, string Check, string Option, string Setter)[] MovedSwitches =
    [
        ("QuickBarToggle", "WallQuickBarCheck", "QuickBar", "SetQuickBarEnabled"),
        ("EventPanelToggle", "WallEventPanelCheck", "EventPanel", "SetEventPanelEnabled"),
        ("AlarmPopupToggle", "WallAlarmPopupCheck", "AlarmPopup", "SetAlarmPopupEnabled"),
        ("MirrorToggle", "WallSecondScreenCheck", "SecondScreen", "SetSecondScreenEnabled"),
        ("AiToggle", "WallAiOverlayCheck", "AiOverlay", "SetAiOverlayEnabled"),
    ];

    [Fact]
    public void 頂欄恰好保留七顆入口且順序固定()
    {
        var xaml = SourceContract.Read(MainXaml);

        Assert.Equal(HeaderButtons, TopBarButtonNames(xaml));

        // 七顆都還在畫面上（名字對了但被移到別的容器，等於「入口不見了」）。
        foreach (var name in HeaderButtons)
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        // 每一顆都要有自己的點擊路徑——頂欄是操作員唯一的入口，一顆沒接線就是死按鈕。
        foreach (var (name, handler) in new[]
                 {
                     ("HeaderImmersiveButton", "OnImmersiveClicked"),
                     ("HeaderPlaybackButton", "OnPlaybackClicked"),
                     ("WallSequenceToggle", "OnWallSequenceToggled"),
                     ("HeaderEventButton", "OnEventClicked"),
                     ("MapButton", "OnMapClicked"),
                     ("HeaderSettingsButton", "OnSettingsClicked"),
                     ("HeaderExitButton", "OnExitClicked"),
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
            Assert.Contains($"Click=\"{handler}\"", xaml);
        }
    }

    [Fact]
    public void 被移走的顯示開關不再出現在頂欄()
    {
        var mainXaml = SourceContract.Read(MainXaml);
        var mainCode = SourceContract.Read(MainCode);
        var settingsXaml = SourceContract.Read(SettingsXaml);

        foreach (var (control, check, _, _) in MovedSwitches)
        {
            Assert.DoesNotContain(control, mainXaml);
            Assert.DoesNotContain(control, mainCode);

            // 設定頁那一顆要在，而且勾選與取消都接到同一個 handler（只接一半＝取消不掉）。
            Assert.Contains($"x:Name=\"{check}\"", settingsXaml);
            Assert.Contains($"Checked=\"OnWallDisplayToggled\"", settingsXaml);
            Assert.Contains($"Unchecked=\"OnWallDisplayToggled\"", settingsXaml);
        }

        // 整個區塊有標題、說明，並且在沒有牆面可控制時整區停用（勾了什麼都不會發生，不能假裝可用）。
        Assert.Contains("x:Name=\"WallDisplaySection\"", settingsXaml);
        Assert.Contains("WallDisplaySection.IsEnabled = false", SourceContract.Read(SettingsCode));
    }

    [Fact]
    public void 五個開關各自接到正確的顯示項目()
    {
        var settings = SourceContract.Read(SettingsCode);
        var main = SourceContract.Read(MainCode);
        var options = SourceContract.Read(OptionsCode);

        // 列舉本身要有這五個成員：介面只有一份，兩邊都靠它對照。
        foreach (var (_, _, option, _) in MovedSwitches)
        {
            Assert.Contains($"{option},", options);
        }

        Assert.Equal(
            MovedSwitches.Length,
            Regex.Matches(settings, @"_wallDisplayBoxes\[Wall\w+\] = WallDisplayOption\.\w+").Count);

        var setter = SourceContract.BodyOf(main, "SetWallDisplayOption");
        var getter = SourceContract.BodyOf(main, "GetWallDisplayOption");
        Assert.NotEqual(string.Empty, setter);
        Assert.NotEqual(string.Empty, getter);

        foreach (var (_, check, option, target) in MovedSwitches)
        {
            Assert.Contains($"_wallDisplayBoxes[{check}] = WallDisplayOption.{option}", settings);
            Assert.Contains($"WallDisplayOption.{option} => {target}(enabled)", setter);
            Assert.Contains($"WallDisplayOption.{option} =>", getter);

            // setter 本體必須存在（分支指到一個改名後不存在的方法會編不過，但列舉漏一項不會）。
            Assert.NotEqual(string.Empty, SourceContract.BodyOf(main, target));
        }

        // 牆面仍是狀態唯一持有者：設定頁拿到的必須是主視窗自己。
        Assert.Contains("public partial class MainWindow : Window, IWallDisplayOptions", main);
        Assert.Contains("wallDisplay: this", main);
    }

    [Fact]
    public void 設定頁以實際生效值回寫勾選()
    {
        var settings = SourceContract.Read(SettingsCode);
        var toggled = SourceContract.BodyOf(settings, "OnWallDisplayToggled");
        Assert.NotEqual(string.Empty, toggled);

        // AI 沒有授權、第二螢幕開不起來時 setter 回 false：勾選要退回，否則設定頁說謊。
        Assert.Contains("SetWallDisplayOption(option, requested)", toggled);
        Assert.Contains("box.IsChecked = effective", toggled);

        // 牆面自己關掉（鏡像窗被 ✕／Esc、螢幕被拔掉）時回拋，設定頁重讀實際狀態。
        Assert.Contains("WallDisplayOptionChanged += OnWallDisplayOptionChanged", settings);
        Assert.Contains("WallDisplayOptionChanged -= OnWallDisplayOptionChanged", settings);

        var changed = SourceContract.BodyOf(settings, "OnWallDisplayOptionChanged");
        Assert.Contains("GetWallDisplayOption(option)", changed);
    }

    /// <summary>頂欄（<c>TopBarBorder</c>）裡所有 Button／ToggleButton／CheckBox 的 <c>x:Name</c>，依出現順序。</summary>
    /// <remarks>
    /// 刻意不用「找 StackPanel 再切」：頂欄的版面改了（例如多一欄狀態文字）就會切錯位置，
    /// 而這條契約要驗的正是「頂欄有哪些按鈕」。以 <c>LicenseBanner</c> 的註解當右界，
    /// 是因為它就是下一個區塊的起點，且不會跟著頂欄版面變動。
    /// </remarks>
    private static string[] TopBarButtonNames(string xaml)
    {
        var start = xaml.IndexOf("x:Name=\"TopBarBorder\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow.xaml 找不到頂欄（TopBarBorder）");

        var end = xaml.IndexOf("<!-- License expiry banner", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到頂欄的結尾（License expiry banner 之前的區塊）");

        return
        [
            .. Regex.Matches(
                    xaml[start..end],
                    @"<(?:Button|ToggleButton|CheckBox)\b[^>]*?x:Name=""(\w+)""",
                    RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value),
        ];
    }
}
