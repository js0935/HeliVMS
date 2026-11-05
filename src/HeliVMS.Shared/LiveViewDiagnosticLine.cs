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
        string State);

    /// <summary>
    /// 一行：<c>時間 | cells=N | ch103 sub 640x360 shown=14.8/s read=29.9/s drop=15.1/s backlog=120ms age=45ms Streaming</c>
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

            builder.Append(" shown=").Append(Rate(cell.ShownFps)).Append("/s");
            builder.Append(" read=").Append(Rate(cell.ReadPerSecond)).Append("/s");
            builder.Append(" drop=").Append(Rate(cell.DroppedPerSecond)).Append("/s");

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
