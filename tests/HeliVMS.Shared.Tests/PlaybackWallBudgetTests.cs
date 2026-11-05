using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 多路回放每一格的解碼預算（最多 64 路）。這張表錯了不會有任何錯誤訊息，
/// 只會表現成「大格數時畫面很頓」或「錄影被回放搶到掉格」。
/// </summary>
public class PlaybackWallBudgetTests
{
    [Fact]
    public void 單格用原生解析度且不限更新率()
    {
        var pane = PlaybackWallBudget.For(1);

        Assert.Equal(0, pane.DisplayWidth);
        Assert.Equal(0, pane.MaxFramesPerSecond);
        Assert.Equal(0, pane.DecodeThreads);
    }

    [Fact]
    public void 格數越多縮得越小()
    {
        var previousWidth = int.MaxValue;

        foreach (var cells in new[] { 2, 4, 9, 16, 25, 36, 64 })
        {
            var pane = PlaybackWallBudget.For(cells);
            Assert.True(pane.DisplayWidth > 0, $"{cells} 格應該縮放（實得 {pane.DisplayWidth}）");
            Assert.True(pane.DisplayWidth <= previousWidth, $"{cells} 格的縮放寬度不該比上一級大");
            previousWidth = pane.DisplayWidth;
        }

        // 跨級一定要真的變小（同一級內可以相同）：9 格以上每多一級就縮一階。
        var steps = new[] { 4, 9, 16, 25, 36, 64 }.Select(cells => PlaybackWallBudget.For(cells)).ToList();
        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(
                steps[i].DisplayWidth < steps[i - 1].DisplayWidth,
                $"格數跨級時縮放寬度必須下降（第 {i} 級 {steps[i].DisplayWidth} vs 前一級 {steps[i - 1].DisplayWidth}）");
        }
    }

    [Fact]
    public void 格數越多更新率越低()
    {
        // 0＝不限（小格數時讓畫面跟來源一樣順），不能拿它跟有上限的級別比大小。
        Assert.Equal(0, PlaybackWallBudget.For(4).MaxFramesPerSecond);

        var previousFps = double.MaxValue;
        foreach (var cells in new[] { 9, 16, 25 })
        {
            var pane = PlaybackWallBudget.For(cells);
            Assert.True(pane.MaxFramesPerSecond > 0, $"{cells} 格應該有明確的更新率上限");
            Assert.True(
                pane.MaxFramesPerSecond < previousFps,
                $"{cells} 格的更新率必須比上一級低（實得 {pane.MaxFramesPerSecond}）");
            previousFps = pane.MaxFramesPerSecond;
        }

        // 36 格以上改成只解關鍵影格：來源本來就每 GOP（2–4 秒）才出一張，
        // 這裡的上限只是保護 UI 的安全值，不必再逐級下降。
        foreach (var cells in new[] { 36, 64 })
        {
            var pane = PlaybackWallBudget.For(cells);
            Assert.True(pane.KeyframesOnly);
            Assert.InRange(pane.MaxFramesPerSecond, 1, 6);
        }
    }

    [Fact]
    public void 六十四格是最小的預算且仍看得到畫面()
    {
        var pane = PlaybackWallBudget.For(PlaybackWallBudget.MaxCells);

        // 一格在 8×8 的牆上大約只有 110–160 像素寬：256 已經足夠，
        // 而且這是唯一能讓 64 路同時跑起來的設定。
        Assert.InRange(pane.DisplayWidth, 128, 320);
        Assert.InRange(pane.MaxFramesPerSecond, 2, 12);

        // 每一條解碼行程都要限制執行緒：64 路各開 8 條＝512 條在 8 核心上打架。
        Assert.Equal(1, pane.DecodeThreads);

        // 36 格以上是「只解關鍵影格」的總覽牆：實測解完整串影格時每格只剩 1–2fps，
        // 而且把錄影的 CPU 搶光——那比「每幾秒換一張」更糟。
        Assert.True(pane.KeyframesOnly, "64 格必須只解關鍵影格");
    }

    [Fact]
    public void 三十六格以上只用關鍵影格二十五格以下不用()
    {
        Assert.False(PlaybackWallBudget.For(16).KeyframesOnly);
        Assert.False(PlaybackWallBudget.For(25).KeyframesOnly);
        Assert.True(PlaybackWallBudget.For(36).KeyframesOnly);
        Assert.True(PlaybackWallBudget.For(64).KeyframesOnly);
    }

    [Fact]
    public void 超過上限的格數仍回最大預算而不是壞掉()
    {
        Assert.Equal(PlaybackWallBudget.For(PlaybackWallBudget.MaxCells), PlaybackWallBudget.For(100));
        Assert.Equal(PlaybackWallBudget.For(1), PlaybackWallBudget.For(0));
    }

    [Fact]
    public void 硬體解碼只會放寬更新率不會改變縮放()
    {
        foreach (var cells in new[] { 2, 4, 9, 16, 25, 36, 64 })
        {
            var soft = PlaybackWallBudget.For(cells, hardwareDecode: false);
            var hard = PlaybackWallBudget.For(cells, hardwareDecode: true);

            Assert.Equal(soft.DisplayWidth, hard.DisplayWidth);
            Assert.Equal(soft.DecodeThreads, hard.DecodeThreads);
            Assert.True(
                hard.MaxFramesPerSecond >= soft.MaxFramesPerSecond,
                $"{cells} 格：硬體解碼不該讓更新率變低（軟 {soft.MaxFramesPerSecond}／硬 {hard.MaxFramesPerSecond}）");
        }
    }

    [Fact]
    public void 分割選項涵蓋一到六十四路且與格數對應表同步()
    {
        var options = PlaybackWallBudget.SplitOptions;

        Assert.Equal([1, 2, 4, 9, 16, 25, 36, 64], options);
        Assert.Equal(1, options[0]);
        Assert.Equal(PlaybackWallBudget.MaxCells, options[^1]);
        Assert.True(options.Count == options.Distinct().Count(), "分割選項不該重複");
    }

    [Fact]
    public void 四格以內不再限制更新率()
    {
        // 2–4 格是現場最常用的比對版面（格子約 440px）：縮到 960 之後管線成本已經夠低，
        // 限制更新率只會讓畫面比來源還頓。
        Assert.Equal(0, PlaybackWallBudget.For(2).MaxFramesPerSecond);
        Assert.Equal(0, PlaybackWallBudget.For(4).MaxFramesPerSecond);
    }
}
