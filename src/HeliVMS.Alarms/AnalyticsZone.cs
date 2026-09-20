using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>分析情境區（M52，§14.7 #6）：模組＋幾何＋門檻。</summary>
public sealed record AnalyticsZone(
    int Id,
    string Name,
    int ChannelId,
    string Module,
    bool Enabled,
    IReadOnlyList<NormalizedPoint> Polygon,
    string Direction = AnalyticsDirections.Both,
    int MinCount = 0,
    int DwellSeconds = 0)
{
    public static AnalyticsZone From(AnalyticsZoneRecord record)
        => new(
            record.Id,
            record.Name,
            record.ChannelId,
            record.Module,
            record.Enabled,
            AnalyticsGeometry.ParsePoints(record.Polygon),
            record.Direction,
            record.MinCount,
            record.DwellSeconds);
}

/// <summary>分析模組註冊表（M52，§14.7 #6；§5.6）：模組與事件類型、授權位元對映。</summary>
public static class AnalyticsModuleCatalog
{
    public const string EventLineCross = "ai_line_cross";
    public const string EventIntrusion = "ai_intrusion";
    public const string EventCrowd = "ai_crowd";
    public const string EventLoitering = "ai_loitering";
    public const string EventStationary = "ai_stationary";
    public const string EventTraffic = "ai_traffic";

    public sealed record ModuleInfo(string Module, string DisplayName, string? EventType, string LicenseFeature);

    public static IReadOnlyList<ModuleInfo> All { get; } = new[]
    {
        new ModuleInfo(AnalyticsModuleKinds.LineCross, "周界/跨線", EventLineCross, "analytics.line_cross"),
        new ModuleInfo(AnalyticsModuleKinds.Intrusion, "區域侵入", EventIntrusion, "analytics.intrusion"),
        new ModuleInfo(AnalyticsModuleKinds.Crowd, "人群聚集", EventCrowd, "analytics.crowd"),
        new ModuleInfo(AnalyticsModuleKinds.Loitering, "長時間徘徊", EventLoitering, "analytics.loitering"),
        new ModuleInfo(AnalyticsModuleKinds.Stationary, "靜止物/遺留物", EventStationary, "analytics.stationary"),
        new ModuleInfo(AnalyticsModuleKinds.Traffic, "車流統計", EventTraffic, "analytics.traffic"),
        new ModuleInfo(AnalyticsModuleKinds.Heatmap, "熱區圖", null, "analytics.heatmap"),
    };

    public static ModuleInfo? For(string module)
        => All.FirstOrDefault(m => m.Module == module);
}
