using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 磁碟健康協調（<see cref="DiskHealthCoordinator"/>）：只回報錄影磁碟、等級改變才寫稽核、
/// 讀不到一律未知且不影響任何事。
/// </summary>
/// <remarks>
/// <para>
/// WMI 在 CI 上讀不到（沒有實體碟的 S.M.A.R.T.、也可能不是管理員），所以取樣一律注入；
/// 這裡要釘的是<b>政策</b>：哪些磁碟算數、什麼時候寫稽核、讀不到時回什麼。
/// </para>
/// <para>
/// 「等級改變才寫稽核」不是省事：一顆預測故障的硬碟會連續好幾天維持同等級，
/// 每小時一筆會把稽核表灌滿、把真正的事件淹掉（與磁碟壓力同一條規則）。
/// </para>
/// </remarks>
public class DiskHealthCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SettingsRepository _settings;
    private readonly AuditLogRepository _audit;
    private readonly string _recordingsRoot;

    public DiskHealthCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-diskhealth-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _settings = new SettingsRepository(_store);
        _audit = new AuditLogRepository(_store);

        // 錄影根目錄設在暫存目錄所在的磁碟（測試機上可能是 C: 或 D:）——重點是
        // 「設定的那顆碟」與「來源回報的碟」要對得上。
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-diskhealth-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        _settings.Set(
            RecordingStorage.RootSettingKey,
            _recordingsRoot);
    }

    private static readonly DateTime Now = new(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 只回報錄影磁碟的健康()
    {
        var thisDrive = Path.GetPathRoot(_recordingsRoot)!;         // 錄影碟
        var otherDrive = thisDrive.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? @"D:\" : @"C:\";

        var source = new FakeSource(
            new VolumeDiskHealth(thisDrive, "WD Purple", "SN1", new SmartAttributes(false, 0, 0, 33, 100)),
            new VolumeDiskHealth(otherDrive, "System SSD", "SN2", new SmartAttributes(true, null, null, null, null)));

        var report = DiskHealthCoordinator.ReadConfigured(_store, _recordingsRoot, source);

        // 系統碟預測故障不干錄影的事（錄影不在那顆上面）。
        var volume = Assert.Single(report.Volumes);
        Assert.Equal(thisDrive, volume.Root);
        Assert.Equal(DiskHealthLevel.Healthy, report.Worst);
        Assert.Equal(0, report.Failing);
    }

    [Fact]
    public void 等級改變時才寫稽核並記下執行期狀態()
    {
        var drive = Path.GetPathRoot(_recordingsRoot)!;

        // 先擺一個舊狀態（模擬「上一次跑是預測故障」），才驗得到「等級改變」這一條——
        // 否則第一次跑剛好健康時本來就不寫（與磁碟壓力同一條去重規則）。
        _settings.Set(DiskHealthCoordinator.LevelKey, DiskHealthLevel.Failing.ToString());

        // 第一次：恢復健康 → 寫一筆（危機解除也要留痕跡）。
        var healthy = new FakeSource(new VolumeDiskHealth(drive, "WD Purple", "SN1", new SmartAttributes(false, 0, 0, 33, 100)));
        _ = DiskHealthCoordinator.RunConfigured(_store, _recordingsRoot, Now, healthy, _audit);
        Assert.Equal(1, CountHealthAudits());
        Assert.Equal(DiskHealthLevel.Healthy.ToString(), _settings.Get(DiskHealthCoordinator.LevelKey));

        // 第二次：同樣健康 → 不再寫（否則每小時一筆灌爆稽核表）。
        _ = DiskHealthCoordinator.RunConfigured(_store, _recordingsRoot, Now.AddHours(1), healthy, _audit);
        Assert.Equal(1, CountHealthAudits());

        // 第三次：變成預測故障 → 寫一筆，且明細要能看出是哪一顆。
        var failing = new FakeSource(new VolumeDiskHealth(
            drive,
            "WD Purple",
            "SN1",
            new SmartAttributes(true, 0, 5, 41, 100)));
        var report = DiskHealthCoordinator.RunConfigured(_store, _recordingsRoot, Now.AddHours(2), failing, _audit);

        Assert.Equal(2, CountHealthAudits());
        Assert.Equal(DiskHealthLevel.Failing, report.Worst);
        Assert.Equal(1, report.Failing);
        Assert.Equal(DiskHealthLevel.Failing.ToString(), _settings.Get(DiskHealthCoordinator.LevelKey));

        var detail = _audit
            .List(new AuditLogQuery { Action = DiskHealthCoordinator.AuditAction, Limit = 10 })
            .First().Detail ?? string.Empty;
        Assert.Contains("level=failing", detail, StringComparison.Ordinal);
        Assert.Contains("predict=true", detail, StringComparison.Ordinal);
        Assert.Contains("pending=5", detail, StringComparison.Ordinal);
        Assert.Contains("WD Purple", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 讀不到時回未知且不丟例外()
    {
        var report = DiskHealthCoordinator.ReadConfigured(_store, _recordingsRoot, new ThrowingSource());

        Assert.Empty(report.Volumes);
        Assert.Equal(DiskHealthLevel.Unknown, report.Worst);

        // 未知也算一種「等級」：第一次跑會留一筆（維運才看得到「這台讀不到 S.M.A.R.T.」），
        // 之後維持未知就不再寫（去重）。
        _ = DiskHealthCoordinator.RunConfigured(_store, _recordingsRoot, Now, new ThrowingSource(), _audit);
        Assert.Equal(1, CountHealthAudits());
        Assert.Equal(DiskHealthLevel.Unknown.ToString(), _settings.Get(DiskHealthCoordinator.LevelKey));

        _ = DiskHealthCoordinator.RunConfigured(_store, _recordingsRoot, Now.AddHours(1), new ThrowingSource(), _audit);
        Assert.Equal(1, CountHealthAudits());
    }

    [Fact]
    public void 來源完全讀不到那顆碟時回未知而不是健康()
    {
        // 來源有回一筆，但該碟的 S.M.A.R.T. 欄位全空（不是管理員、控制器不外露）。
        var drive = Path.GetPathRoot(_recordingsRoot)!;
        var source = new FakeSource(new VolumeDiskHealth(drive, "Some Disk", null, null));

        var report = DiskHealthCoordinator.ReadConfigured(_store, _recordingsRoot, source);

        Assert.Equal(DiskHealthLevel.Unknown, Assert.Single(report.Volumes).Level);
        Assert.Equal(DiskHealthLevel.Unknown, report.Worst);
    }

    private int CountHealthAudits()
        => _audit.List(new AuditLogQuery { Action = DiskHealthCoordinator.AuditAction, Limit = 50 }).Count;

    private sealed class FakeSource(params VolumeDiskHealth[] volumes) : IDiskHealthSource
    {
        public IReadOnlyList<VolumeDiskHealth> Read() => volumes;
    }

    private sealed class ThrowingSource : IDiskHealthSource
    {
        public IReadOnlyList<VolumeDiskHealth> Read()
            => throw new InvalidOperationException("WMI 掛了");
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var path in new[] { _dbPath, _recordingsRoot })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
