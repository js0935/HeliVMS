using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 錄影完整性自我檢查（M318，§11.1 寫盤保護、§15.7）。
///
/// <para>
/// 這裡守的主要是一條安全紅線：查到壞段時**只記錄、不改狀態、不刪檔**。
/// 保留清理的用量統計與汰除候選都以 <c>status='final'</c> 為準，一旦自我檢查把壞段標成
/// <c>corrupt</c>，它會同時退出用量會計與汰除候選，變成永遠刪不掉的孤兒——磁碟照樣佔著、
/// 用量卻少算，正是「看不出磁碟為何快滿」的成因。汰除一律留給保留清理。
/// </para>
/// <para>
/// 驗證器以委派注入，測試不碰真實 ffprobe：抽查的價值在「找出壞段並逐圈前進」的分頁邏輯，
/// 而不在 ffprobe 本身（那屬於外部工具）。
/// </para>
/// </summary>
public sealed class MediaIntegrityCheckerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly AuditLogRepository _audit;
    private readonly string _root;

    public MediaIntegrityCheckerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-integrity-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-integrity-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _audit = new AuditLogRepository(_store);
        EnsureChannel(1);
    }

    /// <summary>完好的段通過抽查、被標記為已檢查，下一輪不再被取出。</summary>
    [Fact]
    public void 完好區段通過檢查並標記已檢查()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);
        Final(now.AddSeconds(60), exists: true);

        var report = Run(now, batchSize: 10, isPlayable: _ => true);

        Assert.Equal(2, report.Checked);
        Assert.Equal(0, report.Corrupt);
        Assert.Equal(0, report.Missing);
        Assert.False(report.AnySuspicious);
        Assert.Empty(_segments.ListFinalUnchecked(10));
    }

    /// <summary>索引有列、檔案卻不存在時回報 missing 並寫稽核（磁碟掉線、外部誤刪的症狀）。</summary>
    [Fact]
    public void 檔案不存在時回報missing並寫稽核()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Final(now, exists: true);
        File.Delete(_segments.Get(id)!.FilePath);

        // 檔案都不在了，驗證器根本不該被叫到。
        var called = false;
        var report = Run(now, batchSize: 10, isPlayable: _ =>
        {
            called = true;
            return true;
        });

        Assert.False(called, "檔案不存在時不應呼叫驗證器");
        Assert.Equal(1, report.Missing);
        Assert.Equal(0, report.Corrupt);
        Assert.True(report.AnySuspicious);
        Assert.Single(_audit.List(new AuditLogQuery { Action = "recording.integrity" }));
    }

    /// <summary>ffprobe 判為無法解析時回報 corrupt 並寫稽核。</summary>
    [Fact]
    public void 驗證判定損壞時回報corrupt並寫稽核()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);

        var report = Run(now, batchSize: 10, isPlayable: _ => false);

        Assert.Equal(1, report.Corrupt);
        Assert.True(report.AnySuspicious);
        Assert.Single(_audit.List(new AuditLogQuery { Action = "recording.integrity" }));
    }

    /// <summary>
    /// 損壞的段**不得**被改狀態或刪檔：狀態仍是 final、檔案仍在原處。
    ///
    /// 這是最容易寫錯、後果最隱蔽的一條：改成 corrupt 會讓它退出用量統計與汰除候選，
    /// 從此永遠刪不掉（見類別註解）。
    /// </summary>
    [Fact]
    public void 查到損壞段時不改狀態也不刪檔()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Final(now, exists: true);
        var path = _segments.Get(id)!.FilePath;

        Run(now, batchSize: 10, isPlayable: _ => false);

        Assert.True(File.Exists(path), "完整性檢查不得刪除檔案，汰除要留給保留清理");
        Assert.Equal(SegmentStatus.Final, _segments.Get(id)!.Status);
    }

    /// <summary>
    /// 無法判定（ffprobe 不在 PATH、逾時、檔案被鎖）既不算損壞也不算遺失，但仍標記已檢查，
    /// 否則每輪都會重取同一批最舊的段，較新的段永遠輪不到。
    /// </summary>
    [Fact]
    public void 無法判定時不當成損壞但仍標記已檢查()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);

        var report = Run(now, batchSize: 10, isPlayable: _ => null);

        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.Corrupt);
        Assert.Equal(0, report.Missing);
        Assert.False(report.AnySuspicious);
        Assert.Empty(_audit.List(new AuditLogQuery { Action = "recording.integrity" }));
        Assert.Empty(_segments.ListFinalUnchecked(10));
    }

    /// <summary>驗證器若擲出例外，視為無法判定而非損壞（一支沒裝 ffprobe 的機器不該把每段都記成壞的）。</summary>
    [Fact]
    public void 驗證器擲例外時視為無法判定()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);

        var report = Run(now, batchSize: 10, isPlayable: _ => throw new InvalidOperationException("ffprobe 不在 PATH"));

        Assert.Equal(1, report.Checked);
        Assert.Equal(0, report.Corrupt);
        Assert.False(report.AnySuspicious);
    }

    /// <summary>每輪只抽查設定的段數，且逐圈前進（不會永遠停在最舊那批）。</summary>
    [Fact]
    public void 每輪只抽查一批並逐圈前進()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);
        Final(now.AddSeconds(60), exists: true);
        Final(now.AddSeconds(120), exists: true);

        var first = Run(now, batchSize: 2, isPlayable: _ => true);
        Assert.Equal(2, first.Checked);

        var second = Run(now, batchSize: 2, isPlayable: _ => true);
        Assert.Equal(1, second.Checked);

        var third = Run(now, batchSize: 2, isPlayable: _ => true);
        Assert.Equal(0, third.Checked);
    }

    /// <summary>設定為 0 時整支停用，回傳空結果且不標記任何段。</summary>
    [Fact]
    public void RunConfigured段數為零時停用自我檢查()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);
        new SettingsRepository(_store).Set(MediaIntegrityCoordinator.BatchSizeKey, "0");

        var report = MediaIntegrityCoordinator.RunConfigured(_store, now, _ => true);

        Assert.Equal(0, report.Checked);
        Assert.False(report.AnySuspicious);
        Assert.Single(_segments.ListFinalUnchecked(10));
    }

    /// <summary>未設定時採用預設段數（此處備妥超過預設值的段，證明預設值真的有被拿來當上限）。</summary>
    [Fact]
    public void RunConfigured未設定時採用預設段數()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < MediaIntegrityCoordinator.DefaultBatchSize + 3; i++)
        {
            Final(now.AddSeconds(i * 60), exists: true);
        }

        var report = MediaIntegrityCoordinator.RunConfigured(_store, now, _ => true);

        Assert.Equal(MediaIntegrityCoordinator.DefaultBatchSize, report.Checked);
    }

    /// <summary>設定值會覆寫預設段數。</summary>
    [Fact]
    public void RunConfigured自訂段數會覆寫預設()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Final(now, exists: true);
        Final(now.AddSeconds(60), exists: true);
        Final(now.AddSeconds(120), exists: true);
        new SettingsRepository(_store).Set(MediaIntegrityCoordinator.BatchSizeKey, "2");

        var report = MediaIntegrityCoordinator.RunConfigured(_store, now, _ => true);

        Assert.Equal(2, report.Checked);
    }

    private IntegrityCheckReport Run(DateTime nowUtc, int batchSize, Func<string, bool?> isPlayable)
        => MediaIntegrityCoordinator.Run(_store, nowUtc, batchSize, isPlayable);

    private long Final(DateTime startUtc, bool exists)
    {
        var file = Path.Combine(_root, $"seg-{Guid.NewGuid():N}.mp4");
        if (exists)
        {
            File.WriteAllBytes(file, new byte[16]);
        }

        var id = _segments.BeginSegment(1, "main", file, startUtc);
        _segments.CompleteSegment(id, startUtc.AddSeconds(60), 16, 60, "sha");
        return id;
    }

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
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
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
