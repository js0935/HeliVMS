namespace HeliVMS.Recording;

/// <summary>
/// 「這顆碟還能錄多久」的三種狀態。
/// </summary>
/// <remarks>
/// 三態是刻意的：<see cref="Unknown"/>（沒有寫入樣本，推不出來）與
/// <see cref="AtReserve"/>（可用空間已低於保留水位，答案真的是 0）在現場的處置完全相反
/// ——前者要去看錄影有沒有在跑，後者要去清磁碟或調保留空間。
/// 把兩者都印成「0 小時」會讓第一個問題永遠不會被問出來。
/// </remarks>
public enum RecordingCapacityState
{
    /// <summary>最近一個量測窗內沒有錄影寫入：無法估算（顯示為「—」，不要編一個數字）。</summary>
    Unknown,

    /// <summary>可用空間已低於保留水位：剩餘可錄時間真的是 0，錄影隨時會被壓力汰除接手。</summary>
    AtReserve,

    /// <summary>有寫入樣本且高於保留水位：可估算。</summary>
    Known,
}

/// <summary>單一磁碟（或全部磁碟合計）的剩餘可錄時間估算結果。</summary>
/// <param name="State">三態（見 <see cref="RecordingCapacityState"/>）。</param>
/// <param name="Remaining">估算的可錄時間；<see cref="RecordingCapacityState.Unknown"/> 時無意義（<c>Zero</c>）。</param>
/// <param name="FreeBytesAboveReserve">可用空間減去保留水位的部分（負值夾為 0）。</param>
/// <param name="BytesPerHour">量測到的寫入速率（bytes/hour）；0 表示沒有樣本。</param>
public readonly record struct RecordingCapacityEstimate(
    RecordingCapacityState State,
    TimeSpan Remaining,
    long FreeBytesAboveReserve,
    double BytesPerHour)
{
    /// <summary>沒有樣本時的結果（保留 <paramref name="freeAboveReserve"/> 供畫面顯示）。</summary>
    public static RecordingCapacityEstimate Unknown(long freeAboveReserve)
        => new(RecordingCapacityState.Unknown, TimeSpan.Zero, freeAboveReserve, 0);
}

/// <summary>
/// 剩餘可錄時間與容量需求估算（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// <b>用實測寫入量，不用名目碼率。</b> 現場的攝影機不會照設定的碼率送：三段不同來源的錄影
/// 實測同一台可以差到 2 倍以上（`-c:v copy` 複製的是攝影機自己的變動位元率），
/// 而名目碼率算出來的「還能錄 30 天」會在 12 天後變成真的沒有空間。所以輸入一律是
/// 「最近一個量測窗內，這顆碟上實際寫進去的 bytes」。
/// </para>
/// <para>
/// <b>沒有樣本就回未知。</b> 剛裝機、全部頻道停錄、或事件模式還沒觸發時，寫入量是 0；
/// 除以 0 得到無限大，而「無限大」在畫面上就是一句謊。未知要長得像未知（「—」），
/// 不能長得像 0（那會被讀成「磁碟滿了」）。
/// </para>
/// <para>
/// 這是 QCV8001 對照（見 `docs/STORAGE-ALIGNMENT.md` 缺口 1）的落地：參考實作以
/// `GetRecordReservedBytes`（每通道 × 64 × 緩衝長度，下限 1 GiB）估容量需求、以
/// `queryRemainRecTime` 回答剩餘時間，兩者都是估的；我們把第二項換成量測值，
/// 並保留「低於保留空間」這個已經確定的答案。
/// </para>
/// </remarks>
public static class RecordingCapacity
{
    /// <summary>
    /// 速率換算用的最短基準：寫入只涵蓋幾分鐘時不能拿那幾分鐘當分母（一段 5 秒的事件錄影
    /// 會算出天文數字般的速率，畫面顯示「可錄 1 分鐘」）。寧可少報可錄時間。
    /// </summary>
    public static readonly TimeSpan MinimumBasis = TimeSpan.FromMinutes(10);

    /// <summary>把「窗內寫入量」換算成每小時速率；基準或寫入量無效時回 0（＝沒有樣本）。</summary>
    /// <param name="writtenBytes">基準時間內寫入的 bytes。</param>
    /// <param name="basis">基準時間長（<see cref="MeasurementBasis"/> 的輸出，不一定等於量測窗）。</param>
    public static double BytesPerHour(long writtenBytes, TimeSpan basis)
    {
        if (writtenBytes <= 0 || basis <= TimeSpan.Zero)
        {
            return 0;
        }

        return writtenBytes / basis.TotalHours;
    }

    /// <summary>
    /// 決定速率的分母：從「窗內最早的段起點」到現在，夾在
    /// <see cref="MinimumBasis"/> 與量測窗之間。
    /// </summary>
    /// <remarks>
    /// 三種情況都要對：錄影滿一小時（涵蓋＝窗，與除以窗相同）、半小時前才開始錄
    /// （涵蓋半小時，速率才是真的）、只有一段剛寫完的事件錄影（夾到
    /// <see cref="MinimumBasis"/>，避免分母趨近 0）。沒有樣本時回量測窗（速率會是 0，
    /// 這個值不影響結果）。
    /// </remarks>
    /// <param name="earliestStartUtc">窗內最早的段起點；沒有樣本時為 <see langword="null"/>。</param>
    /// <param name="nowUtc">現在。</param>
    /// <param name="window">量測窗（上限）。</param>
    public static TimeSpan MeasurementBasis(DateTime? earliestStartUtc, DateTime nowUtc, TimeSpan window)
    {
        if (window <= TimeSpan.Zero || earliestStartUtc is not { } earliest)
        {
            return window > TimeSpan.Zero ? window : MinimumBasis;
        }

        var span = nowUtc - earliest;
        if (span < MinimumBasis)
        {
            return MinimumBasis;
        }

        return span > window ? window : span;
    }

    /// <summary>
    /// 單一磁碟的剩餘可錄時間。
    /// </summary>
    /// <param name="freeBytes">可用空間。</param>
    /// <param name="reserveBytes">保留水位（<c>recording.min_free_bytes</c>）；壓力汰除只會用到這裡為止。</param>
    /// <param name="writtenBytes">這顆碟在基準時間內寫入的錄影量。</param>
    /// <param name="basis">
    /// 速率的分母；呼叫端要用 <see cref="MeasurementBasis"/> 算（不是直接把量測窗傳來，
    /// 除非確定整個窗都有寫入）。
    /// </param>
    public static RecordingCapacityEstimate Estimate(
        long freeBytes,
        long reserveBytes,
        long writtenBytes,
        TimeSpan basis)
    {
        var freeAboveReserve = Math.Max(0, freeBytes - Math.Max(0, reserveBytes));

        // 低於保留水位是「已經確定」的事實，優先於有沒有樣本：這時錄影接下來就會被汰除，
        // 就算這一小時剛好沒有寫入，答案仍然是 0 而不是未知。
        if (freeAboveReserve <= 0)
        {
            return new RecordingCapacityEstimate(
                RecordingCapacityState.AtReserve,
                TimeSpan.Zero,
                0,
                BytesPerHour(writtenBytes, basis));
        }

        var rate = BytesPerHour(writtenBytes, basis);
        if (rate <= 0)
        {
            return RecordingCapacityEstimate.Unknown(freeAboveReserve);
        }

        return new RecordingCapacityEstimate(
            RecordingCapacityState.Known,
            TimeSpan.FromHours(freeAboveReserve / rate),
            freeAboveReserve,
            rate);
    }

    /// <summary>
    /// 多顆磁碟合計：可用空間合計，速率則是逐顆各自換算後加總。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 加總（而不是取單顆）才對得上「錄影會寫到還有空間的那一顆」這個既有行為
    /// （<c>RecordingStorage.ConfiguredRoots</c> 的輪流寫入）；只要有一顆沒有樣本，
    /// 合計仍然可能算出結果，但畫面必須同時列出逐顆的數字——合計看不出是哪一顆要滿了。
    /// </para>
    /// <para>
    /// 每顆用自己的基準換算再相加（不是把 bytes 全部加起來除以單一基準）：一顆錄了一小時、
    /// 另一顆半小時前才加入時，後者的寫入量只有半小時，除以一小時會低估速率。
    /// </para>
    /// </remarks>
    /// <param name="drives">逐顆磁碟的（可用空間、寫入量、基準時間）。</param>
    /// <param name="reserveBytes">保留水位。</param>
    public static RecordingCapacityEstimate EstimateAll(
        IEnumerable<(long FreeBytes, long WrittenBytes, TimeSpan Basis)> drives,
        long reserveBytes)
    {
        ArgumentNullException.ThrowIfNull(drives);

        long freeAbove = 0;
        double rate = 0;
        var any = false;
        foreach (var drive in drives)
        {
            any = true;
            freeAbove += Math.Max(0, drive.FreeBytes - Math.Max(0, reserveBytes));
            rate += BytesPerHour(drive.WrittenBytes, drive.Basis);
        }

        if (!any)
        {
            return RecordingCapacityEstimate.Unknown(0);
        }

        if (freeAbove <= 0)
        {
            // 全部磁碟都在保留水位以下：與單顆同一個判準（這時「沒地方寫了」是事實）。
            return new RecordingCapacityEstimate(RecordingCapacityState.AtReserve, TimeSpan.Zero, 0, rate);
        }

        if (rate <= 0)
        {
            return RecordingCapacityEstimate.Unknown(freeAbove);
        }

        return new RecordingCapacityEstimate(
            RecordingCapacityState.Known,
            TimeSpan.FromHours(freeAbove / rate),
            freeAbove,
            rate);
    }

    /// <summary>
    /// 把剩餘時間拆成天／時／分（<b>一律向下取整</b>），由畫面自行接單位字串。
    /// </summary>
    /// <remarks>
    /// 向下取整不是美感問題：向上取整會讓「還能錄 23 小時 40 分」顯示成「2 天」，
    /// 而操作員會據此決定要不要今天去擴充磁碟。寧可少報，不要多報。
    /// 未知／0 的判斷在 <see cref="RecordingCapacityState"/>，不在這裡。
    /// </remarks>
    public static (int Days, int Hours, int Minutes) Parts(TimeSpan remaining)
    {
        var total = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        return ((int)Math.Floor(total.TotalDays), total.Hours, total.Minutes);
    }
}
