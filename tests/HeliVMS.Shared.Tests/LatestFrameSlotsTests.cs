using System;
using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看繪圖佇列（<see cref="LatestFrameSlots{T}"/>）的行為契約。
///
/// 症狀背景：4×4 佈局下操作員回報「畫面愈拖愈舊、愈點愈卡，但 CPU 沒滿載」。
/// 原因是每一張解碼影格都無條件排進 Dispatcher 佇列，UI 執行緒產能不足時佇列無界膨脹，
/// 而排在後面的全是過期影格。這裡鎖住「每格最多一張在途、畫的永遠是最新那張」。
/// </summary>
public sealed class LatestFrameSlotsTests
{
    private sealed record Frame(int Seq);

    /// <summary>第一張影格必須要求排程重畫，否則第一個畫面永遠不會出現。</summary>
    [Fact]
    public void 第一張影格會要求排程重畫()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        Assert.True(slots.TrySchedule(0, new Frame(1)));
        Assert.True(slots.IsPending(0));
    }

    /// <summary>
    /// 重點保險：已有一張在途重畫時，後到的影格只能覆蓋，不能再排一次。
    /// 這是不讓佇列無界膨脹的唯一機制。
    /// </summary>
    [Fact]
    public void 已有在途重畫時後到的影格不再排程()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        slots.TrySchedule(0, new Frame(1));

        for (var i = 2; i <= 240; i++)
        {
            Assert.False(slots.TrySchedule(0, new Frame(i)));
        }

        Assert.Equal(239, slots.Dropped);
    }

    /// <summary>
    /// 被丟掉的是舊的那張，畫出來的必須是最新一張。
    /// 這是「畫面愈拖愈舊」的根因：排隊等到的往往已經過期。
    /// </summary>
    [Fact]
    public void 畫出來的是最新一張而不是最舊一張()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        slots.TrySchedule(0, new Frame(1));
        slots.TrySchedule(0, new Frame(2));
        slots.TrySchedule(0, new Frame(3));

        Assert.Equal(3, slots.Take(0)?.Seq);
    }

    /// <summary>取出後該槽回到「沒有待繪製影格」，下一張才可再次排程。</summary>
    [Fact]
    public void 取出之後下一張影格才能再次排程()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        slots.TrySchedule(0, new Frame(1));
        Assert.NotNull(slots.Take(0));

        Assert.False(slots.IsPending(0));
        Assert.True(slots.TrySchedule(0, new Frame(2)));
        Assert.Equal(2, slots.Take(0)?.Seq);
    }

    /// <summary>16 格各自獨立：一格已有在途影格時，其他格仍可各自排程（否則會變成整體節流）。</summary>
    [Fact]
    public void 各格互不影響()
    {
        var slots = new LatestFrameSlots<Frame>(16);
        Assert.True(slots.TrySchedule(0, new Frame(1)));

        for (var i = 1; i < 16; i++)
        {
            Assert.True(slots.TrySchedule(i, new Frame(100 + i)));
        }

        Assert.Equal(16, slots.PendingCount);

        // 槽 0 仍保有最初那張，因為後續沒有再餵它。
        Assert.Equal(1, slots.Take(0)?.Seq);
        Assert.Equal(101, slots.Take(1)?.Seq);
    }

    /// <summary>監看中斷後清空，舊影格不得在新連線建立後被畫出來（否則會看到上一段串流的殘影）。</summary>
    [Fact]
    public void 中斷後清空不會再畫出舊影格()
    {
        var slots = new LatestFrameSlots<Frame>(4);
        slots.TrySchedule(0, new Frame(1));
        slots.TrySchedule(3, new Frame(2));

        slots.Clear();

        Assert.Null(slots.Take(0));
        Assert.Null(slots.Take(3));
        Assert.False(slots.IsPending(0));
    }

    /// <summary>清空後該格可再次排程，避免中斷再連線後該格永遠不會更新。</summary>
    [Fact]
    public void 清空之後該格仍可再次排程()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        slots.TrySchedule(0, new Frame(1));
        slots.Clear();

        Assert.True(slots.TrySchedule(0, new Frame(2)));
    }

    /// <summary>實際繪製次數必須遠小於送進來的影格數——這是「CPU 不忙但畫面LAG」的量測點。</summary>
    [Fact]
    public void 繪製次數不會超過送進來的影格數()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        var sent = 1000;

        for (var i = 0; i < sent; i++)
        {
            if (slots.TrySchedule(i % 1, new Frame(i)))
            {
                slots.Take(0);
            }
        }

        Assert.Equal(1000, slots.Rendered);
        Assert.Equal(0, slots.Dropped);
    }

    /// <summary>
    /// 模擬真實負載：16 格同時餵影格且繪製來不及跟上。
    /// 餵入 5000 次後，仍在途的只有每格一張（16 張），其餘全部被丟棄。
    /// </summary>
    [Fact]
    public void 大量積壓時每格最多只有一個在途重畫()
    {
        const int slotsCount = 16;
        var slots = new LatestFrameSlots<Frame>(slotsCount);

        for (var i = 0; i < 5000; i++)
        {
            slots.TrySchedule(i % slotsCount, new Frame(i));
        }

        Assert.Equal(16, slots.PendingCount);
        Assert.Equal(5000 - 16, slots.Dropped);
        Assert.Equal(0, slots.Rendered);

        var drained = 0;
        for (var i = 0; i < slotsCount; i++)
        {
            if (slots.Take(i) is not null)
            {
                drained++;
            }
        }

        Assert.Equal(16, drained);
        Assert.Equal(16, slots.Rendered);
    }

    /// <summary>槽索引超出範圍必須立刻失敗，否則溢位會靜默寫到別的格。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void 槽索引超出範圍會擲出例外(int slot)
    {
        var slots = new LatestFrameSlots<Frame>(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.TrySchedule(slot, new Frame(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => slots.Take(slot));
    }

    /// <summary>槽數必須為正，否則是程式錯誤而非可容忍的邊界。</summary>
    [Fact]
    public void 槽數必須為正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatestFrameSlots<Frame>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatestFrameSlots<Frame>(-1));
    }

    /// <summary>傳入 null 代表清除該槽，且不應被當成一張需要畫的影格。</summary>
    [Fact]
    public void 傳入null會清除該槽()
    {
        var slots = new LatestFrameSlots<Frame>(1);
        slots.TrySchedule(0, new Frame(1));

        Assert.False(slots.TrySchedule(0, null));
        Assert.False(slots.IsPending(0));
        Assert.Null(slots.Take(0));
    }
}