using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 事件快照雜湊索引（視覺搜索 QC_Live PopupVisualSearch 對齊）的存取層。
/// 釘三件事：雜湊往返（ulong 高位位元經 SQLite 有號整數不壞）、
/// 補算清單只含「有快照沒雜湊」、候選清單的時間窗過濾。
/// </summary>
public sealed class AlarmEventSnapshotHashTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _dbPath;

    public AlarmEventSnapshotHashTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-vs-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        // alarm_events.channel_id 有 FK：種子頻道（1、2）之後才能插事件。
        new ChannelRepository(_store).EnsureSeedChannels();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public void 雜湊往返經有號整數不壞()
    {
        var repo = new AlarmEventRepository(_store);
        var id = repo.Insert(1, "motion", DateTime.UtcNow.AddMinutes(-5));
        var hash = 0xDEAD_BEEF_DEAD_BEEFul; // 高位位元組 1：有號整數是負值，不能在這裡壞掉

        repo.SetSnapshotHash(id, hash);

        var candidates = repo.ListHashCandidates(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), 10);
        var row = Assert.Single(candidates);
        Assert.Equal(hash, row.Hash);
    }

    [Fact]
    public void 補算清單只含有快照而沒雜湊的舊事件()
    {
        var repo = new AlarmEventRepository(_store);
        var withPath = repo.Insert(1, "motion", DateTime.UtcNow.AddMinutes(-30), snapshotPath: @"C:\snap\a.bmp");
        var noPath = repo.Insert(2, "motion", DateTime.UtcNow.AddMinutes(-20));
        var already = repo.Insert(1, "motion", DateTime.UtcNow.AddMinutes(-10), snapshotPath: @"C:\snap\b.bmp");
        repo.SetSnapshotHash(already, 0x1234);

        var backlog = repo.ListSnapshotHashBacklog(50);

        var ids = backlog.Select(b => b.Id).ToList();
        Assert.Equal([withPath], ids);
        Assert.DoesNotContain(noPath, ids);
        Assert.DoesNotContain(already, ids);
    }

    [Fact]
    public void 候選清單受時間窗與筆數上限約束()
    {
        var repo = new AlarmEventRepository(_store);
        var old = repo.Insert(1, "motion", DateTime.UtcNow.AddDays(-2), snapshotPath: @"C:\snap\old.bmp");
        repo.Insert(2, "motion", DateTime.UtcNow.AddMinutes(-30), snapshotPath: @"C:\snap\new1.bmp");
        repo.Insert(1, "motion", DateTime.UtcNow.AddMinutes(-10), snapshotPath: @"C:\snap\new2.bmp");
        repo.SetSnapshotHash(old, 1);
        repo.SetSnapshotHash(2, 2);
        repo.SetSnapshotHash(3, 3);

        var candidates = repo.ListHashCandidates(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddHours(1), 1);

        var single = Assert.Single(candidates);
        // 由新到舊：只取 1 筆時是最新那筆（id 3）。
        Assert.Equal(3, single.Id);
    }
}
