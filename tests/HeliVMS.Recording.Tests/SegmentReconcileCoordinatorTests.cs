using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 索引對帳的協調入口：桌面與 Headless Agent 共用的那一條路徑。
///
/// <para>
/// 這裡守的是「設定怎麼解讀」與「什麼時候該留下稽核」——對帳判定本身由
/// <see cref="SegmentIndexReconcilerTests"/> 負責。稽核的兩個方向都要釘：真的標了遺失
/// 必須留紀錄（否則磁碟被清空沒人知道），沒標的東西則一次稽核都不准寫
/// （一小時一輪、每次寫一筆，稽核日誌會被灌滿，真正的事件就沉下去了）。
/// </para>
/// </summary>
public class SegmentReconcileCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _repo;
    private readonly string _liveFolder;
    private readonly DateTime _now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private int _seed;

    public SegmentReconcileCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-reconcile-coord-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new SegmentRepository(_store);
        _liveFolder = Path.Combine(Path.GetTempPath(), $"helivms-coord-live-{Guid.NewGuid():N}", "20261019");
        Directory.CreateDirectory(_liveFolder);
        EnsureChannel(1);
    }

    [Fact]
    public void 設定為零時整支停用()
    {
        new SettingsRepository(_store).Set(SegmentReconcileCoordinator.BatchSizeKey, "0");
        SeedDeferred(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id);

        var report = SegmentReconcileCoordinator.RunConfigured(
            _store, _now, fileExists: _ => false, isVolumeReady: _ => true);

        Assert.Equal(default, report);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public void 標記遺失時寫入稽核()
    {
        SeedDeferred(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id);

        var report = SegmentReconcileCoordinator.RunConfigured(
            _store, _now, fileExists: _ => false, isVolumeReady: _ => true);

        Assert.Equal(1, report.MarkedMissing);
        Assert.Equal(1, report.FoldersRemoved);
        Assert.Equal(SegmentStatus.Missing, StatusOf(id));
        Assert.Equal(1, AuditCount());
    }

    [Fact]
    public void 沒有標記時不寫稽核()
    {
        SeedDeferred(Path.Combine(_liveFolder, "seg-100000000.mp4"), out _);

        var report = SegmentReconcileCoordinator.RunConfigured(
            _store, _now, fileExists: _ => true, isVolumeReady: _ => true);

        Assert.Equal(0, report.MarkedMissing);
        Assert.True(report.Checked > 0, "應該真的有檢查到段，否則這條測試證明不了『沒標就沒稽核』");
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public void 磁碟未就緒時不寫稽核()
    {
        SeedDeferred(@"D:\rec\cam\20261019\seg-100000000.mp4", out var id);

        var report = SegmentReconcileCoordinator.RunConfigured(
            _store, _now, fileExists: _ => false, isVolumeReady: _ => false);

        Assert.Equal(0, report.MarkedMissing);
        Assert.True(report.Unverified > 0);
        Assert.Equal(SegmentStatus.Final, StatusOf(id));
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public void 依設定決定逐檔檢查段數()
    {
        new SettingsRepository(_store).Set(SegmentReconcileCoordinator.BatchSizeKey, "1");
        SeedDeferred(Path.Combine(_liveFolder, "seg-100000000.mp4"), out _);
        SeedDeferred(Path.Combine(_liveFolder, "seg-100010000.mp4"), out _);

        var report = SegmentReconcileCoordinator.RunConfigured(
            _store, _now, fileExists: _ => false, isVolumeReady: _ => true);

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.MarkedMissing);
    }

    private int AuditCount()
        => new AuditLogRepository(_store).Count(new AuditLogQuery
        {
            Action = "recording.missing",
            Category = AuditCategories.Recording,
        });

    private void SeedDeferred(string path, out long id)
    {
        // 每個段給不同的起點：索引對 (channel_id, stream, start_time) 有唯一鍵。
        var start = _now.AddHours(-1).AddSeconds(_seed++);
        id = _repo.BeginSegment(1, "main", path, start);
        _repo.CompleteSegment(id, start.AddSeconds(60), 1000, 60, "sha256");
    }

    private SegmentStatus StatusOf(long id)
        => _store.Query(
            "SELECT status FROM segments WHERE id = $id;",
            static r =>
            {
                r.Read();
                return r.GetString(0) switch
                {
                    "tmp" => SegmentStatus.Temporary,
                    "final" => SegmentStatus.Final,
                    "corrupt" => SegmentStatus.Corrupt,
                    "missing" => SegmentStatus.Missing,
                    "retired" => SegmentStatus.Retired,
                    var other => throw new InvalidOperationException($"未知狀態 {other}"),
                };
            },
            cmd => cmd.Parameters.AddWithValue("$id", id));

    private void EnsureChannel(int id)
    {
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES ($id, $n, $u, 0);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", $"ch-{id}");
                cmd.Parameters.AddWithValue("$u", $"rtsp://test/{id}");
            });
    }

    public void Dispose()
    {
        _store.Dispose();
        var parent = Directory.GetParent(_liveFolder)?.FullName;
        if (parent is not null)
        {
            try
            {
                Directory.Delete(parent, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
