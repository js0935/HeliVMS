using System.IO;

namespace HeliVMS.Storage.Tests;

/// <summary>M55（§14.4 異地）：異地備援複製工作與服務。</summary>
public class OffsiteReplicationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _src;
    private readonly string _dst;
    private SqliteStore _store;
    private readonly OffsiteReplicationRepository _repo;
    private readonly OffsiteReplicationService _service;

    public OffsiteReplicationTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-offsite-{Guid.NewGuid():N}");
        _dbPath = root + ".db";
        _src = Path.Combine(root, "src");
        _dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new OffsiteReplicationRepository(_store);
        _service = new OffsiteReplicationService(_repo);
    }

    public void Dispose()
    {
        _store.Dispose();
        var root = Path.GetDirectoryName(_dst) ?? Path.GetTempPath();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private void WriteSample(string relative)
    {
        var full = Path.Combine(_src, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"sample-{relative}");
    }

    [Fact]
    public void Reinitialize_IsIdempotent_WithJobsTable()
    {
        var id = _repo.Add(_src, _dst, 60, true);
        _store.Dispose();
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _store.Initialize();
        Assert.Contains(new OffsiteReplicationRepository(_store).List(), j => j.Id == id);
    }

    [Fact]
    public void Upsert_DeduplicatesBySourceAndDestination()
    {
        var a = _repo.Upsert(_src, _dst, 30, true);
        var b = _repo.Upsert(_src, _dst, 45, false);
        Assert.Equal(a, b);
        var job = _repo.List().Single();
        Assert.Equal(45, job.IntervalMinutes);
        Assert.False(job.Enabled);
    }

    [Fact]
    public void RunOnce_CopiesAllFiles_PreservesRelativeStructure()
    {
        var id = _repo.Add(_src, _dst, 60, true);
        WriteSample("recordings/ch1/seg001.mp4");
        WriteSample("snapshots/scan.bin");

        Assert.True(_service.RunOnce(id));

        Assert.True(File.Exists(Path.Combine(_dst, "recordings", "ch1", "seg001.mp4")));
        Assert.True(File.Exists(Path.Combine(_dst, "snapshots", "scan.bin")));
        Assert.Equal(2, _dstDirectoryFiles().Count());
    }

    [Fact]
    public void RunOnce_SecondRun_SkipsUnchanged_OnlyUpdatedCopied()
    {
        var id = _repo.Add(_src, _dst, 60, true);
        WriteSample("a.txt");
        WriteSample("b.txt");
        Assert.True(_service.RunOnce(id));
        var job = _repo.List().Single();
        Assert.True(job.LastRunUtc.HasValue);

        var later = (job.LastRunUtc ?? DateTime.UtcNow) + TimeSpan.FromMinutes(2);
        File.Delete(Path.Combine(_dst, "a.txt"));
        File.SetLastWriteTimeUtc(Path.Combine(_src, "a.txt"), later);

        Assert.True(_service.RunOnce(id));
        Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_dst, "b.txt")));
        Assert.Equal("sample-a.txt", File.ReadAllText(Path.Combine(_dst, "a.txt")));
    }

    [Fact]
    public void RunOnce_WhenSourceMissing_RecordsFailure_AndCountsConsecutiveFailures()
    {
        var missingSrc = Path.Combine(_src, "..", $"missing-{Guid.NewGuid():N}");
        var job = new OffsiteJob(0, missingSrc, _dst, 60, true, null, null, null, 0, null);
        var rawId = _repo.Add(missingSrc, _dst, 60, true);

        Assert.False(_service.RunOnce(rawId));
        var afterFail = _repo.List().Single();
        Assert.Equal(1, afterFail.ConsecutiveFailures);
        Assert.Equal("FAILED", afterFail.LastResult);
        Assert.NotNull(afterFail.LastError);

        Directory.CreateDirectory(missingSrc);
        WriteForRaw(missingSrc, "x.txt");
        Assert.True(_service.RunOnce(rawId));
        var afterOk = _repo.List().Single();
        Assert.Equal(0, afterOk.ConsecutiveFailures);
        Assert.Equal("OK", afterOk.LastResult);
        Assert.True(File.Exists(Path.Combine(_dst, "x.txt")));
    }

    [Fact]
    public void IsDue_RespectsIntervalAndEnabled()
    {
        var never = new OffsiteJob(0, _src, _dst, 60, true, null, null, null, 0, null);
        var due = new OffsiteJob(0, _src, _dst, 60, true,
            DateTime.UtcNow.AddMinutes(-70), "OK", null, 0, null);
        var notDue = new OffsiteJob(0, _src, _dst, 60, true,
            DateTime.UtcNow.AddMinutes(-10), "OK", null, 0, null);
        var disabled = new OffsiteJob(0, _src, _dst, 60, false,
            DateTime.UtcNow.AddMinutes(-70), "OK", null, 0, null);

        var now = DateTime.UtcNow;
        Assert.True(OffsiteReplicationService.IsDue(never, now));
        Assert.True(OffsiteReplicationService.IsDue(due, now));
        Assert.False(OffsiteReplicationService.IsDue(notDue, now));
        Assert.False(OffsiteReplicationService.IsDue(disabled, now));
    }

    [Fact]
    public void DueJobs_ReturnsOnlyEnabledDue()
    {
        _repo.Add(_src, _dst, 10, true);
        _repo.Add(_src + "-new", _dst + "-new", 10, false);
        var now = DateTime.UtcNow;
        Assert.Single(_service.DueJobs(now));
    }

    private IEnumerable<string> _dstDirectoryFiles()
    {
        return Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories);
    }

    private static void WriteForRaw(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "x");
    }
}