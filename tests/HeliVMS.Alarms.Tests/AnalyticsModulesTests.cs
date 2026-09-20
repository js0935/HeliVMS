using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>M52（§14.7 #6）：分析幾何、情境評估器與事件引擎。</summary>
public class AnalyticsModulesTests : IDisposable
{
    private const string Square = "0.2,0.2;0.8,0.2;0.8,0.8;0.2,0.8";
    private const string VerticalLine = "0.5,0;0.5,1";

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly int _channelId;

    public AnalyticsModulesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-analytics-alarms-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'analytics', 'rtsp://127.0.0.1:8554/an', NULL, 'h264', 1, 'copy');
            """);
        _channelId = _store.Query("SELECT id FROM channels ORDER BY id LIMIT 1;", static r => r.Read() ? r.GetInt32(0) : 0);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static IReadOnlyList<NormalizedPoint> Poly(string text) => AnalyticsGeometry.ParsePoints(text);

    private static AnalyticsDetection At(double x, double y, string cls = "person", string? track = null)
        => new(cls, x, y, 0.9f, track);

    // ---------- 幾何 ----------

    [Fact]
    public void Geometry_PointInPolygon_InsideOutsideBoundary()
    {
        var square = Poly(Square);

        Assert.True(AnalyticsGeometry.PointInPolygon(square, new NormalizedPoint(0.5, 0.5)));
        Assert.False(AnalyticsGeometry.PointInPolygon(square, new NormalizedPoint(0.1, 0.5)));
        Assert.True(AnalyticsGeometry.PointInPolygon(square, new NormalizedPoint(0.2, 0.5)));
        Assert.True(AnalyticsGeometry.PointInPolygon(square, new NormalizedPoint(0.2, 0.2)));
    }

    [Fact]
    public void Geometry_ConcavePolygon()
    {
        var lShape = Poly("0,0;0.8,0;0.8,0.2;0.2,0.2;0.2,0.8;0,0.8");

        Assert.True(AnalyticsGeometry.PointInPolygon(lShape, new NormalizedPoint(0.1, 0.5)));
        Assert.False(AnalyticsGeometry.PointInPolygon(lShape, new NormalizedPoint(0.5, 0.5)));
    }

    [Theory]
    [InlineData(0.3, 0.5, 1)]
    [InlineData(0.7, 0.5, -1)]
    [InlineData(0.5, 0.5, 0)]
    public void Geometry_SignedSide(double x, double y, int expectedSign)
    {
        var side = AnalyticsGeometry.SignedSide(new NormalizedPoint(0.5, 0), new NormalizedPoint(0.5, 1), new NormalizedPoint(x, y));

        Assert.Equal(expectedSign, side);
    }

    [Fact]
    public void Geometry_SegmentIntersects()
    {
        var a = new NormalizedPoint(0, 0.5);
        var b = new NormalizedPoint(1, 0.5);

        Assert.True(AnalyticsGeometry.SegmentIntersects(a, b, new NormalizedPoint(0.5, 0), new NormalizedPoint(0.5, 1)));
        Assert.True(AnalyticsGeometry.SegmentIntersects(a, b, new NormalizedPoint(0.5, 0.5), new NormalizedPoint(0.8, 0.8)));
        Assert.False(AnalyticsGeometry.SegmentIntersects(a, b, new NormalizedPoint(0.2, 0.8), new NormalizedPoint(0.8, 0.8)));
    }

    [Fact]
    public void Geometry_Area()
    {
        Assert.Equal(0.36, AnalyticsGeometry.Area(Poly(Square)), 6);
    }

    [Fact]
    public void Geometry_ParseAndFormatRoundTrip()
    {
        var points = AnalyticsGeometry.ParsePoints(Square);
        var text = AnalyticsGeometry.FormatPoints(points);

        Assert.True(AnalyticsGeometry.IsValidPolygon(AnalyticsGeometry.ParsePoints(text)));
        Assert.Equal(points[3].X, AnalyticsGeometry.ParsePoints(text)[3].X, 5);
    }

    [Fact]
    public void Geometry_InvalidPolygon()
    {
        Assert.False(AnalyticsGeometry.IsValidPolygon(Poly("0.1,0.1;0.2,0.2")));
        Assert.Empty(AnalyticsGeometry.ParsePoints("garbage"));
    }

    // ---------- 評估器：區域侵入 ----------

    [Fact]
    public void Intrusion_ReportsEnterAndExit()
    {
        var zone = new AnalyticsZone(1, "區A", _channelId, AnalyticsModuleKinds.Intrusion, true, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();

        var enter = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(enter);
        Assert.Equal(AnalyticsModuleCatalog.EventIntrusion, enter[0].EventType);
        Assert.Contains("進入", enter[0].Detail);

        var still = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Empty(still);

        var exit = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.05, 0.05, track: "t1") }, new[] { zone });
        Assert.Single(exit);
        Assert.Contains("離開", exit[0].Detail);
    }

    [Fact]
    public void Intrusion_WithoutTrackId_UsesClassKey()
    {
        var zone = new AnalyticsZone(1, "區A", _channelId, AnalyticsModuleKinds.Intrusion, true, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();

        var enter = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5) }, new[] { zone });

        Assert.Single(enter);
        Assert.Equal("person#0", enter[0].TrackId);
    }

    [Fact]
    public void Intrusion_DisabledZone_IsIgnored()
    {
        var zone = new AnalyticsZone(1, "區A", _channelId, AnalyticsModuleKinds.Intrusion, false, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();

        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5) }, new[] { zone }));
    }

    // ---------- 評估器：跨線 ----------

    [Fact]
    public void LineCross_DetectsCrossingWithDirection()
    {
        var zone = new AnalyticsZone(2, "線L", _channelId, AnalyticsModuleKinds.LineCross, true, Poly(VerticalLine));
        var evaluator = new AnalyticsZoneEvaluator();

        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.5, track: "t1") }, new[] { zone }));

        var crossed = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.7, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(crossed);
        Assert.Equal(AnalyticsModuleCatalog.EventLineCross, crossed[0].EventType);
        Assert.Contains("A→B", crossed[0].Detail);

        var back = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(back);
        Assert.Contains("B→A", back[0].Detail);
    }

    [Fact]
    public void LineCross_DirectionFilterSuppresses()
    {
        var zone = new AnalyticsZone(2, "線L", _channelId, AnalyticsModuleKinds.LineCross, true, Poly(VerticalLine), AnalyticsDirections.AToB);
        var evaluator = new AnalyticsZoneEvaluator();

        evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.7, 0.5, track: "t1") }, new[] { zone });
        var suppressed = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.5, track: "t1") }, new[] { zone });
        Assert.Empty(suppressed);

        evaluator.Reset();
        evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.5, track: "t1") }, new[] { zone });
        var allowed = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.7, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(allowed);
    }

    // ---------- 評估器：人群聚集 ----------

    [Fact]
    public void Crowd_RequiresDebounceFrames()
    {
        var zone = new AnalyticsZone(3, "廣場", _channelId, AnalyticsModuleKinds.Crowd, true, Poly(Square), MinCount: 3);
        var evaluator = new AnalyticsZoneEvaluator();
        var detections = new[] { At(0.3, 0.3), At(0.5, 0.5), At(0.7, 0.7) };

        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, detections, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, detections, new[] { zone }));

        var triggered = evaluator.Evaluate(DateTime.UtcNow, detections, new[] { zone });
        Assert.Single(triggered);
        Assert.Equal(AnalyticsModuleCatalog.EventCrowd, triggered[0].EventType);
        Assert.Equal(3, triggered[0].Count);

        Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, detections, new[] { zone }));
    }

    [Fact]
    public void Crowd_DropsBelowThresholdResetsStreak()
    {
        var zone = new AnalyticsZone(3, "廣場", _channelId, AnalyticsModuleKinds.Crowd, true, Poly(Square), MinCount: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var two = new[] { At(0.3, 0.3), At(0.5, 0.5) };

        evaluator.Evaluate(DateTime.UtcNow, two, new[] { zone });
        evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.3) }, new[] { zone });
        evaluator.Evaluate(DateTime.UtcNow, two, new[] { zone });
        evaluator.Evaluate(DateTime.UtcNow, two, new[] { zone });

        var triggered = evaluator.Evaluate(DateTime.UtcNow, two, new[] { zone });
        Assert.Single(triggered);
    }

    [Fact]
    public void Crowd_ZeroThresholdNeverFires()
    {
        var zone = new AnalyticsZone(3, "廣場", _channelId, AnalyticsModuleKinds.Crowd, true, Poly(Square), MinCount: 0);
        var evaluator = new AnalyticsZoneEvaluator();
        var detections = Enumerable.Range(0, 5).Select(i => At(0.3 + (i * 0.05), 0.5)).ToArray();

        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(evaluator.Evaluate(DateTime.UtcNow, detections, new[] { zone }));
        }
    }

    [Fact]
    public void Evaluator_MixedZonesAndReset()
    {
        var intrusion = new AnalyticsZone(1, "區A", _channelId, AnalyticsModuleKinds.Intrusion, true, Poly(Square));
        var line = new AnalyticsZone(2, "線L", _channelId, AnalyticsModuleKinds.LineCross, true, Poly(VerticalLine));
        var evaluator = new AnalyticsZoneEvaluator();

        var entered = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.3, 0.5, track: "t1") }, new[] { intrusion, line });
        Assert.Single(entered);
        Assert.Equal(AnalyticsModuleCatalog.EventIntrusion, entered[0].EventType);

        var crossed = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.7, 0.5, track: "t1") }, new[] { intrusion, line });
        Assert.Single(crossed);
        Assert.Equal(AnalyticsModuleCatalog.EventLineCross, crossed[0].EventType);

        evaluator.Reset();
        var afterReset = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5, track: "t1") }, new[] { intrusion, line });
        Assert.Single(afterReset);
        Assert.Equal(AnalyticsModuleCatalog.EventIntrusion, afterReset[0].EventType);
    }

    // ---------- 評估器：長時間徘徊（M58） ----------

    [Fact]
    public void Loitering_FiresAfterDwell_RefiresAfterExit()
    {
        var zone = new AnalyticsZone(4, "櫃台", _channelId, AnalyticsModuleKinds.Loitering, true, Poly(Square), DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(0), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone }));

        var fired = evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventLoitering, fired[0].EventType);
        Assert.Contains("徘徊", fired[0].Detail);

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(3), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone }));

        evaluator.Evaluate(t0.AddSeconds(4), new[] { At(0.05, 0.05, track: "t1") }, new[] { zone });
        evaluator.Evaluate(t0.AddSeconds(5), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });

        var refired = evaluator.Evaluate(t0.AddSeconds(7), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(refired);
        Assert.Equal(AnalyticsModuleCatalog.EventLoitering, refired[0].EventType);
    }

    [Fact]
    public void Loitering_DwellZero_FiresImmediately()
    {
        var zone = new AnalyticsZone(4, "櫃台", _channelId, AnalyticsModuleKinds.Loitering, true, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();

        var fired = evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });

        Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventLoitering, fired[0].EventType);
    }

    [Fact]
    public void Loitering_LeavesAndReturns_ReusesInitialEntryTime()
    {
        var zone = new AnalyticsZone(4, "櫃台", _channelId, AnalyticsModuleKinds.Loitering, true, Poly(Square), DwellSeconds: 1);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        evaluator.Evaluate(t0.AddSeconds(0), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.05, 0.05, track: "t1") }, new[] { zone });
        evaluator.Evaluate(t0.AddSeconds(10), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(10), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone }));
        var fired = evaluator.Evaluate(t0.AddSeconds(11), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(fired);
    }

    // ---------- 評估器：靜止物／遺留物（M58） ----------

    [Fact]
    public void Stationary_StationaryObjectFiresThenSuppressed()
    {
        var zone = new AnalyticsZone(5, "遺留物", _channelId, AnalyticsModuleKinds.Stationary, true, Poly(Square), DwellSeconds: 1);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        evaluator.Evaluate(t0.AddSeconds(0), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });

        var fired = evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone });
        Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventStationary, fired[0].EventType);
        Assert.Contains("靜止", fired[0].Detail);

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.5, 0.5, track: "t1") }, new[] { zone }));
    }

    [Fact]
    public void Stationary_MovementBeyondTolerance_ResetsDwellClock()
    {
        var zone = new AnalyticsZone(5, "走動", _channelId, AnalyticsModuleKinds.Stationary, true, Poly(Square), DwellSeconds: 1);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            var results = evaluator.Evaluate(
                t0.AddSeconds(i),
                new[] { At(0.3 + (i * 0.05), 0.5, track: "t1") },
                new[] { zone });
            Assert.Empty(results);
        }
    }

    // ---------- 評估器：車流統計（M58） ----------

    [Fact]
    public void Traffic_CountsCumulativePerDirection()
    {
        var zone = new AnalyticsZone(6, "車流線", _channelId, AnalyticsModuleKinds.Traffic, true, Poly(VerticalLine));
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v1") }, new[] { zone }));

        var first = evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "v1") }, new[] { zone });
        Assert.Single(first);
        Assert.Equal(AnalyticsModuleCatalog.EventTraffic, first[0].EventType);
        Assert.Equal(1, first[0].Count);
        Assert.Contains("車流", first[0].Detail);

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "v1") }, new[] { zone }));
        var back = evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v1") }, new[] { zone });
        Assert.Single(back);
        Assert.Equal(1, back[0].Count);

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v2") }, new[] { zone }));
        var second = evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "v2") }, new[] { zone });
        Assert.Single(second);
        Assert.Equal(2, second[0].Count);
    }

    [Fact]
    public void Traffic_DirectionFilter_OnlyCountsConfiguredDirection()
    {
        var zone = new AnalyticsZone(6, "單向", _channelId, AnalyticsModuleKinds.Traffic, true, Poly(VerticalLine), AnalyticsDirections.AToB);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "v1") }, new[] { zone });
        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v1") }, new[] { zone }));

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v2") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "v2") }, new[] { zone }));

        var crossed = evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "v2") }, new[] { zone });
        Assert.Single(crossed);
        Assert.Equal(AnalyticsModuleCatalog.EventTraffic, crossed[0].EventType);
    }

    // ---------- 評估器：熱區圖（M58） ----------

    [Fact]
    public void Heatmap_AccumulatesCells_AndResetClears()
    {
        var zone = new AnalyticsZone(7, "入口", _channelId, AnalyticsModuleKinds.Heatmap, true, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.5, 0.5), At(0.7, 0.3) }, new[] { zone }));
        }

        Assert.Equal(6, evaluator.HeatmapTotal(zone.Id));
        var cells = evaluator.HeatmapCells(zone.Id);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.Equal(3, c.Count));
        Assert.Contains(cells, c => c.Row == 2 && c.Col == 2);
        Assert.Contains(cells, c => c.Row == 1 && c.Col == 3);

        evaluator.Reset();
        Assert.Equal(0, evaluator.HeatmapTotal(zone.Id));
        Assert.Empty(evaluator.HeatmapCells(zone.Id));
    }

    [Theory]
    [InlineData(0.0, 0.0, 0, 0)]
    [InlineData(0.99, 0.99, 4, 4)]
    [InlineData(0.51, 0.49, 2, 2)]
    public void Heatmap_CellsClampAtEdges(double x, double y, int expectedCol, int expectedRow)
    {
        var zone = new AnalyticsZone(7, "全景", _channelId, AnalyticsModuleKinds.Heatmap, true, Poly("0,0;1,0;1,1;0,1"));
        var evaluator = new AnalyticsZoneEvaluator();

        evaluator.Evaluate(DateTime.UtcNow, new[] { At(x, y) }, new[] { zone });

        var cell = Assert.Single(evaluator.HeatmapCells(zone.Id));
        Assert.Equal(expectedRow, cell.Row);
        Assert.Equal(expectedCol, cell.Col);
    }

    [Fact]
    public void Heatmap_OutsidePolygon_NotCounted()
    {
        var zone = new AnalyticsZone(7, "入口", _channelId, AnalyticsModuleKinds.Heatmap, true, Poly(Square));
        var evaluator = new AnalyticsZoneEvaluator();

        evaluator.Evaluate(DateTime.UtcNow, new[] { At(0.05, 0.05) }, new[] { zone });

        Assert.Equal(0, evaluator.HeatmapTotal(zone.Id));
    }

    // ---------- 評估器：尾隨/逆行 `ai_tailgating`（M59，§5.6） ----------

    [Fact]
    public void Tailgating_FollowingWithinWindow_FiresTailgating()
    {
        var zone = new AnalyticsZone(8, "入口", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.Both, DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "bob") }, new[] { zone }));

        var fired = evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.7, 0.5, track: "bob") }, new[] { zone });
        var ev = Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventTailgating, ev.EventType);
        Assert.Contains("尾隨", ev.Detail);
        Assert.Contains("A→B", ev.Detail);
    }

    [Fact]
    public void Tailgating_FollowingBeyondWindow_NoEvent()
    {
        var zone = new AnalyticsZone(8, "入口", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.Both, DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(10), new[] { At(0.7, 0.5, track: "bob") }, new[] { zone }));

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(11), new[] { At(0.3, 0.5, track: "bob") }, new[] { zone }));
    }

    [Fact]
    public void Tailgating_TwoWayFlow_NoCounterFlowButFollowingFires()
    {
        var zone = new AnalyticsZone(8, "閘門", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.Both, DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "bob") }, new[] { zone }));

        var fired = evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.7, 0.5, track: "bob") }, new[] { zone });
        var ev = Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventTailgating, ev.EventType);
        Assert.Contains("尾隨", ev.Detail);
        Assert.DoesNotContain("逆行", ev.Detail);
    }

    [Fact]
    public void Tailgating_CounterFlowAgainstConfiguredDirection_Fires()
    {
        var zone = new AnalyticsZone(8, "單向閘", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.AToB);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));

        var fired = evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "alice") }, new[] { zone });
        var ev = Assert.Single(fired);
        Assert.Equal(AnalyticsModuleCatalog.EventTailgating, ev.EventType);
        Assert.Contains("逆行", ev.Detail);
    }

    [Fact]
    public void Tailgating_ConfiguredDirection_NotCounterFlow()
    {
        var zone = new AnalyticsZone(8, "單向閘", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.AToB);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
    }

    [Fact]
    public void Tailgating_SameTrackDoesNotFollowItself()
    {
        var zone = new AnalyticsZone(8, "入口", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.Both, DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(3), new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));
    }

    [Fact]
    public void Tailgating_Reset_ClearsFollowHistory()
    {
        var zone = new AnalyticsZone(8, "入口", _channelId, AnalyticsModuleKinds.Tailgating, true, Poly(VerticalLine), AnalyticsDirections.Both, DwellSeconds: 2);
        var evaluator = new AnalyticsZoneEvaluator();
        var t0 = DateTime.UtcNow;

        Assert.Empty(evaluator.Evaluate(t0, new[] { At(0.7, 0.5, track: "alice") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(1), new[] { At(0.3, 0.5, track: "alice") }, new[] { zone }));

        evaluator.Reset();

        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(2), new[] { At(0.7, 0.5, track: "bob") }, new[] { zone }));
        Assert.Empty(evaluator.Evaluate(t0.AddSeconds(3), new[] { At(0.3, 0.5, track: "bob") }, new[] { zone }));
    }

    // ---------- 事件引擎 ----------

    private long CountEvents(string eventType)
        => _store.Query(
            "SELECT COUNT(*) FROM alarm_events WHERE event_type = $t;",
            static r => r.Read() ? r.GetInt64(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$t", eventType));

    [Fact]
    public void Engine_LoadZonesAndInsertEvents()
    {
        new AnalyticsZoneRepository(_store).Add("區A", _channelId, AnalyticsModuleKinds.Intrusion, Square);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();

        Assert.Equal(1, engine.ZoneCount);

        var frame = new DetectionsFrame(
            DateTime.UtcNow,
            new[] { new Detection("person", 0.9f, 0.45f, 0.45f, 0.1f, 0.1f) });

        var inserted = engine.OnDetections(_channelId, frame);

        Assert.Single(inserted);
        Assert.Equal(AnalyticsModuleCatalog.EventIntrusion, inserted[0].EventType);
        Assert.Contains("區A", inserted[0].Detail!);
        Assert.Equal(1, CountEvents(AnalyticsModuleCatalog.EventIntrusion));
    }

    [Fact]
    public void Engine_RaisesEventInserted()
    {
        new AnalyticsZoneRepository(_store).Add("區A", _channelId, AnalyticsModuleKinds.Intrusion, Square);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();

        AlarmEventRecord? captured = null;
        engine.EventInserted += (_, record) => captured = record;

        engine.OnDetections(
            _channelId,
            new DetectionsFrame(DateTime.UtcNow, new[] { new Detection("person", 0.9f, 0.45f, 0.45f, 0.1f, 0.1f) }));

        Assert.NotNull(captured);
        Assert.Equal(AnalyticsModuleCatalog.EventIntrusion, captured!.EventType);
    }

    [Fact]
    public void Engine_OtherChannelIsIgnored()
    {
        new AnalyticsZoneRepository(_store).Add("區A", _channelId, AnalyticsModuleKinds.Intrusion, Square);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();

        var inserted = engine.OnDetections(
            _channelId + 99,
            new DetectionsFrame(DateTime.UtcNow, new[] { new Detection("person", 0.9f, 0.45f, 0.45f, 0.1f, 0.1f) }));

        Assert.Empty(inserted);
        Assert.Equal(0, CountEvents(AnalyticsModuleCatalog.EventIntrusion));
    }

    [Fact]
    public void Engine_InsertsLoiteringEvent()
    {
        new AnalyticsZoneRepository(_store).Add(
            "長佇列", _channelId, AnalyticsModuleKinds.Loitering, Square, AnalyticsDirections.Both, 0, 2);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();
        var t0 = DateTime.UtcNow;

        Assert.Empty(engine.OnDetections(_channelId, FrameAt(t0, 0)));
        Assert.Empty(engine.OnDetections(_channelId, FrameAt(t0, 1)));

        var inserted = engine.OnDetections(_channelId, FrameAt(t0, 2));
        Assert.Single(inserted);
        Assert.Equal(AnalyticsModuleCatalog.EventLoitering, inserted[0].EventType);
        Assert.Equal(1, CountEvents(AnalyticsModuleCatalog.EventLoitering));
    }

    [Fact]
    public void Engine_HeatmapZone_EmitsNoEvents()
    {
        new AnalyticsZoneRepository(_store).Add("熱區", _channelId, AnalyticsModuleKinds.Heatmap, Square);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();

        var inserted = engine.OnDetections(_channelId, FrameAt(DateTime.UtcNow, 0));

        Assert.Empty(inserted);
        Assert.Equal(0, CountEvents(AnalyticsModuleCatalog.EventIntrusion));
    }

    [Fact]
    public void Engine_TailgatingZone_InsertsCounterFlowEvent()
    {
        new AnalyticsZoneRepository(_store).Add("單向閘", _channelId, AnalyticsModuleKinds.Tailgating, VerticalLine, AnalyticsDirections.AToB);
        var engine = new AnalyticsEventEngine(_store);
        engine.LoadZones();
        var t0 = DateTime.UtcNow;

        Assert.Empty(engine.OnDetections(_channelId, new DetectionsFrame(t0, new[] { new Detection("person", 0.9f, 0.65f, 0.45f, 0.1f, 0.1f) })));

        var inserted = engine.OnDetections(_channelId, new DetectionsFrame(t0.AddSeconds(1), new[] { new Detection("person", 0.9f, 0.25f, 0.45f, 0.1f, 0.1f) }));
        var ev = Assert.Single(inserted);
        Assert.Equal(AnalyticsModuleCatalog.EventTailgating, ev.EventType);
        Assert.Contains("逆行", ev.Detail);
        Assert.Equal(1, CountEvents(AnalyticsModuleCatalog.EventTailgating));
    }

    private static DetectionsFrame FrameAt(DateTime t0, int sec)
        => new(t0.AddSeconds(sec), new[] { new Detection("person", 0.9f, 0.45f, 0.45f, 0.1f, 0.1f) });

    [Fact]
    public void ModuleCatalog_MapsModulesToEvents()
    {
        Assert.Equal(AnalyticsModuleCatalog.EventLineCross, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.LineCross)!.EventType);
        Assert.Equal("analytics.crowd", AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Crowd)!.LicenseFeature);
        Assert.Equal(AnalyticsModuleCatalog.EventLoitering, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Loitering)!.EventType);
        Assert.Equal(AnalyticsModuleCatalog.EventStationary, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Stationary)!.EventType);
        Assert.Equal(AnalyticsModuleCatalog.EventTraffic, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Traffic)!.EventType);
        Assert.Equal("analytics.heatmap", AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Heatmap)!.LicenseFeature);
        Assert.Null(AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Heatmap)!.EventType);
        Assert.Equal(AnalyticsModuleCatalog.EventTailgating, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Tailgating)!.EventType);
        Assert.Equal("analytics.tailgating", AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Tailgating)!.LicenseFeature);
        Assert.Null(AnalyticsModuleCatalog.For("thermal"));
    }
}
