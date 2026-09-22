namespace HeliVMS.Storage.Tests;

public class RedactionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly RedactionRepository _repo;
    private static DateTime T0() => new(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);

    public RedactionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-redact-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new RedactionRepository(_store);
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
    public void Add_QueryBySource_RoundTrips()
    {
        var id = _repo.Add(RedactionSources.Clip, 7, 1, T0(), 100, 50, 320, 180, filled: false, T0());

        var rows = _repo.QueryBySource(RedactionSources.Clip, 7);

        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(1, row.ChannelId);
        Assert.Equal(100, row.X);
        Assert.Equal(50, row.Y);
        Assert.Equal(320, row.Width);
        Assert.Equal(180, row.Height);
        Assert.False(row.Filled);
        Assert.Equal(T0(), row.OccurredAtUtc);
    }

    [Fact]
    public void QueryBySource_IgnoresOtherSources()
    {
        _repo.Add(RedactionSources.Clip, 7, 1, T0(), 0, 0, 10, 10, true, T0());
        _repo.Add(RedactionSources.Snapshot, 7, 1, T0(), 0, 0, 10, 10, true, T0());
        _repo.Add(RedactionSources.Clip, 8, 1, T0(), 0, 0, 10, 10, true, T0());

        Assert.Single(_repo.QueryBySource(RedactionSources.Clip, 7));
        Assert.Single(_repo.QueryBySource(RedactionSources.Snapshot, 7));
        Assert.Single(_repo.QueryBySource(RedactionSources.Clip, 8));
    }

    [Fact]
    public void QueryByTime_LeftClosedRightOpen()
    {
        var t = T0();
        _repo.Add(RedactionSources.Clip, 1, 1, t, 0, 0, 10, 10, true, t);
        _repo.Add(RedactionSources.Clip, 1, 1, t.AddMinutes(1), 0, 0, 10, 10, true, t);
        _repo.Add(RedactionSources.Clip, 1, 1, t.AddMinutes(2), 0, 0, 10, 10, true, t);
        _repo.Add(RedactionSources.Clip, 1, 2, t.AddMinutes(1), 0, 0, 10, 10, true, t);

        var rows = _repo.QueryByTime(1, t, t.AddMinutes(2));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.ChannelId));
    }

    [Fact]
    public void QueryByTimeGlobal_CrossesChannels()
    {
        var t = T0();
        _repo.Add(RedactionSources.Clip, 1, 1, t, 0, 0, 10, 10, true, t);
        _repo.Add(RedactionSources.Clip, 1, 2, t.AddMinutes(1), 0, 0, 10, 10, true, t);
        _repo.Add(RedactionSources.Clip, 1, 3, t.AddMinutes(5), 0, 0, 10, 10, true, t);

        var rows = _repo.QueryByTimeGlobal(t, t.AddMinutes(3));

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Add_ZeroSize_Throws()
    {
        var t = T0();
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.Add(
            RedactionSources.Clip, 1, 1, t, 0, 0, 0, 10, true, t));
    }

    [Fact]
    public void Remove_ReturnsTrue_ThenEmpty()
    {
        var id = _repo.Add(RedactionSources.Snapshot, 5, 1, T0(), 0, 0, 10, 10, true, T0());

        Assert.True(_repo.Remove(id));
        Assert.Empty(_repo.QueryBySource(RedactionSources.Snapshot, 5));
        Assert.False(_repo.Remove(id));
    }

    [Fact]
    public void Apply_FilledMask_ZerosPixelsOnlyInsideRegion()
    {
        var w = 8;
        var h = 8;
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = 200;
        }

        RedactionProcessor.Apply(bytes, w, h, 2, 2, 4, 4, filled: true);

        for (var yy = 0; yy < h; yy++)
        {
            for (var xx = 0; xx < w; xx++)
            {
                var off = yy * w * 4 + xx * 4;
                var inside = xx >= 2 && xx < 6 && yy >= 2 && yy < 6;
                if (inside)
                {
                    Assert.Equal(0, bytes[off]);
                    Assert.Equal(0, bytes[off + 2]);
                }
                else
                {
                    Assert.Equal(200, bytes[off]);
                }
            }
        }
    }

    [Fact]
    public void Apply_Blur_ChangesPixelsWithinRegion()
    {
        var w = 16;
        var h = 16;
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i / 4) % 8 == 0 ? 0 : 255);
        }

        var before = (byte[])bytes.Clone();
        RedactionProcessor.Apply(bytes, w, h, 4, 4, 8, 8, filled: false, blurRadius: 2);

        var changedCenter = false;
        for (var yy = 4; yy < 12; yy++)
        {
            for (var xx = 4; xx < 12; xx++)
            {
                var off = yy * w * 4 + xx * 4;
                if (bytes[off] != before[off])
                {
                    changedCenter = true;
                }
            }
        }

        Assert.True(changedCenter);
    }

    [Fact]
    public void Apply_RegionOutsideBounds_ClampsAndKeepsAlphaSafe()
    {
        var w = 6;
        var h = 6;
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = 128;
        }

        RedactionProcessor.Apply(bytes, w, h, -5, -5, 30, 30, filled: true);

        for (var i = 0; i < bytes.Length; i += 4)
        {
            Assert.Equal(0, bytes[i]);
            Assert.Equal(0, bytes[i + 3]);
        }
        Assert.All(bytes, b => Assert.True(b is 0 or 128));
    }

    [Fact]
    public void Apply_FullyOutside_NoChange()
    {
        var w = 6;
        var h = 6;
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = 128;
        }

        var before = (byte[])bytes.Clone();
        RedactionProcessor.Apply(bytes, w, h, 100, 100, 4, 4, filled: true);

        Assert.Equal(before, bytes);
    }

    [Fact]
    public void Apply_BufferTooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() => RedactionProcessor.Apply(
            new byte[10], 8, 8, 0, 0, 2, 2, filled: true));
    }
}