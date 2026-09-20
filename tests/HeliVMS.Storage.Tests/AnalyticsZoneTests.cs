using System.IO;

namespace HeliVMS.Storage.Tests;

/// <summary>M52（§14.7 #6）：分析情境多邊形與分析區存取。</summary>
public class AnalyticsZoneTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AnalyticsZoneRepository _zones;

    public AnalyticsZoneTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-analytics-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _zones = new AnalyticsZoneRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private const string Square = "0.1,0.1;0.9,0.1;0.9,0.9;0.1,0.9";

    [Fact]
    public void Polygon_ParsesValidPoints()
    {
        Assert.True(AnalyticsPolygon.TryParse(Square, out var points));
        Assert.Equal(4, points.Count);
        Assert.Equal(0.1, points[0].X, 5);
        Assert.Equal(0.9, points[2].Y, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.1,0.1;0.9,0.9")]
    [InlineData("0.1,0.1;0.9,0.9;1.5,0.5")]
    [InlineData("0.1,0.1;0.9,0.9;bad")]
    [InlineData("0.1;0.9")]
    public void Polygon_RejectsInvalidText(string text)
    {
        Assert.False(AnalyticsPolygon.TryParse(text, out _));
        Assert.False(AnalyticsPolygon.IsValid(text));
    }

    [Fact]
    public void Polygon_FormatRoundTrips()
    {
        Assert.True(AnalyticsPolygon.TryParse(Square, out var points));
        var formatted = AnalyticsPolygon.Format(points);
        Assert.True(AnalyticsPolygon.TryParse(formatted, out var again));
        Assert.Equal(4, again.Count);
        Assert.Equal(0.9, again[1].X, 5);
    }

    [Fact]
    public void Add_PersistsZone()
    {
        var id = _zones.Add("前門周界", 27, AnalyticsModuleKinds.LineCross, Square, AnalyticsDirections.AToB, 0, 0);

        var record = _zones.Get(id);
        Assert.NotNull(record);
        Assert.Equal("前門周界", record!.Name);
        Assert.Equal(27, record.ChannelId);
        Assert.Equal(AnalyticsModuleKinds.LineCross, record.Module);
        Assert.True(record.Enabled);
        Assert.Equal(AnalyticsDirections.AToB, record.Direction);
        Assert.Equal(Square, record.Polygon);
    }

    [Fact]
    public void Add_InvalidModule_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _zones.Add("x", 1, "thermal", Square));
    }

    [Fact]
    public void Add_LineCross_AcceptsTwoPoints()
    {
        var id = _zones.Add("周界線", 1, AnalyticsModuleKinds.LineCross, "0.5,0;0.5,1");

        Assert.Equal("0.5,0;0.5,1", _zones.Get(id)!.Polygon);
    }

    [Fact]
    public void Add_InvalidPolygon_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _zones.Add("x", 1, AnalyticsModuleKinds.Intrusion, "0.1,0.1;0.2,0.2"));
    }

    [Fact]
    public void Add_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _zones.Add("  ", 1, AnalyticsModuleKinds.Crowd, Square));
    }

    [Fact]
    public void Add_InvalidDirection_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _zones.Add("x", 1, AnalyticsModuleKinds.LineCross, Square, "diagonal"));
    }

    [Fact]
    public void Add_NegativeThresholds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _zones.Add("x", 1, AnalyticsModuleKinds.Crowd, Square, minCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _zones.Add("x", 1, AnalyticsModuleKinds.Crowd, Square, dwellSeconds: -1));
    }

    [Fact]
    public void ListByChannel_Filters()
    {
        var a = _zones.Add("a", 1, AnalyticsModuleKinds.Intrusion, Square);
        _zones.Add("b", 2, AnalyticsModuleKinds.Intrusion, Square);

        var list = _zones.ListByChannel(1);

        Assert.Equal(new[] { a }, list.Select(z => z.Id));
    }

    [Fact]
    public void SetEnabled_Toggles()
    {
        var id = _zones.Add("a", 1, AnalyticsModuleKinds.Intrusion, Square);
        _zones.SetEnabled(id, false);

        Assert.False(_zones.Get(id)!.Enabled);
    }

    [Fact]
    public void Update_ChangesGeometryAndThresholds()
    {
        var id = _zones.Add("a", 1, AnalyticsModuleKinds.Crowd, Square, minCount: 3);
        var bigger = "0,0;1,0;1,1;0,1";

        _zones.Update(id, "b", bigger, AnalyticsDirections.BToA, 5, 10);

        var record = _zones.Get(id)!;
        Assert.Equal("b", record.Name);
        Assert.Equal(bigger, record.Polygon);
        Assert.Equal(AnalyticsDirections.BToA, record.Direction);
        Assert.Equal(5, record.MinCount);
        Assert.Equal(10, record.DwellSeconds);
    }

    [Fact]
    public void Delete_RemovesZone()
    {
        var id = _zones.Add("a", 1, AnalyticsModuleKinds.Intrusion, Square);
        _zones.Delete(id);

        Assert.Null(_zones.Get(id));
    }

    [Fact]
    public void All_ModuleKindsAreValid()
    {
        foreach (var module in AnalyticsModuleKinds.All)
        {
            Assert.True(AnalyticsModuleKinds.IsValid(module));
        }

        Assert.False(AnalyticsModuleKinds.IsValid("unknown"));
    }
}
