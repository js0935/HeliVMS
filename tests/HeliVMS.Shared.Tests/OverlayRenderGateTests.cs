using System;
using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// AI 疊加重畫判斷（<see cref="OverlayRenderGate"/>）的行為契約。
///
/// 背景：影格 15fps，但 AI 結果每 200–800ms 才更新一次。原本每幀都清空並重建整棵
/// 疊加視覺樹，16 格時是純浪費——而「沒有變化就不重建」一旦被破壞，症狀是
/// CPU 上升、介面卡頓，而且沒有任何錯誤訊息。
/// </summary>
public sealed class OverlayRenderGateTests
{
    /// <summary>第一次畫一定要真的畫，否則偵測框永遠不會出現。</summary>
    [Fact]
    public void 第一次一定會重畫()
    {
        var gate = new OverlayRenderGate(4);
        Assert.True(gate.ShouldRender(0, generation: 1, scale: 0.5));
        Assert.True(gate.HasDrawn(0));
    }

    /// <summary>
    /// 重點保險：偵測沒變、縮放沒變就不得重畫。影格每秒 15 次而 AI 只更新幾次，
    /// 這一層就是把其餘那些重畫擋掉的唯一機制。
    /// </summary>
    [Fact]
    public void 偵測與縮放都沒變就不重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 1, scale: 0.5);

        for (var i = 0; i < 200; i++)
        {
            Assert.False(gate.ShouldRender(0, generation: 1, scale: 0.5));
        }
    }

    /// <summary>偵測結果更新就必須重畫，否則框線會停在舊位置。</summary>
    [Fact]
    public void 偵測結果更新後會重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 1, scale: 0.5);

        Assert.True(gate.ShouldRender(0, generation: 2, scale: 0.5));
        Assert.Equal(2, gate.DrawnGeneration(0));
    }

    /// <summary>視窗改大小必須重畫：偵測框位置是依顯示尺寸算的。</summary>
    [Fact]
    public void 顯示縮放改變會重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 1, scale: 0.5);

        Assert.True(gate.ShouldRender(0, generation: 1, scale: 0.25));
    }

    /// <summary>視窗拖曳的浮點雜訊不該觸發重畫，否則拖動視窗邊緣會讓 CPU 飆高。</summary>
    [Fact]
    public void 縮放的浮點雜訊不會觸發重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 1, scale: 0.5);

        Assert.False(gate.ShouldRender(0, generation: 1, scale: 0.5 + 0.00001));
    }

    /// <summary>沒有偵測可畫時，只有狀態變了才需要清空畫面（避免每幀都動一次 Canvas）。</summary>
    [Fact]
    public void 已經是空的不會反覆清空()
    {
        var gate = new OverlayRenderGate(4);
        Assert.True(gate.ShouldClear(0, generation: 1));
        Assert.False(gate.ShouldClear(0, generation: 1));
        Assert.True(gate.ShouldClear(0, generation: 2));
    }

    /// <summary>從「有東西」變成「沒東西」必須真的清一次，否則偵測框會殘留在畫面上。</summary>
    [Fact]
    public void 偵測消失時會清空畫面()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 5, scale: 0.5);

        Assert.True(gate.ShouldClear(0, generation: 6));
    }

    /// <summary>清空之後世代要記在「空」的狀態上，否則下一次有偵測時會被誤判成沒變。</summary>
    [Fact]
    public void 清空後偵測再現會重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldClear(0, generation: 1);
        gate.ShouldClear(0, generation: 2);

        Assert.True(gate.ShouldRender(0, generation: 3, scale: 0.5));
    }

    /// <summary>各格獨立：一格重畫不得影響其他格。</summary>
    [Fact]
    public void 各格互不影響()
    {
        var gate = new OverlayRenderGate(16);
        gate.ShouldRender(0, generation: 1, scale: 0.5);
        gate.ShouldRender(1, generation: 1, scale: 0.5);

        Assert.True(gate.ShouldRender(0, generation: 2, scale: 0.5));
        Assert.False(gate.ShouldRender(1, generation: 1, scale: 0.5));
    }

    /// <summary>重置後一律重畫（監看中斷、佈局重建時呼叫）。</summary>
    [Fact]
    public void 重置後會重新重畫()
    {
        var gate = new OverlayRenderGate(4);
        gate.ShouldRender(0, generation: 7, scale: 0.5);
        Assert.False(gate.ShouldRender(0, generation: 7, scale: 0.5));

        gate.Reset();

        Assert.False(gate.HasDrawn(0));
        Assert.True(gate.ShouldRender(0, generation: 7, scale: 0.5));
    }

    /// <summary>格索引超出範圍必須立刻失敗，否則溢位會靜默寫到別的格。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void 格索引超出範圍會擲出例外(int cell)
    {
        var gate = new OverlayRenderGate(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.ShouldRender(cell, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.ShouldClear(cell, 1));
    }

    /// <summary>格數必須為正，否則是程式錯誤而非可容忍的邊界。</summary>
    [Fact]
    public void 格數必須為正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayRenderGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayRenderGate(-1));
    }
}