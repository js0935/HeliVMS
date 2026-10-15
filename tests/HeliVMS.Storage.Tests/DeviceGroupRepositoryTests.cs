using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 裝置群組樹的資料庫不變式（v50，§7.4）。
///
/// <para>
/// 這棵樹有兩個最容易做錯、又最難在畫面上發現的地方。第一是刪除：刪一個分區若誤用 CASCADE，
/// 整批攝影機會連同錄影索引一起被清掉——操作員只是想移除一個資料夾。因此子群組與設備都必須是
/// <c>SET NULL</c>（回到未分組），不是消失。第二是搬移成環：把群組拖到自己的子樹底下會讓樹變成環，
/// UI 遞迴展開時無限迴圈、整個裝置頁卡死，故搬移一定要擋。
/// </para>
/// </summary>
public sealed class DeviceGroupRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly DeviceGroupRepository _groups;
    private readonly DeviceRepository _devices;

    public DeviceGroupRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-dgrp-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _groups = new DeviceGroupRepository(_store, new AuditLogRepository(_store));
        _devices = new DeviceRepository(_store, new AuditLogRepository(_store));
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
    public void 建立頂層群組會依建立順序排序()
    {
        var a = _groups.Create("一樓");
        var b = _groups.Create("二樓");

        var list = _groups.List();

        Assert.Equal(new[] { a, b }, list.Select(g => g.Id).ToArray());
        Assert.All(list, g => Assert.Null(g.ParentId));
        Assert.Equal(0, list[0].Position);
        Assert.Equal(1, list[1].Position);
    }

    [Fact]
    public void 建立子群組會掛在上層之下()
    {
        var root = _groups.Create("廠區");
        var child = _groups.Create("北棟", root);

        Assert.Equal(root, _groups.Get(child)!.ParentId);
    }

    [Fact]
    public void 群組名稱全樹唯一()
    {
        var root = _groups.Create("廠區");
        _groups.Create("北棟", root);

        // 樹狀 UI 只以名稱當標籤；允許不同上層同名會讓操作員無從分辨要拖到哪個節點。
        Assert.Throws<ArgumentException>(() => _groups.Create("廠區"));
        Assert.Throws<ArgumentException>(() => _groups.Create("北棟"));
        Assert.Throws<ArgumentException>(() => _groups.Create("  北棟  "));
    }

    [Fact]
    public void 建立時指定不存在的上層會被拒絕()
    {
        Assert.Throws<ArgumentException>(() => _groups.Create("孤兒", 999));
    }

    [Fact]
    public void 重新命名不可撞名但可保留原名()
    {
        var a = _groups.Create("一樓");
        _groups.Create("二樓");

        _groups.Rename(a, "一樓");
        _groups.Rename(a, "大廳");

        Assert.Equal("大廳", _groups.Get(a)!.Name);
        Assert.Throws<ArgumentException>(() => _groups.Rename(a, "二樓"));
    }

    [Fact]
    public void 設備指派後可讀回群組成員()
    {
        var g = _groups.Create("一樓");
        var d1 = AddDevice("大門", "10.0.0.1");
        var d2 = AddDevice("後門", "10.0.0.2");

        _groups.AssignDevice(d1, g);
        _groups.AssignDevice(d2, g);

        Assert.Equal(new[] { d1, d2 }, _groups.ListDeviceIds(g).OrderBy(x => x).ToArray());
        Assert.Equal(g, _groups.GroupOfDevice(d1));
    }

    [Fact]
    public void 設備改派到新群組會離開舊群組()
    {
        // 設備「一台一組」由 devices.group_id 單一欄位保證：改派就是覆寫，舊群組自動少一台。
        var g1 = _groups.Create("一樓");
        var g2 = _groups.Create("二樓");
        var d1 = AddDevice("大門", "10.0.0.1");

        _groups.AssignDevice(d1, g1);
        _groups.AssignDevice(d1, g2);

        Assert.Empty(_groups.ListDeviceIds(g1));
        Assert.Equal(g2, _groups.GroupOfDevice(d1));
    }

    [Fact]
    public void 移出群組後未分組()
    {
        var g = _groups.Create("一樓");
        var d1 = AddDevice("大門", "10.0.0.1");
        _groups.AssignDevice(d1, g);

        _groups.AssignDevice(d1, null);

        Assert.Empty(_groups.ListDeviceIds(g));
        Assert.Null(_groups.GroupOfDevice(d1));
    }

    [Fact]
    public void 新設備預設未分組()
    {
        var d1 = AddDevice("大門", "10.0.0.1");

        Assert.Null(_groups.GroupOfDevice(d1));
    }

    [Fact]
    public void 刪除群組只讓設備回到未分組而不刪除()
    {
        // 關鍵回歸：若外鍵誤用 CASCADE，刪一個「分區」會連帶刪掉整批攝影機與其錄影索引。
        var g = _groups.Create("一樓");
        var d1 = AddDevice("大門", "10.0.0.1");
        _groups.AssignDevice(d1, g);

        _groups.Delete(g);

        Assert.NotNull(_devices.Get(d1));
        Assert.Null(_groups.GroupOfDevice(d1));
    }

    [Fact]
    public void 刪除上層群組子群組回到頂層()
    {
        var root = _groups.Create("廠區");
        var child = _groups.Create("北棟", root);

        _groups.Delete(root);

        var got = _groups.Get(child);
        Assert.NotNull(got);
        Assert.Null(got!.ParentId);
    }

    [Fact]
    public void 不可把群組移到自己底下()
    {
        var a = _groups.Create("一樓");

        Assert.Throws<ArgumentException>(() => _groups.Move(a, a));
    }

    [Fact]
    public void 不可把群組移到自己的子樹底下()
    {
        // 成環會讓樹狀 UI 遞迴展開無限迴圈。
        var root = _groups.Create("廠區");
        var mid = _groups.Create("北棟", root);
        var leaf = _groups.Create("三樓", mid);

        Assert.Throws<ArgumentException>(() => _groups.Move(root, leaf));
        Assert.Throws<ArgumentException>(() => _groups.Move(root, mid));
    }

    [Fact]
    public void 移動到新上層會排到末端()
    {
        var root = _groups.Create("廠區");
        _groups.Create("南棟", root);
        var moving = _groups.Create("獨立");

        _groups.Move(moving, root);

        var children = _groups.List().Where(g => g.ParentId == root).OrderBy(g => g.Position).ToArray();
        Assert.Equal(moving, children[^1].Id);
    }

    [Fact]
    public void 移到頂層可從子樹取出()
    {
        var root = _groups.Create("廠區");
        var child = _groups.Create("北棟", root);

        _groups.Move(child, null);

        Assert.Null(_groups.Get(child)!.ParentId);
    }

    [Fact]
    public void 刪除設備後群組不再列出()
    {
        var g = _groups.Create("一樓");
        var d1 = AddDevice("大門", "10.0.0.1");
        _groups.AssignDevice(d1, g);

        _devices.Delete(d1);

        Assert.Empty(_groups.ListDeviceIds(g));
        Assert.Null(_groups.GroupOfDevice(d1));
    }

    private int AddDevice(string name, string ip)
        => _devices.Add(name, ip, 80, "admin", "pw", "manual");
}
