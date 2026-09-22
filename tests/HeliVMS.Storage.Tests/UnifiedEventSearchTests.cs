namespace HeliVMS.Storage.Tests;

public class UnifiedEventSearchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly DoorEventRepository _door;
    private readonly POSEventRepository _pos;
    private readonly EdgeSmartEventRepository _edge;
    private readonly AlarmEventRepository _alarm;
    private readonly UnifiedEventSearch _search;

    private static DateTime T(int hour) =>
        new DateTime(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public UnifiedEventSearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-fts-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();

        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'c1', 'rtsp://cam1', NULL, 'h264', 1, 'copy');
            """);
        _door = new DoorEventRepository(_store);
        _pos = new POSEventRepository(_store);
        _edge = new EdgeSmartEventRepository(_store);
        _alarm = new AlarmEventRepository(_store);
        _search = new UnifiedEventSearch(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void DoorTrigger_NewEvent_IsSearchable()
    {
        _door.Insert(1, 1, "CARD-77", "In", true, "approved", T(8));

        var hits = _search.Search("CARD-77 OR approved", null, null, ForensicSource.Door);

        var hit = Assert.Single(hits);
        Assert.Equal(ForensicSource.Door, hit.Source);
        Assert.Contains("CARD-77", hit.Text);
    }

    [Fact]
    public void PosTrigger_NewTransaction_IsSearchable()
    {
        _pos.Insert(1, "R1", "TXN-999", 1200, T(9));

        var hits = _search.Search("TXN-999", null, null, ForensicSource.Pos);

        var hit = Assert.Single(hits);
        Assert.Equal(ForensicSource.Pos, hit.Source);
        Assert.Contains("TXN-999", hit.Text);
        Assert.Contains("1200", hit.Text);
    }

    [Fact]
    public void EdgeTrigger_NewClass_IsSearchable()
    {
        _edge.Append(1, "lpr-plate", "TRK-5", "LtR", 0, 0, 1, 1, T(10));

        var hits = _search.Search("lpr", null, null, ForensicSource.EdgeSmart);

        var hit = Assert.Single(hits);
        Assert.Equal(ForensicSource.EdgeSmart, hit.Source);
        Assert.Contains("lpr-plate", hit.Text);
    }

    [Fact]
    public void AlarmSource_Searchable_WhenFlagged()
    {
        _alarm.Insert(1, "motion", T(8), null, "alpha beta");
        var hits = _search.Search("alpha", null, null, ForensicSource.Alarm);
        var hit = Assert.Single(hits);
        Assert.Equal(ForensicSource.Alarm, hit.Source);
    }

    [Fact]
    public void UnifiedSearch_OrdersLatestFirst_AcrossSources()
    {
        _door.Insert(1, 1, "CARD-88", "In", true, "approved", T(10));
        _pos.Insert(1, "R2", "TXN-88", 150, T(12));
        _edge.Append(1, "person", "TRK-88", "RtL", 0, 0, 1, 1, T(11));

        var hits = _search.Search("88", null, null);

        Assert.Equal(3, hits.Count);
        Assert.Equal(ForensicSource.Pos, hits[0].Source);   // 12:00 最新
        Assert.Equal(ForensicSource.EdgeSmart, hits[1].Source); // 11:00
        Assert.Equal(ForensicSource.Door, hits[2].Source);  // 10:00
    }

    [Fact]
    public void SourceMask_FiltersSources()
    {
        _door.Insert(1, 1, "CARD-77", "In", true, "approved", T(8));
        _pos.Insert(1, "R1", "TXN-77", 100, T(9));

        var door = _search.Search("77", null, null, ForensicSource.Door);
        var pos = _search.Search("77", null, null, ForensicSource.Pos);

        Assert.Single(door);
        Assert.Single(pos);
        Assert.Equal(ForensicSource.Door, door[0].Source);
        Assert.Equal(ForensicSource.Pos, pos[0].Source);
    }

    [Fact]
    public void OperatorAndOr_SurviveNormalization()
    {
        _door.Insert(1, 1, "CARD-77", "In", true, "approved", T(8));
        _door.Insert(1, 1, "CARD-88", "In", true, "approved", T(9));

        var and = _search.Search("77 AND approved", null, null, ForensicSource.Door);
        var or = _search.Search("77 OR approved", null, null, ForensicSource.Door);

        Assert.Single(and);
        Assert.Equal(2, or.Count);
    }

    [Fact]
    public void RangeFilter_LeftClosedRightOpen()
    {
        _door.Insert(1, 1, "CARD-77", "In", true, "approved", T(8));
        _door.Insert(1, 1, "CARD-77", "Out", true, "approved", T(6));

        var hits = _search.Search("77", T(7), T(12), ForensicSource.Door);

        var hit = Assert.Single(hits);
        Assert.Contains("In", hit.Text);
    }

    [Fact]
    public void BlankQuery_Throws()
    {
        Assert.Throws<ArgumentException>(() => _search.Search("   ", null, null));
    }

    [Fact]
    public void RebuildAll_HealsStaleIndexes()
    {
        _door.Insert(1, 1, "CARD-77", "In", true, "approved", T(8));
        Assert.Single(_search.Search("77", null, null, ForensicSource.Door));

        _store.Execute("DELETE FROM door_events_fts;"); // 索引毀損（count 不變、MATCH 空）
        Assert.Empty(_search.Search("77", null, null, ForensicSource.Door));

        _search.RebuildAll();

        Assert.Single(_search.Search("77", null, null, ForensicSource.Door));
    }
}