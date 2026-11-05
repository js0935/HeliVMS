using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 累計影格數 → 每秒率。診斷日誌要靠它回答「現在掉幾格」，而它有一個不明顯的邊界：
/// 重連讓計數器歸零時差值是負的。
/// </summary>
public class LiveFrameRatesTests
{
    private static LiveFrameRates.Counters Counters(long read, long emitted, long dropped)
        => new(read, emitted, dropped);

    private static LiveFrameRates.Rates Rates(double read, double emitted, double dropped)
        => new(read, emitted, dropped);

    [Fact]
    public void 第一次取樣沒有前一筆時全為零()
    {
        var rates = LiveFrameRates.PerSecond(null, Counters(500, 100, 400), TimeSpan.FromSeconds(5));

        Assert.Equal(Rates(0, 0, 0), rates);
    }

    [Fact]
    public void 依實際間隔換算每秒率()
    {
        // 5 秒讀了 50 格、送出 20 格、丟棄 30 格。
        var rates = LiveFrameRates.PerSecond(
            Counters(1000, 500, 500),
            Counters(1050, 520, 530),
            TimeSpan.FromSeconds(5));

        Assert.Equal(10, rates.Read, 3);
        Assert.Equal(4, rates.Emitted, 3);
        Assert.Equal(6, rates.Dropped, 3);
    }

    [Fact]
    public void 重連讓計數器歸零時不會出現負的率()
    {
        // 重連＝新的 ffmpeg 行程，計數器從 0 開始。負的 fps 在日誌上只會被誤讀成
        // 「有影格被退回來」，實際上那一段時間沒有可比較的取樣。
        var rates = LiveFrameRates.PerSecond(
            Counters(9000, 4000, 5000),
            Counters(3, 2, 1),
            TimeSpan.FromSeconds(5));

        Assert.Equal(Rates(0, 0, 0), rates);
    }

    [Fact]
    public void 間隔為零時回零而不是除以零()
    {
        var rates = LiveFrameRates.PerSecond(Counters(1, 1, 0), Counters(9, 5, 4), TimeSpan.Zero);

        Assert.Equal(Rates(0, 0, 0), rates);
    }
}
