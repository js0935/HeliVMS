namespace HeliVMS.App.Tests;

/// <summary>
/// 無邊框最大化視窗必須自備出路（AGENTS 規則的全面執法）：每一個都以
/// 右上角紅 X（Content="✕"、紅底 #C62828）＋ Esc 關閉。過去只有 SchedulingWindow
/// 踩過這個教訓；這裡把整類視窗一次釘死，新增視窗沒接線就會紅。
/// </summary>
public sealed class WindowExitContractTests
{
    private static readonly string[] BorderlessMaximized =
    [
        "DetectionWindow", "RedactionWindow", "DewarpWindow", "StreamSwitchWindow", "IoWindow", "TalkWindow",
        "CameraManagerWindow", "ChannelManagerWindow", "MonitorGroupWindow", "RecordingImportWindow",
        "NotificationLogWindow", "OnvifWizardWindow", "SchedulingWindow", "ExportWindow", "ExportCenterWindow",
        "MapWindow", "PtzWindow", "SettingsWindow", "ImageSearchWindow", "VisualRoiWindow",
    ];

    [Fact]
    public void 無邊框最大化視窗都有紅X關閉鈕與Esc接線()
    {
        foreach (var name in BorderlessMaximized)
        {
            var xaml = SourceContract.Read($"src/HeliVMS.App/{name}.xaml");
            var cs = SourceContract.Read($"src/HeliVMS.App/{name}.xaml.cs");

            Assert.True(xaml.Contains("Content=\"✕\""), $"{name} 缺少紅 X 關閉鈕");
            Assert.True(
                xaml.Contains("Click=\"OnCloseClicked\"") || xaml.Contains("Click=\"OnExitClicked\""),
                $"{name} 的關閉鈕沒有接線");
            Assert.True(cs.Contains("Key.Escape") || cs.Contains("OnWindowKeyDown"), $"{name} 缺少 Esc 關閉");
        }
    }
}
