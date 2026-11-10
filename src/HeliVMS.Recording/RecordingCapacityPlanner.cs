namespace HeliVMS.Recording;

/// <summary>規劃用的輸入：幾路、每路多少碼率、要留幾天。</summary>
/// <param name="Channels">錄影路數（≥1）。</param>
/// <param name="MegabitsPerSecond">每路平均碼率（Mbps）；現場是變動位元率，取保守（偏大）值。</param>
/// <param name="RetentionDays">保留天數（≥1）。</param>
/// <param name="StreamsPerChannel">
/// 每路同時錄幾條流（預設 1）。現場常見「次流主錄＋事件切主流」是 <b>1</b> 條，
/// 而「主含次都錄」才是 2——這個數字直接乘進需求，填錯就是兩倍誤差。
/// </param>
/// <param name="OverheadFactor">
/// 內容以外的餘裕（索引、對齊、快照、資料庫；預設 1.05）。RAID 的容錯耗損<b>不含</b>在內，
/// 由 <see cref="RecordingCapacityPlanner.DisksRequired"/>／
/// <see cref="RecordingCapacityPlanner.Evaluate"/> 單獨算。
/// </param>
public readonly record struct CapacityPlanRequest(
    int Channels,
    double MegabitsPerSecond,
    int RetentionDays,
    int StreamsPerChannel = 1,
    double OverheadFactor = RecordingCapacityPlanner.DefaultOverheadFactor)
{
    /// <summary>輸入是否可用（任一項不合理就別算，回一個看起來很精確的錯數字）。</summary>
    public bool IsValid =>
        Channels >= 1 &&
        MegabitsPerSecond > 0 &&
        RetentionDays >= 1 &&
        StreamsPerChannel >= 1 &&
        OverheadFactor >= 1;
}

/// <summary>規劃結果。</summary>
/// <param name="ContentBytes">純錄影內容（不含餘裕）。</param>
/// <param name="RequiredBytes">含餘裕的需求容量（＝要買的可用空間）。</param>
/// <param name="RequiredTb">同上，以 TB（10^12 bytes）表示——與 §11.2 的算式同一口徑。</param>
/// <param name="PerDayTb">每天寫入量（TB）。</param>
public readonly record struct CapacityPlan(
    long ContentBytes,
    long RequiredBytes,
    double RequiredTb,
    double PerDayTb);

/// <summary>RAID 建議。</summary>
/// <param name="DataDisks">放資料的碟數。</param>
/// <param name="TotalDisks">含容錯碟的總數。</param>
/// <param name="UsableBytes">這個組合實際可用的容量。</param>
/// <param name="Shortfall">可用容量 <b>不足</b>需求（買得不夠，畫面必須說出來）。</param>
public readonly record struct RaidDiskPlan(
    int DataDisks,
    int TotalDisks,
    long UsableBytes,
    bool Shortfall);

/// <summary>容錯等級（決定要幾顆容錯碟）。</summary>
public enum RaidLevel
{
    /// <summary>不做容錯（單碟或 JBOD）：任何一顆壞掉就少一段歷史。</summary>
    None,

    /// <summary>鏡像（RAID 1／10）：資料碟每一顆都要一顆對應。</summary>
    Mirror,

    /// <summary>RAID 5：一顆容錯碟，最少 2 顆資料碟。</summary>
    Raid5,

    /// <summary>RAID 6：兩顆容錯碟，最少 3 顆資料碟。</summary>
    Raid6,
}

/// <summary>
/// 容量規劃（純函式）：給「還沒裝機」的場合算「這樣的錄影條件需要多大的碟」。
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="RecordingCapacity"/>（回答「這台已經在錄，還撐多久」，用<b>實測</b>寫入量）
/// 是同一件事的兩面：這一支用<b>輸入的碼率</b>，因為還沒有機器可以量。兩者刻意分開，
/// 免得有人把規劃用的假設碼率拿去當現場估算，或反過來。
/// </para>
/// <para>
/// 算式與 `docs/ARCHITECTURE.md` §11.2 一致：
/// <c>bytes = Mbps / 8 × 86400 × 路數 × 天數 × 流數 × 餘裕</c>。
/// 文件上原本寫的範例結果（5 Mbps、32 路、30 天 ≈ 518 TB）<b>差了 10 倍</b>——同一個算式
/// 算出來是 51.84 TB。買錯一個數量級比算不出來更貴，所以那個數字由測試釘住。
/// </para>
/// <para>
/// 刻意不做的事：不猜「主流 4M／次流 1M」之類的預設值，也不自動幫使用者選 RAID 等級——
/// 兩者都會讓一個憑空生出來的數字看起來像量測結果。
/// </para>
/// </remarks>
public static class RecordingCapacityPlanner
{
    /// <summary>內容以外的預設餘裕（索引、檔案系統對齊、快照與資料庫）。</summary>
    public const double DefaultOverheadFactor = 1.05;

    private const double SecondsPerDay = 86400;

    /// <summary>依輸入算出所需容量；輸入不合理時回 <see langword="null"/>（不要編一個數字）。</summary>
    public static CapacityPlan? Plan(CapacityPlanRequest request)
    {
        if (!request.IsValid)
        {
            return null;
        }

        // Mbps → bytes/s：1 Mbit = 10^6 bit（與 §11.2 同一口徑，不是 2^20）。
        var bytesPerSecond = request.MegabitsPerSecond * 1_000_000d / 8d;
        var perChannelPerDay = bytesPerSecond * SecondsPerDay;
        var content = perChannelPerDay * request.Channels * request.StreamsPerChannel * request.RetentionDays;
        var required = content * request.OverheadFactor;

        return new CapacityPlan(
            ContentBytes: (long)Math.Ceiling(content),
            RequiredBytes: (long)Math.Ceiling(required),
            RequiredTb: required / 1_000_000_000_000d,
            PerDayTb: perChannelPerDay * request.Channels * request.StreamsPerChannel / 1_000_000_000_000d);
    }

    /// <summary>
    /// 需要幾顆碟（含容錯碟）才放得下需求容量。
    /// </summary>
    /// <param name="requiredBytes">要買的可用容量（<see cref="CapacityPlan.RequiredBytes"/>）。</param>
    /// <param name="diskSizeBytes">單顆碟的<b>實際</b>容量（標示容量買到手會少一些）。</param>
    /// <param name="level">容錯等級。</param>
    public static (int DataDisks, int TotalDisks)? DisksRequired(
        long requiredBytes,
        long diskSizeBytes,
        RaidLevel level)
    {
        if (requiredBytes <= 0 || diskSizeBytes <= 0)
        {
            return null;
        }

        var dataDisks = DataDisksFor(requiredBytes, diskSizeBytes, level);
        return (dataDisks, dataDisks + ParityDisks(level, dataDisks));
    }

    /// <summary>
    /// 手上已經有 <paramref name="availableDisks"/> 顆同容量碟時，可用容量夠不夠。
    /// </summary>
    /// <remarks>
    /// 「需要幾顆」與「現有的夠不夠」是兩個問題：前者是採購清單，後者是「要不要再買」。
    /// 只回前一項會讓已經有碟的人自己拿計算機敲 RAID 幾何（而 RAID 耗損正是最常被忘掉的一項）。
    /// </remarks>
    public static RaidDiskPlan? Evaluate(
        long requiredBytes,
        long diskSizeBytes,
        int availableDisks,
        RaidLevel level)
    {
        if (requiredBytes <= 0 || diskSizeBytes <= 0 || availableDisks <= 0)
        {
            return null;
        }

        var needed = DisksRequired(requiredBytes, diskSizeBytes, level);
        if (needed is not { } need)
        {
            return null;
        }

        // 手上的碟能放幾顆「資料碟」：容錯碟優先保留（先扣掉才談可用容量）。
        var dataDisks = level switch
        {
            RaidLevel.Mirror => availableDisks / 2,
            RaidLevel.Raid5 => Math.Max(0, availableDisks - 1),
            RaidLevel.Raid6 => Math.Max(0, availableDisks - 2),
            _ => availableDisks,
        };

        var usable = (long)dataDisks * diskSizeBytes;
        return new RaidDiskPlan(
            DataDisks: need.DataDisks,
            TotalDisks: need.TotalDisks,
            UsableBytes: usable,
            Shortfall: usable < requiredBytes);
    }

    /// <summary>資料碟需要幾顆（不含容錯碟）。</summary>
    private static int DataDisksFor(long requiredBytes, long diskSizeBytes, RaidLevel level)
    {
        var dataDisks = (int)Math.Ceiling(requiredBytes / (double)diskSizeBytes);
        return level switch
        {
            // RAID 5 至少要 2 顆資料碟才有容錯意義（1 顆等於鏡像）；RAID 6 至少 3 顆。
            RaidLevel.Raid5 => Math.Max(2, dataDisks),
            RaidLevel.Raid6 => Math.Max(3, dataDisks),
            _ => dataDisks,
        };
    }

    /// <summary>容錯碟數：鏡像與資料碟一對一，RAID 5 一顆，RAID 6 兩顆，不做容錯則零。</summary>
    private static int ParityDisks(RaidLevel level, int dataDisks) => level switch
    {
        RaidLevel.Mirror => dataDisks,
        RaidLevel.Raid5 => 1,
        RaidLevel.Raid6 => 2,
        _ => 0,
    };
}
