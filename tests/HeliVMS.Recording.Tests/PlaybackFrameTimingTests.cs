using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放時間基準：影格序號 → 檔案內容時刻。
/// </summary>
/// <remarks>
/// 這裡釘的是現場回報「多路回放各播各的」的根因：舊算法用「影格序號 ÷ 名目幀率」推算時刻，
/// 只有在檔案真是定幀、相機也照名目速率送格時才成立。相機送格速率一變（名目 15fps 實際
/// 12.5fps、名目 30fps 實際 13fps），每一格都比名目時間早到，各台以不同速率前進：
/// 一台 1.2×、另一台 2.3×，而 OSD 顯示的時刻也是錯的。
/// </remarks>
public class PlaybackFrameTimingTests
{
    [Fact]
    public void 起播點落在第一個不早於它的封包()
    {
        double[] times = [0, 0.067, 0.133, 5.0, 5.067, 9.933];

        Assert.Equal(0, PlaybackFrameTiming.StartIndex(times, 0));
        Assert.Equal(2, PlaybackFrameTiming.StartIndex(times, 0.1));
        Assert.Equal(3, PlaybackFrameTiming.StartIndex(times, 0.2));
        Assert.Equal(3, PlaybackFrameTiming.StartIndex(times, 5));
        Assert.Equal(5, PlaybackFrameTiming.StartIndex(times, 9.9));
    }

    [Fact]
    public void 起播點越過所有封包時沒有可用時間戳()
    {
        // ＝ffmpeg 不會輸出任何影格；呼叫端退回固定間隔，回放不會卡住。
        Assert.Equal(-1, PlaybackFrameTiming.StartIndex([0, 1, 2], 5));
        Assert.Equal(-1, PlaybackFrameTiming.StartIndex([], 0));
    }

    [Fact]
    public void 時間戳的空隙會如實出現在內容時刻()
    {
        // 相機斷流 5 秒：中間沒有任何影格，之後那一格必須直接跳到空隙之後，
        // 而不是被壓成「下一格只晚 67ms」——後者會讓兩台看似同步卻在播不同時刻。
        double[] times = [0, 0.067, 0.133, 5.0, 5.067];
        var start = PlaybackFrameTiming.StartIndex(times, 0);

        Assert.Equal(0L, PlaybackFrameTiming.ContentMs(times, start, 0, 0, 66.7));
        Assert.Equal(67L, PlaybackFrameTiming.ContentMs(times, start, 1, 0, 66.7));
        Assert.Equal(5000L, PlaybackFrameTiming.ContentMs(times, start, 3, 133, 66.7));
        Assert.Equal(5067L, PlaybackFrameTiming.ContentMs(times, start, 4, 5000, 66.7));
    }

    [Fact]
    public void 內容時刻從起播點起算不是檔案開頭()
    {
        // 從段內 100 秒起播：第 0 格是 0ms（＝起播點），第 2 格才是它與起播點的差。
        double[] times = [99.9, 100.0, 100.067, 100.133];
        var start = PlaybackFrameTiming.StartIndex(times, 100.0);

        Assert.Equal(1, start);
        Assert.Equal(0L, PlaybackFrameTiming.ContentMs(times, start, 0, 0, 66.7));
        Assert.Equal(67L, PlaybackFrameTiming.ContentMs(times, start, 1, 0, 66.7));
        Assert.Equal(133L, PlaybackFrameTiming.ContentMs(times, start, 2, 0, 66.7));
    }

    [Fact]
    public void 目前位置所在的那一格是逐幀的起點()
    {
        // 逐幀要踩「檔案裡真的相鄰的兩格」：名目 15fps 實送 12.5fps 的檔案上，
        // 固定 1/15 秒的步長會落在同一格（按了像沒反應），30fps 名目則會一次跳好幾格。
        double[] times = [0, 0.067, 0.133, 5.0];

        Assert.Equal(0, PlaybackFrameTiming.IndexAtOrBefore(times, 0));
        Assert.Equal(1, PlaybackFrameTiming.IndexAtOrBefore(times, 0.1));
        Assert.Equal(2, PlaybackFrameTiming.IndexAtOrBefore(times, 0.133));
        Assert.Equal(3, PlaybackFrameTiming.IndexAtOrBefore(times, 99));

        // 位置在檔案第一格之前（浮點誤差或段端點）＝沒有可用格。
        Assert.Equal(-1, PlaybackFrameTiming.IndexAtOrBefore(times, -0.5));
        Assert.Equal(-1, PlaybackFrameTiming.IndexAtOrBefore([], 1));
    }

    [Fact]
    public void 沒有時間戳時退回固定間隔()
    {
        // ffprobe 探不到（缺檔、容器怪、逾時）不是致命錯誤：回放必須照播。
        var start = PlaybackFrameTiming.StartIndex([], 3);

        Assert.Equal(-1, start);
        Assert.Equal(0L, PlaybackFrameTiming.ContentMs([], start, 0, 0, 40));
        Assert.Equal(40L, PlaybackFrameTiming.ContentMs([], start, 1, 0, 40));
        Assert.Equal(80L, PlaybackFrameTiming.ContentMs([], start, 2, 40, 40));
    }

    [Fact]
    public void 時間戳用完後以固定間隔續算而不是倒退()
    {
        // 封包數少於解出的影格數（尾端補完、時間戳缺漏）：時刻必須繼續往前，
        // 一旦倒退，多路同步會把它當成嚴重落後而立刻重開解碼，畫面反而抖個不停。
        double[] times = [0, 0.04, 0.08];
        var start = PlaybackFrameTiming.StartIndex(times, 0);

        Assert.Equal(80L, PlaybackFrameTiming.ContentMs(times, start, 2, 40, 40));
        Assert.Equal(120L, PlaybackFrameTiming.ContentMs(times, start, 3, 80, 40));
        Assert.Equal(400L, PlaybackFrameTiming.ContentMs(times, start, 10, 360, 40));
    }
}
