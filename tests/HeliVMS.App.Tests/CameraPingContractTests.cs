namespace HeliVMS.App.Tests;

/// <summary>
/// 「攝影機管理 → 選取一台 → 按 Ping → 對那一台的 IP 探測」這條操作路徑的契約。
/// </summary>
/// <remarks>
/// 桌面專案沒有 WPF 執行期測試環境，所以這裡守的是接線：按鈕真的存在、真的接到處理常式、
/// 處理常式真的拿「清單中被選取的那一列」而不是整個 devices 表。這正是需求被寫錯的地方——
/// 第一版的按鈕就接在一個會遍歷全部裝置的方法上，看起來有 Ping，實際 ping 的不是操作員
/// 選的那一台，也沒有人看得出來。
/// </remarks>
public class CameraPingContractTests
{
    private const string Xaml = "src/HeliVMS.App/CameraManagerWindow.xaml";
    private const string Code = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";

    [Fact]
    public void 攝影機管理有Ping按鈕且接到處理常式()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"PingDeviceButton\"", xaml);
        Assert.Contains("Click=\"OnPingDeviceClicked\"", xaml);

        // 結果要留在畫面上，不能只是閃一下的狀態文字：狀態列每 2 秒會被連線狀態輪詢重畫。
        Assert.Contains("x:Name=\"PingStatusText\"", xaml);
        Assert.Contains("x:Name=\"CameraColPing\"", xaml);
        Assert.Contains("{Binding PingText}", xaml);
    }

    [Fact]
    public void Ping只探測清單中被選取的那一台()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnPingDeviceClicked");

        Assert.False(string.IsNullOrWhiteSpace(body), "找不到 OnPingDeviceClicked 的實作");

        // 目標來自清單選取，不是迴圈掃全部。
        Assert.Contains("DeviceList.SelectedItem", body);

        // 探測的參數也必須是那一列的主機；只斷言 body 裡出現過 row.Host 不夠——
        // 狀態文字也用了 row.Host，把探測目標換成常數時那條斷言照樣會過。
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"HostProbe\.ProbeAsync\(\s*row\.Host\s*,\s*row\.Port"), body);

        // 反向確認：處理常式裡不得出現「重新載入裝置清單」或整表掃描，否則就是「一按 ping 全部」。
        Assert.DoesNotContain("_devices.List()", body);
        Assert.DoesNotContain("foreach", body);

        // 2 秒逾時的 ICMP 加上退路的 TCP 可能等到 4 秒；同步等待會讓整個視窗凍住。
        Assert.Contains("await", body);
    }

    [Fact]
    public void Ping結果會同時寫進狀態列與清單欄位()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnPingDeviceClicked");

        // 狀態列：講清楚是哪個位址、走 ICMP 還是 TCP、幾毫秒。
        Assert.Contains("PingStatusText.Text", body);
        Assert.Contains("Camera.PingReplyTcp", body);
        Assert.Contains("\"Camera.PingReply\"", body);

        // 清單欄位：狀態列會被輪詢覆蓋，真正留存的是欄位。
        Assert.Contains("SetPing(", body);
    }

    [Fact]
    public void 沒選取攝影機時會提示而不是靜默不動作()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnPingDeviceClicked");

        // 靜默 return 會讓操作員以為按鈕壞了。
        Assert.Contains("Camera.PingSelectFirst", body);
    }

    [Fact]
    public void Ping欄位在還沒探測過時顯示佔位字元而不是逾時()
    {
        var source = SourceContract.Read(Code);

        // 「還沒 Ping」與「Ping 不到」必須看得出差別，否則整張表一開啟就全是紅字。
        Assert.Contains("_pingText ?? Localizer.T(\"Camera.PingIdle\")", source);
    }
}
