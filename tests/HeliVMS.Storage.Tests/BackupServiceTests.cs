using System.Security.Cryptography;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _recordsRoot;
    private readonly string _target;
    private readonly SqliteStore _store;

    public BackupServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-bk-{Guid.NewGuid():N}.db");
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-bk-{Guid.NewGuid():N}");
        _recordsRoot = Path.Combine(dir, "recordings");
        _target = Path.Combine(dir, "backup-target");
        Directory.CreateDirectory(_recordsRoot);
        Directory.CreateDirectory(_target);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        new ChannelRepository(_store).EnsureSeedChannels();
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            var dir = Path.GetDirectoryName(_recordsRoot)!;
            Directory.Delete(dir, recursive: true);
        }
        catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private readonly static DateTime Base = new(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);

    private (long Id, string Path) AddSegment(int channelId, DateTime startUtc, bool writeFile = true)
    {
        var payload = new byte[] { 0x48, 0x45, 0x4C, (byte)(channelId & 0xFF), (byte)(startUtc.Minute & 0xFF) };
        var dir = Path.Combine(_recordsRoot, $"ch{channelId}", startUtc.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"seg-{startUtc:HHmmss}.mp4");
        if (writeFile)
        {
            File.WriteAllBytes(path, payload);
        }

        var segments = new SegmentRepository(_store);
        var id = segments.BeginSegment(channelId, "main", path, startUtc);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        segments.CompleteSegment(id, startUtc.AddSeconds(10), payload.Length, 10.0, sha);
        return (id, path);
    }

    [Fact]
    public void Run_Empty_Noop()
    {
        var result = new BackupService(_store).Run(_recordsRoot, _target);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Advanced);
        Assert.Single(new BackupRepository(_store).ListRuns(targetRoot: _target));
    }

    [Fact]
    public void Run_CopiesAllFinalSegments_TargetHashMatchesDb()
    {
        AddSegment(1, Base.AddMinutes(-2));
        AddSegment(2, Base.AddMinutes(-1));

        var result = new BackupService(_store).Run(_recordsRoot, _target);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Advanced);

        var records = new BackupRepository(_store).ListRuns(targetRoot: _target);
        Assert.Single(records);
        Assert.NotNull(records[0].CheckpointUtc);

        var dbSegs = new SegmentRepository(_store).ListAllFinal();
        var targets = Directory.GetFiles(_target, "*.mp4", SearchOption.AllDirectories);
        Assert.Equal(2, targets.Length);
        foreach (var seg in dbSegs)
        {
            var target = Directory.GetFiles(_target, Path.GetFileName(seg.FilePath), SearchOption.AllDirectories).Single();
            Assert.Equal(seg.Sha256, Sha256Hex(target));
        }
    }

    [Fact]
    public void Run_Incremental_CopiesOnlyNewSegments()
    {
        AddSegment(1, Base.AddMinutes(-2));
        AddSegment(2, Base.AddMinutes(-1));
        var first = new BackupService(_store).Run(_recordsRoot, _target);
        Assert.Equal(2, first.Copied);

        AddSegment(1, DateTime.UtcNow.AddMinutes(2));
        var second = new BackupService(_store).Run(_recordsRoot, _target);

        Assert.Equal(1, second.Scanned);
        Assert.Equal(1, second.Copied);
        Assert.Equal(0, second.Failed);
        Assert.True(second.Advanced);
        Assert.Equal(3, Directory.GetFiles(_target, "*.mp4", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Run_SourceMissing_FailsWithoutAdvancing_ThenRetrySucceeds()
    {
        var start = Base.AddMinutes(-1);
        var seg = AddSegment(1, start);
        File.Delete(seg.Path);

        var result = new BackupService(_store).Run(_recordsRoot, _target);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Advanced);
        var run = new BackupRepository(_store).ListRuns(targetRoot: _target).Single();
        Assert.Null(run.CheckpointUtc);
        Assert.Contains("來源檔不存在", run.Detail);

        var payload = new byte[] { 0x48, 0x45, 0x4C, 1, (byte)(start.Minute & 0xFF) };
        Directory.CreateDirectory(Path.GetDirectoryName(seg.Path)!);
        File.WriteAllBytes(seg.Path, payload);

        var retry = new BackupService(_store).Run(_recordsRoot, _target);
        Assert.Equal(1, retry.Scanned);
        Assert.Equal(1, retry.Copied);
        Assert.Equal(0, retry.Failed);
        Assert.True(retry.Advanced);
        Assert.NotNull(new BackupRepository(_store).ListRuns(targetRoot: _target).First().CheckpointUtc);
    }

    [Fact]
    public void Run_TargetEqualsSource_Throws()
    {
        AddSegment(1, Base.AddMinutes(-1));
        Assert.Throws<InvalidOperationException>(() => new BackupService(_store).Run(_recordsRoot, _recordsRoot));
    }

    [Fact]
    public void ListRuns_NewestFirst()
    {
        AddSegment(1, Base.AddMinutes(-5));
        var svc = new BackupService(_store);

        svc.Run(_recordsRoot, _target);
        AddSegment(1, DateTime.UtcNow.AddMinutes(2));
        svc.Run(_recordsRoot, _target);

        var runs = new BackupRepository(_store).ListRuns(targetRoot: _target);
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].RunAt >= runs[1].RunAt);
        Assert.Equal(1, runs[0].CopiedCount);
        Assert.Equal(1, runs[1].CopiedCount);
    }

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}