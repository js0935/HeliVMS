namespace HeliVMS.Shared;

/// <summary>
/// 監看格線排版計算：依格數與可用區域挑選最適的列×欄，並算出「每格維持固定畫面比例、
/// 整塊格線置中」時的外框尺寸。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼抽成純函式：桌面端沒有 WPF 執行期測試（見 UiWiringContractTests 的說明），
/// 排版若留在 <c>MainWindow</c> 的程式碼後置裡就只能靠人眼驗證。這裡是唯一能放行為測試的地方。
/// </para>
/// <para>
/// 舊行為是無條件用正方形佈局（n 路就 n×n），而 <c>UniformGrid</c> 又把整塊撐滿監看區，
/// 於是「每格長寬比」等於「整個監看區的長寬比」。在非 16:9 的視窗或 4:3 的舊攝影機上，
/// 畫面就會被塞進與自己比例不同的格子裡，出現大片黑邊或變形感。
/// 新行為：格子鎖定來源畫面比例，格線置中，只有真的選不到剛好切合的列欄時才留邊。
/// </para>
/// </remarks>
public static class MonitorGrid
{
    /// <summary>挑選結果：列數、欄數，以及在可用區域內置中後的外框寬高。</summary>
    /// <remarks><see cref="Width"/>/<see cref="Height"/> 為 0 代表「還算不出來」（可用區域為 0）。</remarks>
    public readonly record struct Arrangement(int Rows, int Columns, double Width, double Height)
    {
        /// <summary>此排列的格位總數（可能大於要求格數，例如 5 路最緊的排列是 2×3＝6）。</summary>
        public int Capacity => Rows * Columns;
    }

    /// <summary>
    /// 依目標格數與可用區域挑最適列×欄，再算出置中後的外框尺寸。
    /// </summary>
    /// <param name="cells">目標格數（&lt;1 視為 1）。</param>
    /// <param name="areaWidth">可用區域寬（例如監看區邊框的實際寬）。</param>
    /// <param name="areaHeight">可用區域高。</param>
    /// <param name="cellAspect">每格要維持的寬高比（寬÷高），預設 16:9。</param>
    /// <remarks>
    /// 只用「不會多出整排／整列的排列」為候選：欄數 c、列數 r＝⌈cells／c⌉。
    /// 先算出這些候選中最大的「單格可達寬度」，只保留不低於該值 90% 的候選，
    /// 再從中挑最接近正方形者（同則取空格少者、再取列數少者）——這樣可以避免
    /// 為了多 5% 的格子而選出 1×6 這種長條排列。
    /// </remarks>
    public static Arrangement Fit(
        int cells,
        double areaWidth,
        double areaHeight,
        double cellAspect = 16.0 / 9.0)
    {
        if (cells < 1)
        {
            cells = 1;
        }

        if (!(cellAspect > 0) || double.IsNaN(cellAspect) || double.IsInfinity(cellAspect))
        {
            cellAspect = 16.0 / 9.0;
        }

        if (!(areaWidth > 0) || !(areaHeight > 0) ||
            double.IsNaN(areaWidth) || double.IsNaN(areaHeight) ||
            double.IsInfinity(areaWidth) || double.IsInfinity(areaHeight))
        {
            var side = (int)Math.Ceiling(Math.Sqrt(cells));
            return new Arrangement(side, side, 0, 0);
        }

        // 兩段式：先找「單格最大可達寬度」，再只在這條上限的容差內挑最均衡的排列。
        // 若單純取最大格，6 路在很高的直立監看區會選 1×6 這種長條（每格只大 5%），
        // 視覺上反而差；限制在最大值的 BALANCE_TOLERANCE 內再取最接近正方形者。
        const double BalanceTolerance = 0.9;

        var maxCellWidth = double.NegativeInfinity;
        for (var cols = 1; cols <= cells; cols++)
        {
            var rows = (int)Math.Ceiling((double)cells / cols);
            var cellWidth = Math.Min(areaWidth / cols, areaHeight * cellAspect / rows);
            if (cellWidth > maxCellWidth)
            {
                maxCellWidth = cellWidth;
            }
        }

        var threshold = maxCellWidth * BalanceTolerance;
        var bestCols = cells;
        var bestRows = 1;
        var bestEmpty = int.MaxValue;
        var bestSkew = int.MaxValue;

        for (var cols = 1; cols <= cells; cols++)
        {
            var rows = (int)Math.Ceiling((double)cells / cols);

            // 每格可達寬度：同時受可用寬度（areaWidth/cols）與可用高度
            // （搭配固定比例後為 areaHeight*cellAspect/rows）夾限。
            var cellWidth = Math.Min(areaWidth / cols, areaHeight * cellAspect / rows);
            if (cellWidth + 1e-9 < threshold)
            {
                continue;
            }

            var empty = rows * cols - cells;
            var skew = Math.Abs(rows - cols);

            var better =
                skew < bestSkew ||
                (skew == bestSkew &&
                    (empty < bestEmpty ||
                     (empty == bestEmpty && rows < bestRows)));

            if (better)
            {
                bestCols = cols;
                bestRows = rows;
                bestEmpty = empty;
                bestSkew = skew;
            }
        }

        var gridAspect = (double)bestCols / bestRows * cellAspect;
        double width;
        double height;
        if (areaWidth / areaHeight > gridAspect)
        {
            height = areaHeight;
            width = height * gridAspect;
        }
        else
        {
            width = areaWidth;
            height = width / gridAspect;
        }

        return new Arrangement(bestRows, bestCols, width, height);
    }
}
