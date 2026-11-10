using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 第二螢幕鏡像牆的格數決策（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 鏡像牆把主牆已解碼的影格<b>轉發</b>到第二螢幕，所以格數的成本不在解碼（那是一片空白），
/// 而在 WPF 貼圖：每格都是獨立的一張 <c>WriteableBitmap</c>。1080p 的第二螢幕切成 25 格時每格
/// 只剩約 380×215，把主牆 64 格那套「小到看不見也照開」帶過來只會讓貼圖成本壓過畫面價值，
/// 因此這裡獨立畫一條 <see cref="MaxCells"/>＝16：夠用（16 格時每格約 480×270，與主牆 16 格同級），
/// 也不會讓操作員在第二螢幕上得到一格比一格還不如的縮圖。
/// </para>
/// <para>
/// 選項清單一律取自 <see cref="PlaybackWallBudget.SplitOptions"/>，UI 不得自己再抄一份
/// （主牆與回放牆已經因為兩份清單漂移吃過一次「回放能 64 分割、監看只能 36」的操作員困惑）。
/// </para>
/// <para>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試，而「儲存值壞掉／不在清單裡」
/// 正是升過版、改過選單才會踩到的地方——那時 <c>UniformGrid</c> 會用一個不存在的列×欄
/// 排版，畫面看起來像壞掉而不是像設定跑掉。
/// </para>
/// </remarks>
public static class MirrorWallBudget
{
    /// <summary>沒有儲存值、或儲存值無效時的格數（MVP 的 2×2，也是操作員最常用的版面）。</summary>
    public const int DefaultCells = 4;

    /// <summary>鏡像牆的最大格數。超過這個數字，每格的貼圖成本換不到可看的畫面。</summary>
    public const int MaxCells = 16;

    /// <summary>可選的格數（依序）：1／2／4／9／16。</summary>
    public static IReadOnlyList<int> SplitOptions { get; } =
        PlaybackWallBudget.SplitOptions.Where(cells => cells <= MaxCells).ToArray();

    /// <summary>
    /// 把任意格數夾回合法選項：不是選項之一（0、負值、7、25…）時回
    /// <see cref="DefaultCells"/>，而不是讓 <c>UniformGrid</c> 排出一個不存在的版面。
    /// </summary>
    public static int Clamp(int cells) => SplitOptions.Contains(cells) ? cells : DefaultCells;

    /// <summary>
    /// 還原儲存的格數：讀不懂或不在清單裡時回 <paramref name="fallback"/>
    /// （fallback 本身也必須是合法選項，否則退回 <see cref="DefaultCells"/>）。
    /// </summary>
    public static int Resolve(string? stored, int fallback = DefaultCells)
    {
        var safeFallback = Clamp(fallback);
        return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Clamp(value)
            : safeFallback;
    }

    /// <summary>
    /// 格數 → <c>UniformGrid</c> 的列×欄。正方形優先（2×2、3×3、4×4），
    /// 2 格則用 1 列×2 欄（並排）——上下兩格在 16:9 螢幕上會變成又扁又寬的兩條。
    /// </summary>
    public static (int Rows, int Columns) GridOf(int cells)
    {
        var count = Clamp(cells);
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);
        return (rows, columns);
    }
}
