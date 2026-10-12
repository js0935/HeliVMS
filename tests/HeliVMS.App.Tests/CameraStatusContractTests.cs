namespace HeliVMS.App.Tests;

/// <summary>
/// 攝影機管理「狀態」欄契約。
/// </summary>
/// <remarks>
/// <para>
/// 症狀是「欄位在，但一直顯示同一種狀態」、「顏色永遠一樣」或「完全沒有這一欄」：桌面端沒有
/// WPF 執行期測試，而狀態是從 <c>ChannelManager</c> 的頻道工作階段推導、靠計時器輪詢更新。
/// 這條接線任何一段斷掉（欄位沒綁 <c>StatusText</c>／<c>StatusBrush</c>、<c>ComputeStatus</c>
/// 不看工作階段、計時器沒啟動）都不會編譯錯，只會安靜地顯示錯誤的狀態或顏色。
/// </para>
/// <para>
/// 顏色刻意由狀態推導（<c>StatusBrush</c>）而非在 XAML 比對翻譯後的中文字串，否則切換語系
/// 就會讓顏色失效。這裡釘住綁定存在、狀態推導看工作階段、以及三態各有自己的筆刷。
/// </para>
/// <para>
/// 已知邊界：契約只證明「綁定與接線存在」，不證明狀態值本身對——那需要 WPF 執行期環境。
/// </para>
/// </remarks>
public sealed class CameraStatusContractTests
{
    private const string Xaml = "src/HeliVMS.App/CameraManagerWindow.xaml";
    private const string Code = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";

    [Fact]
    public void 攝影機清單有狀態欄且綁定文字與顏色()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.Contains("x:Name=\"CameraColStatus\"", xaml);
        Assert.Matches(
            @"x:Name=""CameraColStatus""[\s\S]*?Text=""\{Binding StatusText\}""[\s\S]*?Foreground=""\{Binding StatusBrush\}""",
            xaml);

        Assert.Contains(
            "CameraColStatus.Header = Localizer.T(\"Camera.ColStatus\")",
            SourceContract.Read(Code));
    }

    [Fact]
    public void 狀態依頻道工作階段推導()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ComputeStatus");
        Assert.NotEqual(string.Empty, body);

        // 攝影機本身沒有連線，要看它底下頻道的工作階段狀態。
        Assert.Contains("ChannelSessionFor", body);
        Assert.Contains("RtspState.Streaming", body);
        Assert.Contains("RtspState.Connecting", body);
        Assert.Contains("CameraStatus.NoChannel", body);
    }

    [Fact]
    public void 三種狀態各有對應顏色與文字()
    {
        var code = SourceContract.Read(Code);

        // 綠色（已連線）、橘色（連線中）、紅色（離線）各有一支筆刷，且由狀態推導。
        Assert.Contains("s_onlineBrush", code);
        Assert.Contains("s_connectingBrush", code);
        Assert.Contains("s_offlineBrush", code);

        // 這兩個是繫結用的唯讀屬性（運算式主體，無括號），SourceContract.BodyOf 只認方法
        // 簽章，故直接以運算式主體範圍比對。
        Assert.Matches(
            @"StatusBrush\s*=>[\s\S]*?CameraStatus\.Online[\s\S]*?s_onlineBrush[\s\S]*?CameraStatus\.Connecting[\s\S]*?s_connectingBrush[\s\S]*?s_offlineBrush",
            code);

        Assert.Matches(
            @"StatusText\s*=>[\s\S]*?Camera\.StatusOnline[\s\S]*?Camera\.StatusConnecting[\s\S]*?Camera\.StatusOffline",
            code);
    }

    [Fact]
    public void 狀態會定時刷新()
    {
        var code = SourceContract.Read(Code);

        // 連線／斷線不經過本視窗的事件，只能輪詢。
        Assert.Contains("DispatcherTimer", code);
        Assert.Contains("_statusTimer", code);
        Assert.Matches(@"_statusTimer\.Tick\s*\+=\s*\([^)]*\)\s*=>\s*RefreshStatuses\(\)", code);
        Assert.Contains("_statusTimer.Stop()", code);

        var refresh = SourceContract.BodyOf(code, "RefreshStatuses");
        Assert.NotEqual(string.Empty, refresh);
        Assert.Contains(".Status = ComputeStatus(", refresh);
    }
}
