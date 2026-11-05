using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 顯示幀率閘門。現場症狀：診斷日誌同時出現 <c>read=15/s</c>、<c>drop=4–9/s</c>、<c>shown=5–10/s</c>
/// ——來源 15fps 卻只畫出 5–10fps，因為「距上一張至少 1/上限 秒」把每一批抖動的第二張都丟掉了。
/// </summary>
public class FrameRateGateTests
{
    private const double Cap = 15;

    /// <summary>攝影機的實際形狀：每 133ms 來一批兩張（相距 10ms），平均 15fps。</summary>
    [Fact]
    public void 一批兩張的抖動整批通過()
    {
        var gate = new FrameRateGate();
        var admitted = 0;

        // 20 批 = 40 張 = 2.67 秒；嚴格等間距的舊寫法只會放行約 20 張。
        for (var batch = 0; batch < 20; batch++)
        {
            if (gate.Admit(TimeSpan.FromMilliseconds(batch == 0 ? 0 : 133), Cap))
            {
                admitted++;
            }

            if (gate.Admit(TimeSpan.FromMilliseconds(10), Cap))
            {
                admitted++;
            }
        }

        Assert.Equal(40, admitted);
    }

    [Fact]
    public void 長期平均不會超過上限()
    {
        var gate = new FrameRateGate();
        var admitted = 0;

        // 來源 60fps、上限 15fps：每 16.7ms 一張，跑 60 秒（3600 張）。
        for (var i = 0; i < 3600; i++)
        {
            if (gate.Admit(TimeSpan.FromMilliseconds(16.7), Cap))
            {
                admitted++;
            }
        }

        // 60 秒 × 15fps = 900 張，加上起始的 burst 與量化誤差。
        Assert.InRange(admitted, 890, 930);
    }

    [Fact]
    public void 閒置再久也只累積到一批的量()
    {
        var gate = new FrameRateGate();

        // 串流卡住 10 秒後恢復：不可以一次補上 150 張。
        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            if (gate.Admit(i == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.Zero, Cap))
            {
                admitted++;
            }
        }

        Assert.Equal(2, admitted);
    }

    [Fact]
    public void 慢來源不會被丟棄()
    {
        var gate = new FrameRateGate();
        var admitted = 0;

        for (var i = 0; i < 20; i++)
        {
            if (gate.Admit(TimeSpan.FromMilliseconds(200), Cap))
            {
                admitted++;
            }
        }

        Assert.Equal(20, admitted);
    }

    [Fact]
    public void 第一張一定出畫()
    {
        // 來源只送一張（測試 stub、或剛連上就看到的一格）時必須收得到，
        // 否則頻道會一直停在「連線中」並反覆重連。
        Assert.True(new FrameRateGate().Admit(TimeSpan.Zero, Cap));
        Assert.True(new FrameRateGate().Admit(TimeSpan.Zero, 1));
    }

    [Fact]
    public void 上限為零或負值時不限流()
    {
        var gate = new FrameRateGate();

        for (var i = 0; i < 100; i++)
        {
            Assert.True(gate.Admit(TimeSpan.FromMilliseconds(1), 0));
            Assert.True(gate.Admit(TimeSpan.FromMilliseconds(1), -5));
        }
    }

    [Fact]
    public void 單張的突發量可調整()
    {
        // burst=1 就等同「嚴格等間距」：一批兩張只會過第一張。
        var gate = new FrameRateGate(burstFrames: 1);
        Assert.True(gate.Admit(TimeSpan.FromMilliseconds(133), Cap));
        Assert.False(gate.Admit(TimeSpan.FromMilliseconds(10), Cap));
    }
}
