namespace HeliVMS.Alarms;

/// <summary>
/// 模組化分析情境評估器（M52／M290，§14.7 #6；§5.6）：把每一幀的偵測交給已註冊的
/// <see cref="IAnalyticsModule"/> 分派評估，本身只負責逐幀去重鍵與模組路由，不再內含任何情境邏輯。
/// 有狀態（委由各模組跨幀保存），呼叫 <see cref="Reset"/> 可清空。
/// </summary>
public sealed class AnalyticsZoneEvaluator
{
    /// <summary>人群聚集需連續達標幀數（去抖動）；轉發 <see cref="CrowdModule.DebounceFrames"/>。</summary>
    public const int CrowdDebounceFrames = CrowdModule.DebounceFrames;

    /// <summary>熱區圖格子邊長（5×5）。</summary>
    public const int HeatmapGridSize = HeatmapModule.GridSize;

    /// <summary>靜止物判定移動容忍（正規化距離）。</summary>
    public const double StationaryMoveTolerance = StationaryModule.MoveTolerance;

    /// <summary>熱區圖單格計數。</summary>
    public sealed record HeatCell(int Row, int Col, int Count);

    private readonly Dictionary<string, IAnalyticsModule> _modules;
    private readonly IHeatmapSource? _heatmap;

    public AnalyticsZoneEvaluator()
        : this(AnalyticsModuleRegistry.CreateDefault())
    {
    }

    /// <summary>以指定模組集建構，讓呼叫端注入自訂模組或依授權過濾（<see cref="AnalyticsModuleRegistry.Filter"/>）。</summary>
    public AnalyticsZoneEvaluator(IReadOnlyList<IAnalyticsModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = new Dictionary<string, IAnalyticsModule>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            _modules[module.Module] = module;
        }

        _heatmap = modules.OfType<IHeatmapSource>().FirstOrDefault();
    }

    /// <summary>已註冊的模組識別碼。</summary>
    public IReadOnlyList<string> RegisteredModules => _modules.Keys.ToArray();

    public void Reset()
    {
        foreach (var module in _modules.Values)
        {
            module.Reset();
        }
    }

    /// <summary>回傳指定分析區之熱區單格計數（row×col＝5×5，M58）；未註冊熱區模組時為空。</summary>
    public IReadOnlyList<HeatCell> HeatmapCells(int zoneId)
        => _heatmap?.HeatmapCells(zoneId) ?? Array.Empty<HeatCell>();

    public int HeatmapTotal(int zoneId)
        => _heatmap?.HeatmapTotal(zoneId) ?? 0;

    public IReadOnlyList<AnalyticsResult> Evaluate(
        DateTime snapshotUtc,
        IReadOnlyList<AnalyticsDetection> detections,
        IReadOnlyList<AnalyticsZone> zones)
    {
        var results = new List<AnalyticsResult>();
        var keyed = KeyDetections(detections);
        var frame = new AnalyticsModuleFrame(snapshotUtc);

        foreach (var zone in zones)
        {
            if (!zone.Enabled)
            {
                continue;
            }

            if (!_modules.TryGetValue(zone.Module, out var module))
            {
                continue;
            }

            module.Evaluate(frame, keyed, new[] { zone }, results);
        }

        return results;
    }

    private static List<(string Key, AnalyticsDetection Detection)> KeyDetections(IReadOnlyList<AnalyticsDetection> detections)
    {
        var list = new List<(string, AnalyticsDetection)>();
        var counters = new Dictionary<string, int>();
        foreach (var detection in detections)
        {
            string key;
            if (!string.IsNullOrEmpty(detection.TrackId))
            {
                key = detection.TrackId!;
            }
            else
            {
                counters.TryGetValue(detection.Class, out var n);
                counters[detection.Class] = n + 1;
                key = $"{detection.Class}#{n}";
            }

            list.Add((key, detection));
        }

        return list;
    }
}
