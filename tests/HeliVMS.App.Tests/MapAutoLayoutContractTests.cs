using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 智慧地圖圖釘自動布局的桌面契約（M284，§14.7 #11）。
///
/// 布局演算法（<c>MapLayout.Resolve</c>）本身在 Storage 層有行為測試，但如果設定中心沒有把
/// 按鈕接上並寫回座標，能力等於不存在：重疊的圖釘永遠疊著，操作員還以為其他鏡頭沒設定。
/// </summary>
public sealed class MapAutoLayoutContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 設定中心地圖頁有自動布局按鈕且綁定()
    {
        Assert.Equal("OnMapAutoLayoutClicked", WiredHandler(SettingsXaml, "MapAutoLayoutButton"));
    }

    [Fact]
    public void 自動布局按鈕會重排圖釘並寫回座標()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnMapAutoLayoutClicked");
        Assert.Contains("MapLayout.Resolve(", handler);
        Assert.Contains("SetDevicePosition(", handler);
        Assert.Contains("ReloadMapPins(", handler);
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
