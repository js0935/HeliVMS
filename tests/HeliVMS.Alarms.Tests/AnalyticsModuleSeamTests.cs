using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// M290（§5.6）：<see cref="IAnalyticsModule"/> 外掛縫——模組註冊表的完整性、授權位元對映、
/// 依授權過濾與評估器分派。這些守衛確保「新增情境不用改共用類別」「未授權模組不會跑」。
/// </summary>
public class AnalyticsModuleSeamTests
{
    private const string Square = "0.2,0.2;0.8,0.2;0.8,0.8;0.2,0.8";
    private const string VerticalLine = "0.5,0;0.5,1";

    private static IReadOnlyList<NormalizedPoint> Poly(string text) => AnalyticsGeometry.ParsePoints(text);

    private static AnalyticsDetection At(double x, double y, string? track = null)
        => new("person", x, y, 0.9f, track);

    private static AnalyticsZone Zone(string module, string polygon, string direction = AnalyticsDirections.Both, int dwell = 0)
        => new(1, "區", 1, module, true, Poly(polygon), direction, 0, dwell);

    [Fact]
    public void 模組註冊表涵蓋所有分析模組種類且無重複()
    {
        var modules = AnalyticsModuleRegistry.CreateDefault();

        Assert.Equal(AnalyticsModuleKinds.All.Count, modules.Count);
        Assert.Equal(
            AnalyticsModuleKinds.All.OrderBy(x => x, StringComparer.Ordinal),
            modules.Select(m => m.Module).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(modules.Count, modules.Select(m => m.Module).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 內建模組的授權位元與事件類型與目錄一致()
    {
        foreach (var module in AnalyticsModuleRegistry.CreateDefault())
        {
            var info = AnalyticsModuleCatalog.For(module.Module);
            Assert.NotNull(info);
            Assert.Equal(info!.LicenseFeature, module.LicenseFeature);
            Assert.Equal(info.EventType, module.EventType);
        }
    }

    [Fact]
    public void 評估器只分派已註冊的模組()
    {
        var evaluator = new AnalyticsZoneEvaluator([new LineCrossModule()]);
        var intrusion = Zone(AnalyticsModuleKinds.Intrusion, Square);

        Assert.Equal(["line_cross"], evaluator.RegisteredModules);
        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, [At(0.5, 0.5, "t1")], [intrusion]));
    }

    [Fact]
    public void 自訂模組可注入並改寫事件輸出()
    {
        var fake = new FakeIntrusionModule();
        var evaluator = new AnalyticsZoneEvaluator([fake]);
        var intrusion = Zone(AnalyticsModuleKinds.Intrusion, Square);
        var line = Zone(AnalyticsModuleKinds.LineCross, VerticalLine);

        var results = evaluator.Evaluate(DateTime.UtcNow, [At(0.5, 0.5, "t1")], [intrusion, line]);

        var only = Assert.Single(results);
        Assert.Equal("custom.intrusion", only.EventType);
        Assert.Equal(1, fake.EvaluateCalls);
    }

    [Fact]
    public void 依授權過濾後未授權模組不再產生事件()
    {
        var allowed = AnalyticsModuleRegistry.Filter(
            AnalyticsModuleRegistry.CreateDefault(),
            feature => feature != "analytics.intrusion");
        var evaluator = new AnalyticsZoneEvaluator(allowed);

        var intrusion = Zone(AnalyticsModuleKinds.Intrusion, Square);
        var line = Zone(AnalyticsModuleKinds.LineCross, VerticalLine);
        var now = DateTime.UtcNow;

        // 侵入模組未授權：即使目標在區域內也不產生事件。
        Assert.Empty(evaluator.Evaluate(now, [At(0.5, 0.5, "t1")], [intrusion]));

        // 已授權的跨線模組仍正常運作：左側進、右側出即計一次跨越。
        evaluator.Evaluate(now, [At(0.3, 0.5, "t2")], [line]);
        var cross = evaluator.Evaluate(now, [At(0.7, 0.5, "t2")], [line]);
        var only = Assert.Single(cross);
        Assert.Equal(AnalyticsModuleCatalog.EventLineCross, only.EventType);
    }

    [Fact]
    public void 熱區查詢經評估器轉發給熱區模組()
    {
        var evaluator = new AnalyticsZoneEvaluator();
        var heatmap = Zone(AnalyticsModuleKinds.Heatmap, Square);

        for (var i = 0; i < 3; i++)
        {
            evaluator.Evaluate(DateTime.UtcNow, [At(0.5, 0.5, "t1")], [heatmap]);
        }

        Assert.Equal(3, evaluator.HeatmapTotal(heatmap.Id));
        var cell = Assert.Single(evaluator.HeatmapCells(heatmap.Id));
        Assert.Equal(3, cell.Count);
    }

    [Fact]
    public void 未註冊熱區模組時熱區查詢為空()
    {
        var evaluator = new AnalyticsZoneEvaluator([new LineCrossModule()]);

        Assert.Empty(evaluator.HeatmapCells(1));
        Assert.Equal(0, evaluator.HeatmapTotal(1));
    }

    [Fact]
    public void 評估器常數別名與模組常數一致()
    {
        Assert.Equal(CrowdModule.DebounceFrames, AnalyticsZoneEvaluator.CrowdDebounceFrames);
        Assert.Equal(HeatmapModule.GridSize, AnalyticsZoneEvaluator.HeatmapGridSize);
        Assert.Equal(StationaryModule.MoveTolerance, AnalyticsZoneEvaluator.StationaryMoveTolerance);
    }

    private sealed class FakeIntrusionModule : IAnalyticsModule
    {
        public int EvaluateCalls { get; private set; }

        public string Module => AnalyticsModuleKinds.Intrusion;

        public void Reset() => EvaluateCalls = 0;

        public void Evaluate(
            AnalyticsModuleFrame frame,
            IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
            IReadOnlyList<AnalyticsZone> zones,
            List<AnalyticsResult> results)
        {
            EvaluateCalls++;
            foreach (var zone in zones)
            {
                results.Add(new AnalyticsResult(
                    zone.Module, "custom.intrusion", zone.Id, zone.Name, zone.ChannelId, null, 1, "自訂模組"));
            }
        }
    }
}
