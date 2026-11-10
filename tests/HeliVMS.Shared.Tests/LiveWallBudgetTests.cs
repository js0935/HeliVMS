using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 即時監看牆每一格的解碼預算（<see cref="LiveWallBudget"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這張表錯了不會有任何錯誤訊息，只會表現成「大格數時畫面很頓」或「縮過頭看不清車牌」，
/// 所以每個邊界都要釘住。
/// </para>
/// <para>
/// 與回放牆（<see cref="PlaybackWallBudget"/>）刻意不同的一點也要釘住：即時監看永遠解完整影格，
/// 不會切成「只解關鍵影格」——那在現場等於每 2–10 秒才換一張，操作員會直接判定畫面壞了。
/// </para>
/// </remarks>
public class LiveWallBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void 一到四格維持原本的不縮放與十五張上限(int cells)
    {
        // 1–4 格是最常用的比對版面。動手改它換不到什麼，卻會讓既有現場畫面「看起來變了」，
        // 所以預算必須與調整前完全相同。
        var window = LiveWallBudget.For(cells);

        Assert.Equal(0, window.DecodeWidth);
        Assert.Equal(LiveWallBudget.DefaultMaxFramesPerSecond, window.MaxFramesPerSecond);
        Assert.Equal(15, window.MaxFramesPerSecond);
    }

    [Fact]
    public void 格數越多解碼寬度只會變小不會變大()
    {
        // 4 格以下不縮放（0＝原生），所以從 9 格開始比較。
        var previousWidth = int.MaxValue;

        foreach (var cells in new[] { 9, 16, 25, 36, 64 })
        {
            var window = LiveWallBudget.For(cells);
            Assert.True(window.DecodeWidth > 0, $"{cells} 格應該要縮小解碼寬度（實得 {window.DecodeWidth}）");
            Assert.True(window.DecodeWidth <= previousWidth, $"{cells} 格的解碼寬度不該比上一級大");
            previousWidth = window.DecodeWidth;
        }
    }

    [Fact]
    public void 跨級時解碼寬度真的變小()
    {
        // 「不會變大」對一個常數表也成立，所以另外釘住每一級真的更小——
        // 否則有人把整張表填成同一個寬度也測不出來。
        var steps = new[] { 9, 16, 25, 36, 64 }
            .Select(cells => LiveWallBudget.For(cells))
            .ToList();

        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(
                steps[i].DecodeWidth < steps[i - 1].DecodeWidth,
                $"格數跨級時解碼寬度必須更小（第 {i} 級 {steps[i].DecodeWidth} vs 前一級 {steps[i - 1].DecodeWidth}）");
        }
    }

    [Fact]
    public void 格數越多更新率上限只會變低()
    {
        var previousFps = double.MaxValue;

        foreach (var cells in new[] { 4, 9, 16, 25, 36, 64 })
        {
            var window = LiveWallBudget.For(cells);
            Assert.True(window.MaxFramesPerSecond > 0, $"{cells} 格必須有明確的更新率上限");
            Assert.True(
                window.MaxFramesPerSecond <= previousFps,
                $"{cells} 格的更新率上限不該比上一級高（實得 {window.MaxFramesPerSecond}）");
            previousFps = window.MaxFramesPerSecond;
        }

        Assert.True(LiveWallBudget.For(64).MaxFramesPerSecond < LiveWallBudget.For(9).MaxFramesPerSecond);
    }

    [Fact]
    public void 六十四格仍保留可辨識動態的更新率()
    {
        // 這是與回放牆最大的差別：64 格只解關鍵影格的話，每 2–10 秒才換一張，
        // 現場會直接說「畫面不動了」。即時監看寧可縮小，也不能停止更新。
        var window = LiveWallBudget.For(LiveWallBudget.MaxCells);

        Assert.InRange(window.DecodeWidth, 128, 384);
        Assert.InRange(window.MaxFramesPerSecond, 2, 12);
    }

    [Fact]
    public void 超過上限的格數以最大格數計算且零格視為一格()
    {
        Assert.Equal(
            LiveWallBudget.For(LiveWallBudget.MaxCells),
            LiveWallBudget.For(100));

        // 0 或負值不可能出現在正常流程，但一旦出現也不該算出 0 寬（那會讓 ffmpeg 不縮放，
        // 於是 64 格全部用原生解析度解碼，而且沒有任何錯誤）。
        Assert.Equal(LiveWallBudget.For(1), LiveWallBudget.For(0));
        Assert.Equal(LiveWallBudget.For(1), LiveWallBudget.For(-5));
    }

    [Fact]
    public void 硬體解碼只提高更新率不會改變縮放寬度()
    {
        foreach (var cells in new[] { 1, 2, 4, 9, 16, 25, 36, 64 })
        {
            var soft = LiveWallBudget.For(cells, hardwareDecode: false);
            var hard = LiveWallBudget.For(cells, hardwareDecode: true);

            // 縮放寬度與是否有 GPU 無關：顯示需要多少像素是版面決定的。
            Assert.Equal(soft.DecodeWidth, hard.DecodeWidth);
            Assert.True(
                hard.MaxFramesPerSecond >= soft.MaxFramesPerSecond,
                $"{cells} 格：硬解不該讓更新率更低（軟 {soft.MaxFramesPerSecond}／硬 {hard.MaxFramesPerSecond}）");
        }
    }

    [Fact]
    public void AI偵測啟用時不縮小解碼寬度但保留更新率上限()
    {
        // AI 的輸入就是這一張影格的像素。多分割時把它縮到 480 再送模型，等於把每個物件
        // 縮小之後才辨識——遠處的人車就是這樣消失的，而且只有「AI 事件變少了」這個線索。
        foreach (var cells in new[] { 9, 16, 25, 36, 64 })
        {
            foreach (var hardwareDecode in new[] { false, true })
            {
                var budget = LiveWallBudget.For(cells, hardwareDecode);
                var ai = LiveWallBudget.ForDetection(budget);

                Assert.Equal(0, ai.DecodeWidth);
                Assert.Equal(budget.MaxFramesPerSecond, ai.MaxFramesPerSecond);
            }
        }
    }

    [Fact]
    public void AI例外只改寬度不改動本來就不縮的預算()
    {
        // 1–4 格本來就是原生寬度：換算後必須一模一樣，否則「有沒有開 AI」會讓
        // 最常用的比對版面出現兩種行為。
        var native = LiveWallBudget.For(4);

        Assert.Equal(native, LiveWallBudget.ForDetection(native));
        Assert.Equal(LiveWallBudget.Window.Native, LiveWallBudget.ForDetection(LiveWallBudget.Window.Native));
    }

    [Fact]
    public void 沒開AI的頻道仍然要縮小解碼()
    {
        // 這是「AI 例外」最危險的一種寫錯方向：把它寫成無條件套用，就等於全站從此
        // 不再縮小解碼，多分割又回到每一格都用原生解析度——沒有任何錯誤訊息，
        // 只有「大格數時有點頓」。
        foreach (var cells in new[] { 9, 16, 25, 36, 64 })
        {
            var budget = LiveWallBudget.For(cells);

            Assert.Equal(budget, LiveWallBudget.ApplyDetectionRule(budget, aiEnabled: false));
            Assert.Equal(0, LiveWallBudget.ApplyDetectionRule(budget, aiEnabled: true).DecodeWidth);
        }
    }

    [Fact]
    public void 分割選項與回放牆共用同一份()
    {
        // 兩套牆各抄一份清單，遲早出現「回放可以 64 分割、監看只能 36」這種
        // 操作員無法理解的不一致。
        Assert.Same(PlaybackWallBudget.SplitOptions, LiveWallBudget.SplitOptions);
        Assert.Equal([1, 2, 4, 9, 16, 25, 36, 64], LiveWallBudget.SplitOptions);
    }
}
