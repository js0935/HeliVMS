using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 磁碟滿策略的協調與稽核（M319，§11.1）。
///
/// <para>
/// 這裡測的是「維運看不看得到磁碟快滿了」：等級判定本身在 <see cref="DiskPressurePolicyTests"/>，
/// 但若協調器每輪都寫稽核（或等級改變時反而沒寫），症狀是稽核表被灌滿雜訊、真正的爆碟事件被淹掉，
/// 而磁碟爆掉當下沒有任何紀錄可追。故以注入的磁碟用量固定輸入，驗證「只有改變才記一筆」。
/// </para>
/// </summary>
public sealed class DiskPressureCoordinatorTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;
    private readonly string _fallbackRoot =
        Path.Combine(Path.GetTempPath(), $"helivms-disk-{Guid.NewGuid():N}");

    public DiskPressureCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-disk-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
    }

    /// <summary>首次進入預警就要留下稽核——磁碟第一次超過 85% 是操作員該知道的維運事件。</summary>
    [Fact]
    public void 首次達預警時寫下一筆稽核()
    {
        var level = DiskPressureCoordinator.RunConfigured(
            _store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 10 * GiB));

        Assert.Equal(DiskPressureLevel.Warning, level);
        Assert.Equal(1, AuditCount());
    }

    /// <summary>
    /// 同一個等級連續回報不重複寫稽核。保留迴圈每小時跑一次，若每圈都記一筆，
    /// 一台長期超過 85% 的機器會用雜訊把真正的事件淹掉。
    /// </summary>
    [Fact]
    public void 等級不變時不重複寫稽核()
    {
        var usage = UsageOf(100 * GiB, 10 * GiB);
        DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, usage);
        DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, usage);

        Assert.Equal(1, AuditCount());
    }

    /// <summary>等級改變（預警→危急）要再記一筆，讓惡化過程看得出時間點。</summary>
    [Fact]
    public void 等級改變時再寫一筆稽核()
    {
        DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 10 * GiB));
        var level = DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 1 * GiB));

        Assert.Equal(DiskPressureLevel.Critical, level);
        Assert.Equal(2, AuditCount());
    }

    /// <summary>危機解除（危急→正常）也要記一筆並更新狀態，否則「已恢復」不留痕跡。</summary>
    [Fact]
    public void 回到正常時也寫一筆稽核並更新狀態()
    {
        DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 1 * GiB));
        var level = DiskPressureCoordinator.RunConfigured(_store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 50 * GiB));

        Assert.Equal(DiskPressureLevel.Normal, level);
        Assert.Equal(2, AuditCount());
        Assert.Equal("Normal", new SettingsRepository(_store).Get(DiskPressureCoordinator.LevelKey));
    }

    /// <summary>一直正常就不該有任何稽核——正常運作不產生事件。</summary>
    [Fact]
    public void 正常等級不寫稽核()
    {
        var level = DiskPressureCoordinator.RunConfigured(
            _store, _fallbackRoot, DateTime.UtcNow, UsageOf(100 * GiB, 50 * GiB));

        Assert.Equal(DiskPressureLevel.Normal, level);
        Assert.Equal(0, AuditCount());
    }

    /// <summary>
    /// 多顆錄影磁碟時回報<b>最有壓力</b>的一顆（任一顆超過 85% 就要讓操作員看到），
    /// 即使其他碟還很空。
    /// </summary>
    [Fact]
    public void 多顆磁碟回報最嚴重的一顆()
    {
        SetDisks(("D:\\", true), ("E:\\", false));

        var level = DiskPressureCoordinator.EvaluateConfigured(
            _store, null, UsageByDrive(('D', (100 * GiB, 0)), ('E', (100 * GiB, 50 * GiB))));

        Assert.Equal(DiskPressureLevel.Critical, level);
    }

    /// <summary>
    /// 但只要還有一顆錄影磁碟寫得下，就<b>不能</b>停新錄——多磁碟站台不該因某一顆滿了而全面停錄。
    /// </summary>
    [Fact]
    public void 多顆磁碟不會因一顆滿了就停新錄()
    {
        SetDisks(("D:\\", true), ("E:\\", false));

        var stop = DiskPressureCoordinator.ShouldStopNewRecording(
            _store, null, UsageByDrive(('D', (100 * GiB, 0)), ('E', (100 * GiB, 50 * GiB))));

        Assert.False(stop);
    }

    /// <summary>所有錄影磁碟都低於寫入門檻時才停新錄（第三階段判準）。</summary>
    [Fact]
    public void 全部磁碟都低於門檻時停新錄()
    {
        var stop = DiskPressureCoordinator.ShouldStopNewRecording(
            _store, _fallbackRoot, UsageOf(100 * GiB, 0));

        Assert.True(stop);
    }

    /// <summary>空間充足時不停新錄。</summary>
    [Fact]
    public void 空間充足時不停新錄()
    {
        var stop = DiskPressureCoordinator.ShouldStopNewRecording(
            _store, _fallbackRoot, UsageOf(100 * GiB, 50 * GiB));

        Assert.False(stop);
    }

    private int AuditCount() => _audit.Count(new AuditLogQuery { Action = DiskPressureCoordinator.AuditAction });

    private static Func<string, (long, long)> UsageOf(long total, long free) => _ => (total, free);

    private static Func<string, (long, long)> UsageByDrive(params (char Drive, (long Total, long Free) Usage)[] map)
        => root => map.First(m => char.ToUpperInvariant(m.Drive) == char.ToUpperInvariant(root[0])).Usage;

    private void SetDisks(params (string Root, bool Primary)[] disks)
    {
        var dtos = disks.Select(d => new RecordingStorage.RecordingDiskDto { Root = d.Root, Primary = d.Primary });
        new SettingsRepository(_store).Set(RecordingStorage.DisksSettingKey, RecordingStorage.SerializeDisks(dtos));
    }

    public void Dispose()
    {
        _store.Dispose();
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
