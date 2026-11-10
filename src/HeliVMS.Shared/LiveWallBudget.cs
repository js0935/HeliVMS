namespace HeliVMS.Shared;

/// <summary>
/// 即時監看牆每一格的解碼預算（依格數調整），最多 64 路。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：<c>RtspClient</c> 原本對每一格都用「來源原生解析度、固定 15fps」解碼。
/// 在 9 格以上的版面，一格在畫面上只有幾百像素寬，卻照 1920（單畫面主流）或 640（次流）
/// 解碼，那張原始影格要先過管線、再被 WPF 用軟體縮小畫進格子——全部只是為了畫縮圖。
/// </para>
/// <para>
/// 與 <see cref="PlaybackWallBudget"/> 的差別（刻意不同，不可合併）：
/// 回放在 36 格以上會切成「只解關鍵影格」的總覽牆，因為回放可以慢慢看清楚；
/// <b>即時監看不行</b>——只解關鍵影格等於每 2–10 秒才換一張，操作員會直接判定
/// 「畫面壞了」。所以這裡只縮尺寸、降更新率，永遠解完整影格。
/// </para>
/// <para>
/// 1–4 格的預算與調整前的行為完全相同（不縮、15fps）：那是最常用的比對版面，
/// 動手改它換不到什麼，卻會讓既有現場畫面「看起來變了」。
/// </para>
/// <para>
/// 抽成純函式的理由與 <see cref="PlaybackWallBudget"/> 相同：這張表錯了不會有任何錯誤訊息，
/// 只會表現成「大格數時畫面很頓」或「縮過頭看不清車牌」。
/// </para>
/// </remarks>
public static class LiveWallBudget
{
    /// <summary>一格的解碼預算。</summary>
    /// <param name="DecodeWidth">解碼後縮到的寬度（0＝原生）。</param>
    /// <param name="MaxFramesPerSecond">顯示上限（0＝不限）。</param>
    public readonly record struct Window(int DecodeWidth, double MaxFramesPerSecond)
    {
        /// <summary>不縮、維持既有 15fps 上限（單格與小格數）。</summary>
        public static Window Native => new(0, DefaultMaxFramesPerSecond);
    }

    /// <summary>支援的最大格數（8×8），與回放牆相同。</summary>
    public const int MaxCells = 64;

    /// <summary>調整前的固定上限；保留為「小格數」的預設值，讓既有行為不變。</summary>
    public const double DefaultMaxFramesPerSecond = 15;

    /// <summary>
    /// 分割選項與回放牆共用同一份來源：兩套牆各自抄一份，遲早出現「回放可以 64 分割、
    /// 監看只能 36」這種操作員無法理解的不一致。
    /// </summary>
    public static IReadOnlyList<int> SplitOptions => PlaybackWallBudget.SplitOptions;

    /// <summary>依格數決定每一格的預算。</summary>
    /// <param name="cells">牆上的總格數，至少 1（0 或負值視為 1）。</param>
    /// <param name="hardwareDecode">本機硬體解碼可用且已啟用。</param>
    public static Window For(int cells, bool hardwareDecode = false)
    {
        if (cells <= 4)
        {
            // 1–4 格：最常用的版面，維持既有行為（不縮、15fps）。
            return Window.Native;
        }

        if (cells <= 9)
        {
            return new Window(640, hardwareDecode ? 20 : DefaultMaxFramesPerSecond);
        }

        if (cells <= 16)
        {
            return new Window(480, hardwareDecode ? 15 : 10);
        }

        if (cells <= 25)
        {
            return new Window(384, hardwareDecode ? 12 : 8);
        }

        if (cells <= 36)
        {
            return new Window(320, hardwareDecode ? 10 : 6);
        }

        return new Window(256, hardwareDecode ? 8 : 4);
    }

    /// <summary>
    /// AI 偵測啟用時的預算：<b>不縮小解碼寬度</b>，只保留更新率上限。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 偵測精度優先於 CPU。AI 的輸入是「這一張影格的像素」：把 640 寬的次碼流縮到 480 再送進
    /// 模型，等於把每一個物件都縮小 25% 之後才辨識——遠處的人車就是這樣消失的，而且不會有任何
    /// 錯誤訊息，只表現成「這台攝影機的 AI 事件變少了」。
    /// </para>
    /// <para>
    /// 回傳原生寬度也正好是「與調整前相同」：AI 頻道維持原本的解碼成本，省下來的 CPU 落在
    /// 佔多數的、沒有跑 AI 的頻道上。
    /// </para>
    /// <para>
    /// 運動與遮蔽偵測不需要這個例外：兩者都先把影格抽樣成 16×16 的亮度格
    /// （<c>FrameDifferenceMotionDetector.DownsampleToGrid</c>），影格解析度本來就不影響判定。
    /// </para>
    /// </remarks>
    /// <param name="window">依格數算出的預算。</param>
    public static Window ForDetection(Window window)
        => window.DecodeWidth > 0 ? window with { DecodeWidth = 0 } : window;

    /// <summary>
    /// 依「這台頻道的 AI 偵測是否啟用」決定真正要交給解碼端的預算。
    /// </summary>
    /// <remarks>
    /// 這個判斷刻意放在純函式裡：它只有兩種結果，但寫在桌面端就沒有執行期測試能釘住，
    /// 而寫錯的方向（永遠當成 AI 已啟用）不會有任何錯誤訊息——它只是讓全站悄悄停止縮小解碼，
    /// 於是多分割又回到每一格都用原生解析度，症狀只有「大格數時有點頓」。
    /// </remarks>
    /// <param name="window">依格數算出的預算。</param>
    /// <param name="aiEnabled">該頻道是否正在跑 AI 偵測。</param>
    public static Window ApplyDetectionRule(Window window, bool aiEnabled)
        => aiEnabled ? ForDetection(window) : window;
}
