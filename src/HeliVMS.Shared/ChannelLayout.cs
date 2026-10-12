namespace HeliVMS.Shared;

/// <summary>
/// 監看格位配置：把「指定第幾格」的頻道排到對應索引，其餘依原本順序補滿剩下的空位。
/// </summary>
/// <remarks>
/// <para>
/// 監看區的格位由 <c>ChannelManager.ReconcileAsync</c>「自 startIndex 起、依序」指派，
/// 換句話說「頻道在序列中的索引」就等於「顯示在第幾格」。所以「指定格位」只要把序列重排即可，
/// 不需要另外傳一份格位對照表。
/// </para>
/// <para>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試（見 <c>UiWiringContractTests</c>），
/// 而「兩台搶同一格」或「指定的格號超出頻道數」正是只有真的設定過才會踩到的情況。
/// </para>
/// </remarks>
public static class ChannelLayout
{
    /// <summary>監看區最大格數（與主視窗的佈局上限一致：64 路＝8×8）。</summary>
    public const int MaxCells = 64;

    /// <summary>
    /// 顯示格位下拉可選的格數＝授權路數，且不超過監看區最大格數。
    /// </summary>
    /// <remarks>
    /// 授權路數決定「這台機器最多能掛幾個頻道」，格位選項跟著授權走，未授權就不該讓人排格位；
    /// 但就算買到 64 路，監看區也只有 <see cref="MaxCells"/> 格，選到不存在的格子等於沒設，
    /// 故上限封頂。授權為 0（未匯入）時回 0，下拉僅剩「自動」。
    /// </remarks>
    public static int SelectableCells(int licensedCameras)
        => Math.Clamp(licensedCameras, 0, MaxCells);

    /// <summary>
    /// 已被其他頻道佔用的格位集合（0 基，與 <c>DisplayCell</c> 同基準），供下拉停用。
    /// </summary>
    /// <param name="channels">頻道與其格位（未指定為 <see langword="null"/>）。</param>
    /// <param name="exceptId">要排除的頻道 Id（＝正在編輯的那一台）；<see langword="null"/> 表示不排除任何頻道。</param>
    /// <remarks>
    /// 排除自己的原因：正在編輯的頻道當下就佔著某一格，若不排除，它自己的選項會被判定成「被佔用」而反灰，
    /// 使用者會看到目前的值選不到。兩個頻道若因舊資料同佔一格，編輯其一時另一台仍算佔用，維持一格一機。
    /// </remarks>
    public static HashSet<int> TakenCells(IEnumerable<(int Id, int? Cell)> channels, int? exceptId)
    {
        var taken = new HashSet<int>();
        foreach (var (id, cell) in channels)
        {
            if (cell is int c && id != exceptId)
            {
                taken.Add(c);
            }
        }

        return taken;
    }

    /// <summary>
    /// 依每台頻道的目標格位（0 基，null＝未指定）重排，回傳等長的新序列；索引即監看格位。
    /// </summary>
    /// <remarks>
    /// 指定的格位若超出目前頻道數或與他人衝突，該頻道退回「依原順序補空位」，
    /// 不會被丟掉，也不會擠掉別人已佔的格位（先到先得）。
    /// </remarks>
    public static List<T> ReorderByCell<T>(IReadOnlyList<T> channels, Func<T, int?> cellOf)
    {
        var n = channels.Count;
        var result = new T[n];
        var placed = new bool[n];
        var remaining = new List<T>();

        foreach (var channel in channels)
        {
            var cell = cellOf(channel);
            if (cell is int k && k >= 0 && k < n && !placed[k])
            {
                result[k] = channel;
                placed[k] = true;
            }
            else
            {
                remaining.Add(channel);
            }
        }

        var cursor = 0;
        foreach (var channel in remaining)
        {
            while (placed[cursor])
            {
                cursor++;
            }

            result[cursor] = channel;
            placed[cursor] = true;
        }

        return result.ToList();
    }

    /// <summary>
    /// 把索引 <paramref name="from"/> 的元素搬到 <paramref name="to"/>（其餘元素遞補），回傳新序列。
    /// </summary>
    /// <remarks>
    /// 監看牆拖曳調整順序用。採「搬移」而非「交換」：操作員把第 4 台拖到第 1 格，期望的是整排往前遞補，
    /// 而不是把原本第 1 台塞到第 4 格——後者會讓沒被拖到的相機莫名其妙換位。
    /// 索引不合法或原地搬移時原樣回傳（放開的位置可能落在格子外）。
    /// </remarks>
    public static List<T> Move<T>(IReadOnlyList<T> items, int from, int to)
    {
        var list = items.ToList();
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to)
        {
            return list;
        }

        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);
        return list;
    }

    /// <summary>
    /// 新增一個「未指定格位」的頻道時，它會被排到第幾格（0 基）。
    /// </summary>
    /// <remarks>
    /// 用的是與 <see cref="ReorderByCell"/> 完全相同的補位規則（被指定者先佔位、未指定者依序補空位），
    /// 所以回傳值就是「這台新頻道實際上會顯示的格子」。
    /// <para>
    /// 手動新增頻道時據此命名為「攝影機{格號+1}」並把格位寫進 <c>DisplayCell</c>，名稱才會和監看牆上的位置一致；
    /// 又因為建立當下就把格位定住，之後刪掉別台也不會整體往前擠而使名稱與格子對不上。
    /// </para>
    /// </remarks>
    public static int NextFreeCell(IReadOnlyList<int?> displayCells)
    {
        var n = displayCells.Count + 1;
        var occupied = new bool[n];
        var unspecified = 0;

        // 第一趟與 ReorderByCell 相同：只有「合法且尚未被佔」的指定格才算佔位，
        // 重複指定、超出範圍與未指定者都退回自動補位。
        foreach (var cell in displayCells)
        {
            if (cell is int k && k >= 0 && k < n && !occupied[k])
            {
                occupied[k] = true;
            }
            else
            {
                unspecified++;
            }
        }

        // 既有未指定的頻道先補位（新頻道排在它們之後）。
        for (var i = 0; i < unspecified; i++)
        {
            var cursor = 0;
            while (occupied[cursor])
            {
                cursor++;
            }

            occupied[cursor] = true;
        }

        // 新頻道補到第一個仍空著的位。
        var next = 0;
        while (occupied[next])
        {
            next++;
        }

        return next;
    }
}
