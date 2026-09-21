namespace HeliVMS.Storage.Tests;

public class LegalHoldRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly LegalHoldRepository _repo;
    private readonly DateTime _base;

    public LegalHoldRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-hold-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new LegalHoldRepository(_store);
        var now = DateTime.UtcNow;
        _base = new DateTime(now.AddDays(-10).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, '保存鎖定', 'rtsp://127.0.0.1:8554/hold', NULL, 'h264', 1, 'copy');
            """);
    }

    [Fact]
    public void Add_Then_ListActive_ContainsHold()
    {
        var id = _repo.Add(1, _base, _base.AddHours(2), "爭議錄影保存", "alice", _base);

        var active = _repo.ListActive();
        var hold = Assert.Single(active);
        Assert.Equal(id, hold.Id);
        Assert.Equal(1, hold.ChannelId);
        Assert.Equal(_base, hold.FromUtc);
        Assert.Equal(_base.AddHours(2), hold.ToUtc);
        Assert.Equal("爭議錄影保存", hold.Reason);
        Assert.Equal("alice", hold.CreatedBy);
        Assert.True(hold.IsActive);
    }

    [Fact]
    public void Add_InvalidRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _repo.Add(1, _base.AddHours(2), _base, "壞", "alice", _base));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _repo.Add(1, _base, _base, "壞", "alice", _base));
        Assert.Throws<ArgumentException>(() =>
            _repo.Add(1, _base, _base.AddHours(1), "  ", "alice", _base));
    }

    [Fact]
    public void IsLocked_OverlapMatrix()
    {
        _repo.Add(1, _base, _base.AddHours(2), "範圍", "alice", _base); // [T0, T0+2h]
        var t = _base;

        Assert.True(_repo.IsLocked(1, t.AddMinutes(30), t.AddMinutes(90)));   // 內
        Assert.False(_repo.IsLocked(1, t.AddHours(3), t.AddHours(4)));        // 後
        Assert.False(_repo.IsLocked(1, t.AddHours(-4), t.AddHours(-3)));      // 前
        Assert.True(_repo.IsLocked(1, t.AddMinutes(-30), t.AddMinutes(30)));  // 左重疊
        Assert.True(_repo.IsLocked(1, t.AddHours(1).AddMinutes(30), t.AddHours(3))); // 右重疊
        Assert.True(_repo.IsLocked(1, t, t.AddHours(2)));                     // 恰等（含端點）
        Assert.False(_repo.IsLocked(2, t.AddMinutes(30), t.AddMinutes(90)));  // 他頻道
    }

    [Fact]
    public void Revoke_RemovesFromActive_KeepsInAllWithAudit()
    {
        var id = _repo.Add(1, _base, _base.AddHours(1), "r", "alice", _base);
        Assert.Single(_repo.ListActive());

        Assert.True(_repo.Revoke(id, "bob", "案件結案，沖銷", _base.AddDays(1)));

        Assert.Empty(_repo.ListActive());
        Assert.False(_repo.IsLocked(1, _base.AddMinutes(10), _base.AddMinutes(20)));
        var all = Assert.Single(_repo.ListAll());
        Assert.False(all.IsActive);
        Assert.Equal("bob", all.RevokedBy);
        Assert.Equal("案件結案，沖銷", all.RevokedReason);
        Assert.Equal(_base.AddDays(1), all.RevokedAtUtc);
    }

    [Fact]
    public void Revoke_NonExistentOrAlreadyRevoked_ReturnsFalse()
    {
        Assert.False(_repo.Revoke(999, "bob", "x", _base));

        var id = _repo.Add(1, _base, _base.AddHours(1), "r", "alice", _base);
        Assert.True(_repo.Revoke(id, "bob", "x", _base));
        Assert.False(_repo.Revoke(id, "carol", "y", _base));
    }

    [Fact]
    public void ListAll_OrdersDescending_AndMapsFields()
    {
        var older = _repo.Add(1, _base, _base.AddHours(1), "old", "alice", _base);
        var newer = _repo.Add(1, _base.AddDays(1), _base.AddDays(1).AddHours(1), "new", "bob", _base);
        _repo.Revoke(older, "bob", "released", _base.AddDays(2));

        var all = _repo.ListAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(newer, all[0].Id);
        Assert.Equal(older, all[1].Id);
        Assert.Null(all[0].RevokedAtUtc);
        Assert.Equal("released", all[1].RevokedReason);
        Assert.True(_repo.AnyActive());
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }
}