namespace HeliVMS.Storage;

/// <summary>
/// 一顆磁碟的健康等級。
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> 與 <see cref="Healthy"/> 必須分開：讀不到 S.M.A.R.T.（不是管理員、
/// 控制器不外露、USB/SAS 橋接）與「硬碟說它很好」在現場的處置完全不同——前者要去看別的地方
/// （硬體監控、事件檢視器），後者才是不用管。把未知印成健康是最糟的一種靜默失效：
/// 畫面一片綠，而硬碟已經在重新配置磁區。
/// </remarks>
public enum DiskHealthLevel
{
    /// <summary>沒有讀到 S.M.A.R.T. 資料：不判斷（顯示「未提供」）。</summary>
    Unknown,

    /// <summary>沒有預測故障、沒有待重新配置磁區、溫度正常。</summary>
    Healthy,

    /// <summary>已經在重新配置磁區或溫度偏高：還能用，但要準備換。</summary>
    Warning,

    /// <summary>控制器預測即將故障，或已有待重新配置磁區：資料正在流失，先救錄影。</summary>
    Failing,
}

/// <summary>S.M.A.R.T. 取樣（欄位為 <see langword="null"/> 代表該項讀不到，不代表正常）。</summary>
/// <param name="PredictFailure">控制器（WMI <c>FailurePredictStatus</c>）的故障預測；<see langword="null"/>＝讀不到。</param>
/// <param name="ReallocatedSectors">已重新配置的磁區數（SMART 屬性 5）。</param>
/// <param name="PendingSectors">待重新配置磁區數（屬性 197）：讀取失敗、等重映射，代表資料已在流失。</param>
/// <param name="TemperatureCelsius">溫度（屬性 194）。</param>
/// <param name="PowerOnHours">累計通電時數（屬性 9）。</param>
public readonly record struct SmartAttributes(
    bool? PredictFailure,
    int? ReallocatedSectors,
    int? PendingSectors,
    int? TemperatureCelsius,
    int? PowerOnHours);

/// <summary>
/// 磁碟健康判斷（純函式）：把 S.M.A.R.T. 取樣變成一個等級，並解析 ATA 的原始屬性區。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要有這一層：<c>DiskPressurePolicy</c> 回答的是「碟滿了」，這一個回答的是「碟要壞了」。
/// 兩者都會讓錄影無聲消失（前者寫不進去、後者讀不出來），但處置完全相反（清檔 vs 換碟）。
/// 錄影碟的失效還特別隱蔽：<c>MediaIntegrityChecker</c> 用 ffprobe 抽查的是<b>解碼</b>結果，
/// 是事後才知道某一格讀不出來；S.M.A.R.T. 是硬碟自己講的早期訊號。
/// </para>
/// <para>
/// 判斷規則刻意保守：任何一項證據成立就往上報，不要求多項同時成立（硬碟不會「有點壞」）；
/// 但讀不到任何證據時一律回 <see cref="DiskHealthLevel.Unknown"/>，不猜。
/// </para>
/// <para>
/// 與 QCV8001 的關係：參考實作有磁碟 SMART 頁（`config/disk/information/smart`，
/// 見 `docs/STORAGE-ALIGNMENT.md` §5.3），但**取樣機制在反編譯語料裡沒有證據**
/// （沒有 WMI 類別、沒有 IOCTL、沒有 WS 命令）。所以這一支不是「照抄」，是照 Windows 上
/// 有文件的做法自建；證據等級寫在文件裡，不要當成已對齊。
/// </para>
/// </remarks>
public static class DiskHealth
{
    /// <summary>溫度告警門檻（°C）。硬碟廠商一般標 60，留 5 度給機箱熱點。</summary>
    public const int WarningTemperatureCelsius = 55;

    /// <summary>ATA SMART 屬性區在 <c>VendorSpecific</c> 裡的起點與長度（規格：12 bytes × 30 項）。</summary>
    private const int AttributeStart = 2;
    private const int AttributeSize = 12;
    private const int AttributeCount = 30;

    private const byte ReallocatedSectorsId = 5;
    private const byte PowerOnHoursId = 9;
    private const byte TemperatureId = 194;
    private const byte PendingSectorsId = 197;

    /// <summary>
    /// 由取樣判斷等級；<paramref name="sample"/> 為 <see langword="null"/> 時回
    /// <see cref="DiskHealthLevel.Unknown"/>。
    /// </summary>
    public static DiskHealthLevel Evaluate(SmartAttributes? sample)
    {
        if (sample is not { } value)
        {
            return DiskHealthLevel.Unknown;
        }

        // 控制器說「即將故障」是最強的訊號，先看它。
        if (value.PredictFailure == true)
        {
            return DiskHealthLevel.Failing;
        }

        if (value.PendingSectors is > 0)
        {
            return DiskHealthLevel.Failing;
        }

        var hasAttributes = value.ReallocatedSectors is not null ||
                            value.PendingSectors is not null ||
                            value.TemperatureCelsius is not null;

        if (!hasAttributes)
        {
            // 只有控制器的「沒有預測故障」可用時，就以它為準；什麼都沒有才是未知。
            return value.PredictFailure == false ? DiskHealthLevel.Healthy : DiskHealthLevel.Unknown;
        }

        if (value.ReallocatedSectors is > 0 ||
            value.TemperatureCelsius >= WarningTemperatureCelsius)
        {
            return DiskHealthLevel.Warning;
        }

        return DiskHealthLevel.Healthy;
    }

    /// <summary>
    /// 多顆磁碟取最嚴重的一顆：任一顆 <see cref="DiskHealthLevel.Failing"/> 就是 Failing，
    /// 其次是 Warning、Healthy；全部未知才是 Unknown。
    /// </summary>
    /// <remarks>
    /// 未知不掩蓋已知：一顆讀不到、另一顆預測故障時回 Failing，不是 Unknown。
    /// </remarks>
    public static DiskHealthLevel Worst(IEnumerable<DiskHealthLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var worst = DiskHealthLevel.Unknown;
        foreach (var level in levels)
        {
            if (Rank(level) > Rank(worst))
            {
                worst = level;
            }
        }

        return worst;
    }

    private static int Rank(DiskHealthLevel level) => level switch
    {
        DiskHealthLevel.Failing => 3,
        DiskHealthLevel.Warning => 2,
        DiskHealthLevel.Healthy => 1,
        _ => 0,
    };

    /// <summary>
    /// 解析 WMI <c>MSStorageDriver_ATAPISmartData.VendorSpecific</c>（512 bytes）中的屬性區。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 版型（ATA 規格）：前 2 bytes 是版本，之後 30 項 × 12 bytes＝(id, flags×2, 目前值, 最差值,
    /// 原始值×6)，原始值為小端。太短或全零一律回 <see langword="null"/>（結構不對時硬解會得到
    /// 看起來很合理的錯誤數字，比讀不到更糟）。
    /// </para>
    /// <para>
    /// 原始值只取前 4 bytes（48-bit 欄位裡高位幾乎都是 0，超過 2^32 的磁區數等於 2 TB 的壞軌，
    /// 那時硬碟早就離線了）；溫度取原始值最低位元組（廠商實作一致的部分）。
    /// </para>
    /// </remarks>
    /// <param name="vendorSpecific">WMI 回傳的屬性區；長度不足或無有效項時回 <see langword="null"/>。</param>
    public static SmartAttributes? ParseAtaSmart(byte[]? vendorSpecific)
    {
        if (vendorSpecific is null || vendorSpecific.Length < AttributeStart + (AttributeCount * AttributeSize))
        {
            return null;
        }

        int? reallocated = null;
        int? powerOnHours = null;
        int? temperature = null;
        int? pending = null;

        for (var i = 0; i < AttributeCount; i++)
        {
            var offset = AttributeStart + (i * AttributeSize);
            var id = vendorSpecific[offset];
            if (id == 0)
            {
                // 0 是空槽（屬性表以 0 結束）；有些控制器整段是 0，全部略過後自然回 null。
                continue;
            }

            // 原始值：小端，最多 4 bytes（見上面說明）。
            var raw = 0;
            for (var b = 3; b >= 0; b--)
            {
                raw = (raw << 8) | vendorSpecific[offset + 8 + b];
            }

            switch (id)
            {
                case ReallocatedSectorsId:
                    reallocated = raw;
                    break;
                case PendingSectorsId:
                    pending = raw;
                    break;
                case TemperatureId:
                    // 原始值最低位元組是現在溫度；其餘位元組是最高/最低紀錄（廠商各自的定義）。
                    temperature = vendorSpecific[offset + 8];
                    break;
                case PowerOnHoursId:
                    powerOnHours = raw;
                    break;
            }
        }

        if (reallocated is null && pending is null && temperature is null && powerOnHours is null)
        {
            return null;
        }

        // PredictFailure 由呼叫端另外從 FailurePredictStatus 併進來（不同 WMI 類別）。
        return new SmartAttributes(null, reallocated, pending, temperature, powerOnHours);
    }

    /// <summary>把控制器的故障預測併進解析出來的屬性（兩者是不同的 WMI 類別）。</summary>
    public static SmartAttributes WithPredictFailure(SmartAttributes attributes, bool? predictFailure)
        => attributes with { PredictFailure = predictFailure };

    /// <summary>i18n 用的等級字串（`Disk.Health*`）。</summary>
    public static string LevelKey(DiskHealthLevel level) => level switch
    {
        DiskHealthLevel.Failing => "Settings.DiskHealthFailing",
        DiskHealthLevel.Warning => "Settings.DiskHealthWarning",
        DiskHealthLevel.Healthy => "Settings.DiskHealthHealthy",
        _ => "Settings.DiskHealthUnknown",
    };
}
