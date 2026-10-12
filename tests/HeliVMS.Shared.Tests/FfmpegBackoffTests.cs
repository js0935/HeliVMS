using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// ffmpeg 重啟退避（<see cref="FfmpegBackoff"/>）的行為契約（§13-2 行程治理）。
///
/// 現場症狀：一台拔掉網路線的攝影機，因為解碼端固定每 5 秒重試，一天製造 17,280 次
/// 無用交握；錄影端雖有退避但實作是自己的另一份，兩邊會各自漂移。這個純函式是兩者
/// 唯一的數字來源。
/// </summary>
public sealed class FfmpegBackoffTests
{
    [Fact]
    public void 第一次失敗等待基準退避()
    {
        Assert.Equal(FfmpegBackoff.BaseDelay, FfmpegBackoff.DelayFor(0));
        Assert.Equal(FfmpegBackoff.BaseDelay, FfmpegBackoff.DelayFor(1));
    }

    [Fact]
    public void 連續失敗時間隔指數成長()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), FfmpegBackoff.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(20), FfmpegBackoff.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(40), FfmpegBackoff.DelayFor(4));
    }

    [Fact]
    public void 退避不超過五分鐘上限()
    {
        Assert.Equal(FfmpegBackoff.MaxDelay, FfmpegBackoff.DelayFor(7));
        Assert.Equal(FfmpegBackoff.MaxDelay, FfmpegBackoff.DelayFor(1_000));
        Assert.Equal(FfmpegBackoff.MaxDelay, FfmpegBackoff.DelayFor(int.MaxValue));
    }

    [Fact]
    public void 可自訂基準與上限()
    {
        var d = FfmpegBackoff.DelayFor(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));

        Assert.Equal(TimeSpan.FromSeconds(8), d);
        Assert.Equal(TimeSpan.FromSeconds(1), FfmpegBackoff.DelayFor(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)));
    }
}
