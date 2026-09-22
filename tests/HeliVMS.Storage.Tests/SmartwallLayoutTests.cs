namespace HeliVMS.Storage.Tests;

public class SmartwallLayoutTests : IDisposable
{
    private readonly SmartwallLayoutRepository _repo;
    private readonly SqliteStore _store;
    private readonly string _dbPath;

    public SmartwallLayoutTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-sw-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SmartwallLayoutRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void SchemaVersion_IsV39()
    {
        var version = _store.Query<int>(
            "PRAGMA user_version;", r => r.Read() ? r.GetInt32(0) : -1);
        Assert.Equal(39, version);
    }

    [Fact]
    public void CreateLayout_Persists()
    {
        var id = _repo.CreateLayout("大廳", 4, 4);

        var list = _repo.ListLayouts();
        var layout = Assert.Single(list);
        Assert.Equal(id, layout.Id);
        Assert.Equal("大廳", layout.Name);
        Assert.Equal(4, layout.Rows);
        Assert.Equal(4, layout.Cols);
        Assert.Equal(DateTimeKind.Utc, layout.CreatedAtUtc.Kind);
    }

    [Fact]
    public void CreateLayout_DuplicateName_Throws()
    {
        _repo.CreateLayout("大廳", 4, 4);
        Assert.ThrowsAny<ArgumentException>(() => _repo.CreateLayout("大廳", 2, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateLayout_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => _repo.CreateLayout(name, 2, 2));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(17, 2)]
    [InlineData(2, 17)]
    public void CreateLayout_DimensionsOutOfRange_Throws(int rows, int cols)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.CreateLayout("X", rows, cols));
    }

    [Fact]
    public void RenameLayout_Updates()
    {
        var id = _repo.CreateLayout("舊名", 2, 2);
        _repo.RenameLayout(id, "新名");

        Assert.Equal("新名", Assert.Single(_repo.ListLayouts()).Name);
    }

    [Fact]
    public void AddTile_AppendsAndReadsBack()
    {
        var layoutId = _repo.CreateLayout("L", 3, 3);
        var tileId = _repo.AddTile(layoutId, row: 0, col: 0, rowSpan: 1, colSpan: 2, channelId: 5, view: 1, position: 0);

        var tile = Assert.Single(_repo.GetTiles(layoutId));
        Assert.Equal(tileId, tile.Id);
        Assert.Equal(0, tile.Row);
        Assert.Equal(2, tile.ColSpan);
        Assert.Equal(5, tile.ChannelId);
        Assert.Equal(1, tile.View);
    }

    [Fact]
    public void AddTile_OutOfBounds_Throws()
    {
        var layoutId = _repo.CreateLayout("L", 2, 2);

        Assert.Throws<ArgumentException>(
            () => _repo.AddTile(layoutId, row: 1, col: 1, rowSpan: 2, colSpan: 1, channelId: 5, view: 0, position: 0));
    }

    [Fact]
    public void AddTile_Overlapping_Throws()
    {
        var layoutId = _repo.CreateLayout("L", 4, 4);
        _repo.AddTile(layoutId, row: 1, col: 1, rowSpan: 2, colSpan: 2, channelId: 5, view: 0, position: 0);

        var ex = Assert.Throws<ArgumentException>(
            () => _repo.AddTile(layoutId, row: 2, col: 2, rowSpan: 1, colSpan: 1, channelId: 6, view: 0, position: 1));
        Assert.Contains("重疊", ex.Message);
    }

    [Fact]
    public void AddTile_TouchingEdges_Allowed()
    {
        var layoutId = _repo.CreateLayout("L", 2, 2);
        _repo.AddTile(layoutId, row: 0, col: 0, rowSpan: 1, colSpan: 1, channelId: 5, view: 0, position: 0);
        _repo.AddTile(layoutId, row: 1, col: 1, rowSpan: 1, colSpan: 1, channelId: 6, view: 0, position: 1);

        Assert.Equal(2, _repo.GetTiles(layoutId).Count);
    }

    [Fact]
    public void RemoveTile_Deletes()
    {
        var layoutId = _repo.CreateLayout("L", 2, 2);
        var tileId = _repo.AddTile(layoutId, row: 0, col: 0, rowSpan: 1, colSpan: 1, channelId: 5, view: 0, position: 0);

        _repo.RemoveTile(tileId);

        Assert.Empty(_repo.GetTiles(layoutId));
    }

    [Fact]
    public void Grid_InBounds_EdgeCases()
    {
        Assert.True(LayoutGrid.IsTileInBounds(4, 4, 3, 3, 1, 1));
        Assert.True(LayoutGrid.IsTileInBounds(4, 4, 0, 0, 4, 4));
        Assert.False(LayoutGrid.IsTileInBounds(4, 4, 0, 0, 5, 1));
        Assert.False(LayoutGrid.IsTileInBounds(4, 4, 0, 0, 0, 1));
        Assert.False(LayoutGrid.IsTileInBounds(4, 4, -1, 0, 1, 1));
    }

    [Fact]
    public void Grid_FindOverlap_CornersAndNone()
    {
        var existing = new[]
        {
            new LayoutTile(1, 1, 1, 1, 2, 2, 5, 0, 0),   // cells (1..2, 1..2)
        };

        Assert.NotNull(LayoutGrid.FindOverlap(existing, row: 2, col: 2, rowSpan: 1, colSpan: 1));
        Assert.NotNull(LayoutGrid.FindOverlap(existing, row: 0, col: 2, rowSpan: 2, colSpan: 1));
        Assert.Null(LayoutGrid.FindOverlap(existing, row: 0, col: 0, rowSpan: 1, colSpan: 1));
        Assert.Null(LayoutGrid.FindOverlap(existing, row: 3, col: 0, rowSpan: 1, colSpan: 3));
    }

    [Fact]
    public void Timings_MatchSpec()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SmartwallTimings.AlarmHighlight);
        Assert.Equal(TimeSpan.FromSeconds(300), SmartwallTimings.MosaicKeepLast);
    }
}