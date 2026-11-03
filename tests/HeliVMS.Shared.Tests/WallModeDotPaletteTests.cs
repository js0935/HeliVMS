using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看牆「錄影方式指示燈」的色票。圖例是操作員背下來的約定（紅＝全時、綠＝事件…），
/// 所以每一項都要有值、四個顏色互不相同，且未知模式不准猜一個顏色。
/// </summary>
public class WallModeDotPaletteTests
{
    [Theory]
    [InlineData(RecordingMode.Always)]
    [InlineData(RecordingMode.Scheduled)]
    [InlineData(RecordingMode.Event)]
    [InlineData(RecordingMode.Manual)]
    public void 每一種錄影方式都有合法色碼的燈色(RecordingMode mode)
    {
        var hex = WallModeDotPalette.For(mode);

        Assert.NotNull(hex);
        // #RRGGBB；寫成 #RGB 或色名會讓 ColorConverter 在桌面端擲例外（徽章每秒重畫 16 次）。
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
    }

    [Fact]
    public void 四種錄影方式的燈色互不相同()
    {
        var colors = new[]
        {
            WallModeDotPalette.For(RecordingMode.Always),
            WallModeDotPalette.For(RecordingMode.Scheduled),
            WallModeDotPalette.For(RecordingMode.Event),
            WallModeDotPalette.For(RecordingMode.Manual),
        };

        // 撞色＝兩個模式在畫面上分不出來，而這盞燈就是為了分辨模式存在的。
        Assert.Equal(4, colors.Distinct().Count());
    }

    [Fact]
    public void 未知的錄影方式沒有燈色()
    {
        Assert.Null(WallModeDotPalette.For(null));
        // 明碼 99：未來新增模式後，這裡是「還沒決定顏色」的守門員。
        Assert.Null(WallModeDotPalette.For((RecordingMode)99));
    }

    [Fact]
    public void 錄影中全亮沒錄影壓暗但仍看得見()
    {
        Assert.Equal(1.0, WallModeDotPalette.OpacityFor(recording: true));

        var idle = WallModeDotPalette.OpacityFor(recording: false);
        // 兩個極端都不行：0＝看不出是哪一種模式，1＝看不出現在有沒有在錄。
        Assert.True(idle is > 0 and < 1, $"未錄影時的不透明度應介於 0 與 1，實際 {idle}");
    }
}
