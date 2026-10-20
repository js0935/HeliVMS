namespace HeliVMS.App.Tests;

/// <summary>
/// 實測碼率的來源查詢必須只認 <c>final</c>（<c>ListPlayableByRange</c>），不能用 <c>ListByRange</c>。
///
/// <para>
/// 症狀：<c>ListByRange</c> 為了對帳把 tmp／corrupt／missing／retired 也回傳，而
/// <c>ObservedBitrateEstimator</c> 只看大小與長度、不看狀態——一個已被刪除的段照樣被算進
/// 「實測碼率」。頻寬治理（RecordingScheduler）會依一個磁碟上根本不存在的碼率去分配，
/// 最該省頻寬時反而分錯；頻道診斷面板（MainWindow）也會顯示不存在於磁碟的流量。
/// </para>
/// </summary>
public sealed class ObservedBitrateSourceContractTests
{
    private const string MainWindowCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SchedulerCode = "src/HeliVMS.Recording/RecordingScheduler.cs";

    [Fact]
    public void 頻道診斷的實測碼率只取可播放段()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(MainWindowCode), "TryObservedBitrate");

        Assert.Contains("ListPlayableByRange", handler);
        Assert.DoesNotContain("ListByRange(", handler);
    }

    [Fact]
    public void 頻寬治理的實測碼率只取可播放段()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SchedulerCode), "DefaultObservedBps");

        Assert.Contains("ListPlayableByRange", handler);
        Assert.DoesNotContain("ListByRange(", handler);
    }
}
