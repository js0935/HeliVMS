namespace HeliVMS.Storage.Tests;

/// <summary>
/// 顯示／錄影分流（M76，§15.2）：顯示可切次流省頻寬，錄影必須固定主流。
/// 這裡防的是「切到次流後錄影也安靜地跟著變次流」——segment 仍標記 main，事後看檔案才發現畫質掉了。
/// </summary>
public sealed class StreamRoutingTests
{
    [Fact]
    public void 選次碼流且次流位址非空時顯示用次流()
    {
        Assert.Equal("rtsp://cam/sub", StreamRouting.DisplayUrl("rtsp://cam/main", "rtsp://cam/sub", StreamKind.Sub));
    }

    [Fact]
    public void 選主碼流時顯示用主流()
    {
        Assert.Equal("rtsp://cam/main", StreamRouting.DisplayUrl("rtsp://cam/main", "rtsp://cam/sub", StreamKind.Main));
    }

    [Fact]
    public void 次流位址為空時退回主流()
    {
        // 有些手動輸入的頻道沒有次流；切 Sub 不能變成拉一個空位址。
        Assert.Equal("rtsp://cam/main", StreamRouting.DisplayUrl("rtsp://cam/main", string.Empty, StreamKind.Sub));
        Assert.Equal("rtsp://cam/main", StreamRouting.DisplayUrl("rtsp://cam/main", "   ", StreamKind.Sub));
    }

    [Fact]
    public void 錄影永遠用主流()
    {
        Assert.Equal("rtsp://cam/main", StreamRouting.RecordingUrl("rtsp://cam/main"));
    }
}
