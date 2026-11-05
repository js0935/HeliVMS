namespace HeliVMS.Shared;

/// <summary>
/// 多路回放每一格的解碼預算（依格數調整），最多 64 路。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼不能「每一格都用原生解析度、原生幀率」：一格 1080p 的原始影格是 6MB，
/// 而 64 格在畫面上每格只有一百多像素寬。實測（本機 8 核心、同時在錄 6 路的真實條件下）：
/// 64 路原生解析度只能跑到每格約 1fps——畫面看起來像當掉，而且還把錄影的 CPU 搶光。
/// 反過來，把每一格縮到顯示需要的寬度並限制更新率之後，管線、記憶體與 UI 成本等比下降，
/// 換到的是「所有格都有畫面且跟得上」。
/// </para>
/// <para>
/// 這張表是量出來的，不是猜的：單格在 Speed=16 下的吞吐上限（本機）約 100–150 幀/秒，
/// 64 格要同時跑就必須把每格的需求壓到那個上限的 1/64 以下並留餘裕給錄影與即時監看。
/// 硬體解碼可用時（<c>Settings → 硬體解碼</c>）解碼成本移到 GPU，每格可以給更高的更新率。
/// </para>
/// <para>
/// 抽成純函式是為了可測試：這張表錯了不會有任何錯誤訊息，只會表現成「大格數時畫面很頓」。
/// </para>
/// </remarks>
public static class PlaybackWallBudget
{
    /// <summary>一格的解碼預算。</summary>
    /// <param name="DisplayWidth">解碼後縮到的寬度（0＝原生）。</param>
    /// <param name="MaxFramesPerSecond">顯示上限（0＝不限）。</param>
    /// <param name="DecodeThreads">解碼執行緒數（0＝由 ffmpeg 決定）。</param>
    /// <param name="KeyframesOnly">只解關鍵影格（大格數的總覽牆）。</param>
    public readonly record struct Pane(
        int DisplayWidth,
        int MaxFramesPerSecond,
        int DecodeThreads,
        bool KeyframesOnly = false)
    {
        /// <summary>不縮、不限、交由 ffmpeg 決定執行緒（單格與小格數的品質優先模式）。</summary>
        public static Pane Native => new(0, 0, 0);
    }

    /// <summary>支援的最大格數（8×8）。</summary>
    public const int MaxCells = 64;

    /// <summary>依格數決定每一格的預算。</summary>
    /// <param name="cells">牆上的總格數（含主畫面格），至少 1。</param>
    /// <param name="hardwareDecode">本機硬體解碼可用且已啟用。</param>
    public static Pane For(int cells, bool hardwareDecode = false)
    {
        if (cells <= 1)
        {
            // 單格＝品質優先（平台放大到全螢幕看細節）。
            return Pane.Native;
        }

        if (cells <= 4)
        {
            // 2–4 格（現場最常用的比對版面）：縮到 960 之後管線成本少一半以上，
            // 而格子在畫面上只有約 440px 寬，看不出差別——所以不必再限制更新率，
            // 讓畫面跟來源一樣順（限制它只會讓畫面比攝影機原本還頓）。
            return new Pane(960, 0, 0);
        }

        if (cells <= 9)
        {
            return new Pane(640, hardwareDecode ? 25 : 15, 1);
        }

        if (cells <= 16)
        {
            return new Pane(480, hardwareDecode ? 20 : 10, 1);
        }

        if (cells <= 25)
        {
            return new Pane(384, hardwareDecode ? 15 : 6, 1);
        }

        // 36 格以上改成「只解關鍵影格」的總覽牆：實測（8 核心、同時在錄 6 路）36 路以上
        // 解完整串影格時每格只剩 1–2fps，而且把錄影的 CPU 搶光——那比「每幾秒換一張」更糟，
        // 因為連操作員自己都看得出畫面在跳。只解關鍵影格時成本降到 1/GOP，
        // 每一格每 2–4 秒穩定換一張（要看清細節就點那一格放大成單格）。
        return cells <= 36
            ? new Pane(320, 2, 1, KeyframesOnly: true)
            : new Pane(256, 2, 1, KeyframesOnly: true);
    }

    /// <summary>依格數給的建議分割選項（UI 用；也讓「最多 64 路」有單一來源）。</summary>
    public static IReadOnlyList<int> SplitOptions { get; } = [1, 2, 4, 9, 16, 25, 36, 64];
}
