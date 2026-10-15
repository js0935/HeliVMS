using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 監看群組的資料庫不變式（v49）。
///
/// <para>
/// 「一台攝影機最多屬於一個群組」是這個功能最容易做錯、又最難在畫面上看出來的一點：
/// 若歸屬表允許同一台同時屬於兩個群組，該頻道會在兩個頁簽都出現，操作員以為自己只放了一個頁簽；
/// 若改派時沒清掉舊歸屬，則會出現幽靈成員。這裡直接對資料層驗證，不依賴 UI。
/// </para>
/// <para>
/// 另一個容易漏的是刪除路徑：<c>channels</c> 與 <c>channel_groups</c> 都是成員表的外鍵父表。
/// 少了 CASCADE，刪掉頻道或群組後會留下孤兒成員列，群組頁簽上就會少一格卻無任何錯誤。
/// </para>
/// </summary>
public sealed class ChannelGroupRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly ChannelGroupRepository _groups;
    private readonly ChannelRepository _channels;

    public ChannelGroupRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-grp-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _groups = new ChannelGroupRepository(_store);
        _channels = new ChannelRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 建立群組會依建立順序排頁簽()
    {
        var a = _groups.Create("出入口");
        var b = _groups.Create("一樓");

        var list = _groups.List();

        Assert.Equal(new[] { a, b }, list.Select(g => g.Id).ToArray());
        Assert.Equal("出入口", list[0].Name);
        Assert.Equal(0, list[0].Position);
        Assert.Equal(1, list[1].Position);
    }

    [Fact]
    public void 群組名稱重複會被拒絕()
    {
        _groups.Create("出入口");

        Assert.Throws<ArgumentException>(() => _groups.Create("出入口"));
        Assert.Throws<ArgumentException>(() => _groups.Create("  出入口  "));
    }

    [Fact]
    public void 重新命名不可撞其他群組但可保留原名()
    {
        var a = _groups.Create("出入口");
        _groups.Create("一樓");

        // 改成自己的名字（去空白後相同）不應被視為重複。
        _groups.Rename(a, "出入口");
        _groups.Rename(a, "大門口");

        Assert.Equal("大門口", _groups.Get(a)!.Name);
        Assert.Throws<ArgumentException>(() => _groups.Rename(a, "一樓"));
    }

    [Fact]
    public void 指派頻道後可讀回群組成員()
    {
        var g = _groups.Create("出入口");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");
        var c2 = _channels.Add("後門", "rtsp://10.0.0.2/1");

        _groups.Assign(c1, g);
        _groups.Assign(c2, g);

        Assert.Equal(new[] { c1, c2 }, _groups.ListChannelIds(g).ToArray());
    }

    [Fact]
    public void 一台頻道改派到新群組會離開舊群組()
    {
        // 這是「一台只屬於一個群組」的核心保證：改派若沒清舊歸屬，兩頁簽都會出現同一台。
        var g1 = _groups.Create("出入口");
        var g2 = _groups.Create("一樓");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");

        _groups.Assign(c1, g1);
        _groups.Assign(c1, g2);

        Assert.Empty(_groups.ListChannelIds(g1));
        Assert.Equal(new[] { c1 }, _groups.ListChannelIds(g2).ToArray());
        Assert.Equal(g2, _groups.GroupOf(c1));
    }

    [Fact]
    public void 重複指派到同一群組不會產生重複成員()
    {
        var g = _groups.Create("出入口");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");

        _groups.Assign(c1, g);
        _groups.Assign(c1, g);

        Assert.Equal(new[] { c1 }, _groups.ListChannelIds(g).ToArray());
    }

    [Fact]
    public void 搬移成員會改變群組內順序()
    {
        var g = _groups.Create("出入口");
        var a = _channels.Add("A", "rtsp://10.0.0.1/1");
        var b = _channels.Add("B", "rtsp://10.0.0.2/1");
        var c = _channels.Add("C", "rtsp://10.0.0.3/1");
        _groups.Assign(a, g);
        _groups.Assign(b, g);
        _groups.Assign(c, g);

        // 把 C 往前一個位置：A、C、B（搬移而非交換）。
        _groups.Move(g, c, -1);

        Assert.Equal(new[] { a, c, b }, _groups.ListChannelIds(g).ToArray());
    }

    [Fact]
    public void 搬移超出端點會夾住且不亂序()
    {
        var g = _groups.Create("出入口");
        var a = _channels.Add("A", "rtsp://10.0.0.1/1");
        var b = _channels.Add("B", "rtsp://10.0.0.2/1");
        _groups.Assign(a, g);
        _groups.Assign(b, g);

        // A 已經在第一個，再往前仍是 A、B；B 往後越界仍在最後。
        _groups.Move(g, a, -5);
        _groups.Move(g, b, 5);

        Assert.Equal(new[] { a, b }, _groups.ListChannelIds(g).ToArray());
    }

    [Fact]
    public void 移出群組後不再屬於任何群組()
    {
        var g = _groups.Create("出入口");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");
        _groups.Assign(c1, g);

        _groups.Remove(c1);

        Assert.Empty(_groups.ListChannelIds(g));
        Assert.Null(_groups.GroupOf(c1));
    }

    [Fact]
    public void 刪除群組會一併清除成員歸屬()
    {
        var g = _groups.Create("出入口");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");
        _groups.Assign(c1, g);

        _groups.Delete(g);

        Assert.Null(_groups.GroupOf(c1));
    }

    [Fact]
    public void 刪除頻道會一併清除群組成員()
    {
        // 沒有 CASCADE 就會留下指向不存在頻道的孤兒成員列，群組頁簽上少一格卻查不出原因。
        var g = _groups.Create("出入口");
        var c1 = _channels.Add("大門", "rtsp://10.0.0.1/1");
        var c2 = _channels.Add("後門", "rtsp://10.0.0.2/1");
        _groups.Assign(c1, g);
        _groups.Assign(c2, g);

        _channels.Delete(c1);

        Assert.Equal(new[] { c2 }, _groups.ListChannelIds(g).ToArray());
        Assert.Null(_groups.GroupOf(c1));
    }
}
