namespace HeliVMS.Alarms;

/// <summary>分析偵測輸入（中心點正規化 0..1）（M52，§14.7 #6）。</summary>
public sealed record AnalyticsDetection(string Class, double X, double Y, float Confidence = 1f, string? TrackId = null);

/// <summary>分析情境評估結果（M52，§14.7 #6）。</summary>
public sealed record AnalyticsResult(
    string Module,
    string EventType,
    int ZoneId,
    string ZoneName,
    int ChannelId,
    string? TrackId,
    int Count,
    string Detail);

/// <summary>
/// 模組化分析情境外掛縫（M290，§5.6）：單一情境（跨線／侵入／聚集／徘徊／靜止／車流／熱區／尾隨）
/// 實作本介面，掛在 <see cref="AnalyticsZoneEvaluator"/> 之後，共用同一事件引擎（<see cref="AnalyticsEventEngine"/>）。
///
/// 之所以要這道縫：原本的評估器是一個對模組字串做 <c>switch</c> 的巨型類別，新增情境必須改動
/// 共用類別並共用跨幀狀態；介面化後每個情境自帶狀態、可獨立測試、可依授權位元個別抽換或隱藏
/// （§5.6「每個模組單獨販售位元」）。
/// </summary>
public interface IAnalyticsModule
{
    /// <summary>模組種類識別碼，對應 <c>AnalyticsModuleKinds</c> 常數。</summary>
    string Module { get; }

    /// <summary>此模組輸出的事件類型；純累計（如熱區圖）為 null。</summary>
    string? EventType => AnalyticsModuleCatalog.For(Module)?.EventType;

    /// <summary>此模組對應的授權旗標（§19.3 功能矩陣），供呼叫端決定是否顯示／執行。</summary>
    string LicenseFeature => AnalyticsModuleCatalog.For(Module)?.LicenseFeature ?? string.Empty;

    /// <summary>清空跨幀狀態；換頻道或重新載入分析區時呼叫。</summary>
    void Reset();

    /// <summary>
    /// 以單一幀的偵測結果評估屬於本模組的分析區，把命中事件附加到 <paramref name="results"/>。
    /// <paramref name="zones"/> 僅含模組比對相符且已啟用的分析區。
    /// </summary>
    void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results);
}

/// <summary>單幀評估脈絡（M290）。</summary>
public readonly record struct AnalyticsModuleFrame(DateTime SnapshotUtc);

/// <summary>熱區累計來源（M58／M290）：會累積熱區格數的模組實作本介面，供評估器轉發查詢。</summary>
public interface IHeatmapSource
{
    IReadOnlyList<AnalyticsZoneEvaluator.HeatCell> HeatmapCells(int zoneId);

    int HeatmapTotal(int zoneId);
}
