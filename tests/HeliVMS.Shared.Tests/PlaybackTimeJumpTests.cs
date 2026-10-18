using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 回放時間跳轉輸入的解析契約（§8.5）：規格那串完整日期時間要能用，只給時刻也要能用。
/// </summary>
/// <remarks>
/// 這條守住的是「複製貼上失效」：操作員從事件清單複製 2026-09-12 14:30:00 貼進跳轉框，
/// 若只認時刻就會被判無效輸入，跳轉功能等於不存在（而且沒有錯誤訊息指向格式問題）。
/// </remarks>
public sealed class PlaybackTimeJumpTests
{
    [Fact]
    public void 規格的完整日期時間可解析出日期與時刻()
    {
        var parsed = PlaybackTimeJump.TryParse("2026-09-12 14:30:00");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 9, 12), parsed!.Value.Date);
        Assert.Equal(new TimeSpan(14, 30, 0), parsed.Value.Clock);
    }

    [Fact]
    public void 秒可省略()
    {
        var parsed = PlaybackTimeJump.TryParse("2026-09-12 14:30");

        Assert.NotNull(parsed);
        Assert.Equal(new TimeSpan(14, 30, 0), parsed!.Value.Clock);
        Assert.Equal(new DateTime(2026, 9, 12), parsed.Value.Date);
    }

    [Fact]
    public void 只給時刻時不帶日期()
    {
        foreach (var text in new[] { "14:30", "14:30:05", " 14:30 " })
        {
            var parsed = PlaybackTimeJump.TryParse(text);

            Assert.NotNull(parsed);
            Assert.Null(parsed!.Value.Date);
            Assert.True(parsed.Value.Clock < TimeSpan.FromDays(1));
        }

        Assert.Equal(new TimeSpan(14, 30, 0), PlaybackTimeJump.TryParse("14:30")!.Value.Clock);
        Assert.Equal(new TimeSpan(14, 30, 5), PlaybackTimeJump.TryParse("14:30:05")!.Value.Clock);
    }

    /// <summary>只有日期時不猜時刻：猜錯會讓人以為跳到指定時間點。</summary>
    [Fact]
    public void 只有日期或空字串一律視為無效()
    {
        Assert.Null(PlaybackTimeJump.TryParse("2026-09-12"));
        Assert.Null(PlaybackTimeJump.TryParse(""));
        Assert.Null(PlaybackTimeJump.TryParse("   "));
        Assert.Null(PlaybackTimeJump.TryParse(null));
        Assert.Null(PlaybackTimeJump.TryParse("下午三點"));
        Assert.Null(PlaybackTimeJump.TryParse("99:99"));
    }
}
