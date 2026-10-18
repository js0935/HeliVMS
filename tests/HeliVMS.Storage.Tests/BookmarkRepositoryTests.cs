namespace HeliVMS.Storage.Tests;

/// <summary>
/// 回放書籤倉儲（§8.5 §11.4）。訴求很單純：標了要記得住、查得回、改得動、刪得掉，
/// 而且不會把不同通道的書籤混在一起——混到會讓操作員跳到別人攝影機的時刻還以為是同一台。
/// </summary>
public class BookmarkRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly BookmarkRepository _repo;
    private readonly DateTime _base;

    public BookmarkRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-bookmark-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new BookmarkRepository(_store);
        _base = new DateTime(2026, 9, 12, 14, 30, 0, DateTimeKind.Utc);
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
    public void 新增書籤後能依通道與時間範圍查回且欄位完整()
    {
        var id = _repo.Add(1, _base, "進貨異常", "alice", _base.AddMinutes(5));

        var rows = _repo.ListByRange(1, _base.AddDays(-1), _base.AddDays(1));

        var b = Assert.Single(rows);
        Assert.Equal(id, b.Id);
        Assert.Equal(1, b.ChannelId);
        Assert.Equal(_base, b.AtUtc);
        Assert.Equal("進貨異常", b.Note);
        Assert.Equal("alice", b.CreatedBy);
        Assert.Equal(_base.AddMinutes(5), b.CreatedAtUtc);
    }

    [Fact]
    public void 查詢範圍是左閉右開()
    {
        _repo.Add(1, _base, "起點", "alice", _base);
        _repo.Add(1, _base.AddHours(1), "結束點", "alice", _base);

        var atStart = _repo.ListByRange(1, _base, _base.AddHours(1));
        var afterStart = _repo.ListByRange(1, _base.AddMilliseconds(1), _base.AddHours(1));

        Assert.Single(atStart);                 // 起點含
        Assert.Equal("起點", atStart[0].Note);
        Assert.Empty(afterStart);               // 終點不含
    }

    [Fact]
    public void 不同通道的書籤不會互相汙染()
    {
        _repo.Add(1, _base, "ch1", "alice", _base);
        _repo.Add(2, _base, "ch2", "alice", _base);

        var ch1 = _repo.ListByRange(1, _base.AddDays(-1), _base.AddDays(1));
        var ch2 = _repo.ListByRange(2, _base.AddDays(-1), _base.AddDays(1));

        Assert.Equal("ch1", Assert.Single(ch1).Note);
        Assert.Equal("ch2", Assert.Single(ch2).Note);
    }

    [Fact]
    public void 改寫筆記後查回的是新內容且不存在的書籤改寫會回報失敗()
    {
        var id = _repo.Add(1, _base, "舊筆記", "alice", _base);

        Assert.True(_repo.UpdateNote(id, "新筆記"));
        Assert.Equal("新筆記", Assert.Single(_repo.ListByRange(1, _base.AddDays(-1), _base.AddDays(1))).Note);

        Assert.False(_repo.UpdateNote(id + 999, "不存在"));
    }

    [Fact]
    public void 刪除書籤後查不到且重複刪除回報失敗()
    {
        var id = _repo.Add(1, _base, "待刪", "alice", _base);

        Assert.True(_repo.Remove(id));
        Assert.Empty(_repo.ListByRange(1, _base.AddDays(-1), _base.AddDays(1)));
        Assert.False(_repo.Remove(id));
    }

    [Fact]
    public void 空白筆記可以建立並原樣保存()
    {
        _repo.Add(1, _base, "  ", "alice", _base);

        // 空白不摺疊、不 Trim：允許先標點、稍後再補字（§8.5「加入書籤 + 筆記」）。
        Assert.Equal("  ", Assert.Single(_repo.ListByRange(1, _base.AddDays(-1), _base.AddDays(1))).Note);
    }

    [Fact]
    public void 建立書籤時頻道編號必須為正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.Add(0, _base, "x", "alice", _base));
    }

    [Fact]
    public void 全部書籤清單依時刻由新到舊()
    {
        _repo.Add(1, _base.AddHours(2), "後", "alice", _base);
        _repo.Add(1, _base, "前", "alice", _base);

        var all = _repo.ListAll();

        Assert.Equal(["後", "前"], all.Select(b => b.Note).ToList());
    }
}
