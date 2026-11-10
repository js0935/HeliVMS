using System.Globalization;
using System.Text;

namespace HeliVMS.Shared;

/// <summary>
/// 即時監看的診斷取樣（一行一字串，供 <c>%TEMP%\helivms-live.log</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報「畫面會延遲」時，過去沒有任何數字可以分辨三種完全不同的原因：
/// ①攝影機或網路本身送得慢（本機無能為力）；②本機解碼管線積了影格（先讀進來再丟，
/// 才不會愈拖愈舊）；③UI 佇列積了影格（畫面比解碼進度還舊）。三者都只表現成「延遲」，
/// 而處理方式完全不同。
/// </para>
/// <para>
/// 抽成純函式是為了可測試：這行字是事後唯一能重建現場的證據，欄位少一個或格式跑掉，
/// 就等於沒有這份日誌——而它偏偏只在出問題的機器上才會被讀。
/// </para>
/// </remarks>
public static class LiveViewDiagnosticLine
{
    /// <summary>一格（一個顯示中的頻道）的取樣值。</summary>
    /// <param name="ChannelId">頻道識別碼。</param>
    /// <param name="StreamLabel">碼流標籤（sub／main）。</param>
    /// <param name="Width">影格寬度（0＝還沒有影格）。</param>
    /// <param name="Height">影格高度。</param>
    /// <param name="ShownFps">畫到螢幕上的更新率。</param>
    /// <param name="ReadPerSecond">每秒從解碼管線讀到的影格數（含被丟棄的）。</param>
    /// <param name="EmittedPerSecond">每秒送出的影格數。</param>
    /// <param name="DroppedPerSecond">每秒因顯示上限丟棄的影格數。</param>
    /// <param name="StreamBacklog">本機管線落後量（null＝量不到）。</param>
    /// <param name="Age">畫面年齡（UI 佇列）。</param>
    /// <param name="State">連線狀態。</param>
    /// <param name="CapPerSecond">生效中的顯示上限（0＝不限）；用來看出「上限沒生效」。</param>
    /// <param name="HwAccel">
    /// 這一輪實際生效的加速方式（<c>null</c>＝軟解）。必須是「實際生效」而不是設定值：
    /// <c>auto</c> 會被解析成明確的方法名、原生尺寸時刻意不啟用、失敗冷卻期內也會退回軟解。
    /// 沒有這一欄，現場「明明設了硬解卻還是吃滿 CPU」只能靠猜。
    /// </param>
    public readonly record struct CellSample(
        int ChannelId,
        string StreamLabel,
        int Width,
        int Height,
        double ShownFps,
        double ReadPerSecond,
        double EmittedPerSecond,
        double DroppedPerSecond,
        TimeSpan? StreamBacklog,
        TimeSpan Age,
        string State,
        double CapPerSecond = 0,
        string? HwAccel = null);

    /// <summary>
    /// 一行：<c>時間 | cells=N | ch103 sub 640x360 hw=d3d11va shown=14.8/s read=29.9/s drop=15.1/s backlog=120ms age=45ms Streaming</c>
    /// </summary>
    public static string Format(DateTime utcNow, IReadOnlyList<CellSample> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var builder = new StringBuilder(96);
        builder.Append(utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        builder.Append(" | cells=").Append(cells.Count);

        foreach (var cell in cells)
        {
            builder.Append(" | ch").Append(cell.ChannelId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(cell.StreamLabel))
            {
                builder.Append(' ').Append(cell.StreamLabel);
            }

            if (cell.Width > 0 && cell.Height > 0)
            {
                builder.Append(' ').Append(cell.Width.ToString(CultureInfo.InvariantCulture))
                    .Append('x').Append(cell.Height.ToString(CultureInfo.InvariantCulture));
            }

            // 硬解一直寫（軟解寫 hw=off），不寫「沒帶就整欄省略」：省略與「這台舊版沒有這一欄」
            // 在日誌上長得一樣，而這一欄正是分辨「設了硬解卻沒生效」的唯一線索。
            // 硬解一直寫（軟解寫 hw=off），不寫「沒帶就整欄省略」：省略與「這台舊版沒有這一欄」
            // 在日誌上長得一樣，而這一欄正是分辨「設了硬解卻沒生效」的唯一線索。
            builder.Append(" hw=").Append(
                string.IsNullOrWhiteSpace(cell.HwAccel) ? "off" : cell.HwAccel);

            builder.Append(" shown=").Append(Rate(cell.ShownFps)).Append("/s");
            builder.Append(" read=").Append(Rate(cell.ReadPerSecond)).Append("/s");
            builder.Append(" emit=").Append(Rate(cell.EmittedPerSecond)).Append("/s");
            builder.Append(" drop=").Append(Rate(cell.DroppedPerSecond)).Append("/s");

            // 上限也要寫出來：`read=30/s drop=0/s cap=15` 這種自相矛盾的組合，唯一的線索
            // 就是「上限寫著 15 卻什麼都沒丟」——沒有這一欄，讀日誌的人只會以為來源是 30fps
            // 而畫面剛好只畫得動 11fps。
            if (cell.CapPerSecond > 0)
            {
                builder.Append(" cap=").Append(Rate(cell.CapPerSecond)).Append("/s");
            }

            // 沒有落後值就整欄不寫：寫成 0ms 會被讀成「完全沒有落後」，那是騙人的。
            if (cell.StreamBacklog is { } backlog)
            {
                builder.Append(" backlog=").Append(Milliseconds(backlog)).Append("ms");
            }

            builder.Append(" age=").Append(Milliseconds(cell.Age)).Append("ms");
            builder.Append(' ').Append(string.IsNullOrWhiteSpace(cell.State) ? "Unknown" : cell.State);
        }

        return builder.ToString();
    }

    private static string Rate(double value)
        => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan value)
        => Math.Round(Math.Max(0, value.TotalMilliseconds)).ToString("0", CultureInfo.InvariantCulture);
}
