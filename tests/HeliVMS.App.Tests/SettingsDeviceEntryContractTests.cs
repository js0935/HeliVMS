using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心「裝置與頻道」入口契約。
///
/// <para>
/// 攝影機／頻道管理曾被整段從設定中心移除，只留一行註解；主畫面的「ONVIF 探索」按鈕
/// 也一併被收進「攝影機管理」視窗。結果操作員在設定中心找不到新增裝置的入口，
/// 而沒有任何測試會紅。這份契約把「設定中心必須有可開啟兩個管理視窗的入口」釘住，
/// 並要求入口handler 自己擋管理員（viewer 用鍵盤或程式化呼叫也進不去）。
/// </para>
/// </summary>
public sealed class SettingsDeviceEntryContractTests
{
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";

    [Fact]
    public void 設定中心必須有裝置與頻道分類與頁面()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"NavDevices\"", xaml);
        Assert.Contains("x:Name=\"PageDevices\"", xaml);
    }

    [Fact]
    public void 選取裝置與頻道分類要顯示該頁()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnNavChanged");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains("PageDevices.Visibility", body);
        Assert.Contains("\"Settings.NavDevices\"", body);
    }

    /// <summary>
    /// 兩顆入口按鈕各自接到對應的管理視窗，且 handler 擋管理員。
    /// </summary>
    [Theory]
    [InlineData("OpenCameraManagerButton", "OnOpenCameraManagerClicked", "CameraManagerWindow")]
    [InlineData("OpenChannelManagerButton", "OnOpenChannelManagerClicked", "ChannelManagerWindow")]
    public void 設定中心入口要接到對應管理視窗且限admin(string button, string handler, string window)
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.Matches($@"x:Name=""{button}""[^>]*Click=""{handler}""", xaml);
        Assert.Contains(window, xaml); // 入口按鈕的工具提示／頁面說明要提到目標視窗名稱。

        var body = SourceContract.BodyOf(SourceContract.Read(Code), handler);
        Assert.NotEqual(string.Empty, body);

        // 少了 guard，viewer 用鍵盤或程式化呼叫照樣開得了裝置（含憑證）。
        Assert.Contains(window, body);
        Assert.Matches(@"SessionContext\.IsAdmin", body);
    }
}
