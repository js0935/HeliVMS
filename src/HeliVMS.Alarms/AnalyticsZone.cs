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

    public sealed record ModuleInfo(string Module, string DisplayName, string EventType, string LicenseFeature);

    public static IReadOnlyList<ModuleInfo> All { get; } = new[]
    {
        new ModuleInfo(AnalyticsModuleKinds.LineCross, "周界/跨線", EventLineCross, "analytics.line_cross"),
        new ModuleInfo(AnalyticsModuleKinds.Intrusion, "區域侵入", EventIntrusion, "analytics.intrusion"),
        new ModuleInfo(AnalyticsModuleKinds.Crowd, "人群聚集", EventCrowd, "analytics.crowd"),
    };

    public static ModuleInfo? For(string module)
        => All.FirstOrDefault(m => m.Module == module);
}
