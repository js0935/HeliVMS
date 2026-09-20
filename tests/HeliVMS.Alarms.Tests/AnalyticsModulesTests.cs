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
    public void ModuleCatalog_MapsModulesToEvents()
    {
        Assert.Equal(AnalyticsModuleCatalog.EventLineCross, AnalyticsModuleCatalog.For(AnalyticsModuleKinds.LineCross)!.EventType);
        Assert.Equal("analytics.crowd", AnalyticsModuleCatalog.For(AnalyticsModuleKinds.Crowd)!.LicenseFeature);
        Assert.Null(AnalyticsModuleCatalog.For("thermal"));
    }
}
