using System.Globalization;
using System.Text;

namespace HeliVMS.Shared;

/// <summary>
/// 多路回放的診斷取樣（一行一字串，供 <c>%TEMP%\helivms-playback.log</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 現場說「多路回放不順」時，可觀察的只有畫面；而可能的成因有四個，處理方式完全不同：
/// ①每一格落後母鐘多少（對時是否失效）、②這一格是不是根本沒在更新（解碼卡住／沒有錄影）、
/// ③重開解碼的次數（對時容差太小＝每 150ms 重開一次，CPU 全花在啟動行程）、
/// ④現在生效的對時容差（36 格以上是 30 秒的總覽模式，0.4 秒的期待本來就不適用）。
/// </para>
/// <para>
/// 抽成純函式是為了可測試：這行字是事後唯一的證據，欄位少一個就等於沒有。
/// </para>
/// </remarks>
public static class PlaybackPaneDiagnosticLine
{
    /// <summary>一格（一個播放中的畫面格）的取樣值。</summary>
    /// <param name="ChannelId">頻道識別碼。</param>
    /// <param name="Stream">碼流標籤（錄影一律是 main，但保留欄位讓不同來源可分）。</param>
    /// <param name="Width">解碼後的影格寬度（縮放後）。</param>
    /// <param name="Height">解碼後的影格高度。</param>
    /// <param name="KeyframesOnly">這一格是否只解關鍵影格（總覽牆）。</param>
    /// <param name="ShownFps">實際顯示的更新率。</param>
    /// <param name="DriftSeconds">與母鐘的差（秒，正＝超前）；沒有在播時為 <c>null</c>。</param>
    /// <param name="Restarts">這一格重開解碼的次數（對時跳轉、換段）。</param>
    /// <param name="SinceLastFrame">距這一格最後一張影格多久（＝這一格還有沒有在更新）。</param>
    /// <param name="State">playing／gap／done／stopped。</param>
    public readonly record struct PaneSample(
        int ChannelId,
        string Stream,
        int Width,
        int Height,
        bool KeyframesOnly,
        double ShownFps,
        double? DriftSeconds,
        int Restarts,
        TimeSpan SinceLastFrame,
        string State);

    /// <summary>
    /// 一行：<c>時間 | cells=4 tol=0.4s | ch103 main 960x540 shown=14.9/s drift=+0.1s restarts=0 frame=40ms playing | …</c>
    /// </summary>
    public static string Format(
        DateTime utcNow,
        int cells,
        double toleranceSeconds,
        IReadOnlyList<PaneSample> panes)
    {
        ArgumentNullException.ThrowIfNull(panes);

        var builder = new StringBuilder(160);
        builder.Append(utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        builder.Append(" | cells=").Append(cells.ToString(CultureInfo.InvariantCulture));
        builder.Append(" tol=").Append(toleranceSeconds.ToString("0.#", CultureInfo.InvariantCulture)).Append('s');

        foreach (var pane in panes)
        {
            builder.Append(" | ch").Append(pane.ChannelId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(pane.Stream))
            {
                builder.Append(' ').Append(pane.Stream);
            }

            if (pane.Width > 0 && pane.Height > 0)
            {
                builder.Append(' ').Append(pane.Width.ToString(CultureInfo.InvariantCulture))
                    .Append('x').Append(pane.Height.ToString(CultureInfo.InvariantCulture));
            }

            // kf＝只解關鍵影格（大格數總覽牆）。少了這個標記，讀日誌的人會以為
            // 「每格每幾秒才換一張」是壞掉，而它其實是那個版面的設計。
            if (pane.KeyframesOnly)
            {
                builder.Append(" kf");
            }

            builder.Append(" shown=").Append(pane.ShownFps.ToString("0.#", CultureInfo.InvariantCulture)).Append("/s");

            // 沒有在播就沒有漂移可言：寫 0 會被讀成「完全同步」。
            if (pane.DriftSeconds is { } drift)
            {
                builder.Append(" drift=").Append(drift.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)).Append('s');
            }

            builder.Append(" restarts=").Append(pane.Restarts.ToString(CultureInfo.InvariantCulture));
            builder.Append(" frame=").Append(Math.Round(Math.Max(0, pane.SinceLastFrame.TotalMilliseconds)).ToString("0", CultureInfo.InvariantCulture)).Append("ms");
            builder.Append(' ').Append(string.IsNullOrWhiteSpace(pane.State) ? "unknown" : pane.State);
        }

        return builder.ToString();
    }
}
