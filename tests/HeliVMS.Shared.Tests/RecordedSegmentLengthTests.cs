using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 段長的唯一換算口徑。現場索引裡 68 段有 15 段與檔案實際長度差超過 60 秒，
/// 最壞的兩段記著 6853／6849 秒（檔案只有 250／260 秒），原因是牆鐘時間被 Windows
/// 睡眠與校時拉長。這個類別的每一條測試都在釘住「檔案長度優先」這件事。
/// </summary>
public class RecordedSegmentLengthTests
{
    [Fact]
    public void 探測到的長度優先於牆鐘長度()
    {
        // 典型情況：暖機讓牆鐘多出 0.4 秒。索引要的是影片真的有幾秒。
        Assert.Equal(45.30, RecordedSegmentLength.Resolve(45.30, 45.71), 6);
    }

    [Fact]
    public void 牆鐘膨脹幾千秒時仍採用檔案長度()
    {
        // 實測的極端值：seg-004219933.mp4 的索引寫 6853 秒，檔案只有 250 秒。
        Assert.Equal(250.0, RecordedSegmentLength.Resolve(250.0, 6853.61), 6);
    }

    [Fact]
    public void 探測不到時退回牆鐘長度()
    {
        // ffprobe 不在 PATH（精簡安裝）或檔案毀損：收尾不能因此失敗，
        // 只能回到舊口徑——比正確值差，但比把一段寫完的錄影丢掉好。
        Assert.Equal(60.4, RecordedSegmentLength.Resolve(null, 60.4), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 不合理的探測值一律退回牆鐘長度(double probed)
    {
        // 0 秒是「容器索引還沒寫好」的常見回報；NaN／Infinity 是解析壞掉。
        Assert.Equal(12.0, RecordedSegmentLength.Resolve(probed, 12.0), 6);
    }

    [Fact]
    public void 超過一天的探測值視為讀錯檔案()
    {
        // 一段錄影不會超過 24 小時；讀到更大的數字代表探到了別的檔案或壞掉的索引。
        Assert.Equal(60.0, RecordedSegmentLength.Resolve(
            RecordedSegmentLength.MaxPlausibleSeconds + 1, 60.0), 6);
        Assert.Equal(RecordedSegmentLength.MaxPlausibleSeconds, RecordedSegmentLength.Resolve(
            RecordedSegmentLength.MaxPlausibleSeconds, 60.0), 6);
    }

    [Fact]
    public void 兩個數字都不能用時記零而不是負值()
    {
        // 時鐘被往回調時牆鐘差是負的。負長度會讓時間軸色塊反向，整條時間軸跟著錯。
        Assert.Equal(0, RecordedSegmentLength.Resolve(null, -5));
        Assert.Equal(0, RecordedSegmentLength.Resolve(null, double.NaN));
    }
}
