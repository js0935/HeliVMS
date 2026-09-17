using System.Security.Cryptography;
using HeliVMS.Recording;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class ExportJobServiceTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _dbPath;
    private readonly string _dir;

    public ExportJobServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ej-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"helivms-ej-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        new ChannelRepository(_store).EnsureSeedChannels();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static readonly DateTime Base = new(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>Fake 執行器：core 判定失敗時拋錯，否則寫「輸出檔」並回傳實測 SHA。</summary>
    private static Func<ExportRequest, CancellationToken, Task<ExportResult>> FakeExecutor(
        Func<ExportRequest, bool>? failWhen = null,
        string failMessage = "無錄影段落（模擬）")
    {
        return (request, ct) =>
        {
            if (failWhen?.Invoke(request) == true)
            {
                throw new InvalidOperationException(failMessage);
            }

            var payload = new byte[] { 1, 2, 3, (byte)(request.ChannelId % 256) };
            File.WriteAllBytes(request.OutputPath, payload);
            var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            return Task.FromResult(new ExportResult(request.OutputPath, sha, payload.Length, 1.0));
        };
    }

    [Fact]
    public async Task ProcessQueued_Empty_Noop()
    {
        var svc = new ExportJobService(_store, FakeExecutor());
        var result = await svc.ProcessQueuedAsync(_dir);

        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task ProcessQueued_TwoJobs_BothDoneWithVerifiableOutput()
    {
        var jobs = new ExportJobRepository(_store);
        var j1 = jobs.Enqueue(1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));
        var j2 = jobs.Enqueue(2, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store, FakeExecutor());
        var result = await svc.ProcessQueuedAsync(_dir);

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.Succeeded);

        var done1 = jobs.Get(j1)!;
        var done2 = jobs.Get(j2)!;
        Assert.Equal("done", done1.Status);
        Assert.Equal("done", done2.Status);
        Assert.NotNull(done1.OutputPath);
        Assert.NotNull(done2.OutputPath);
        Assert.True(File.Exists(done1.OutputPath));
        Assert.True(File.Exists(done2.OutputPath));

        var report = ExportVerifier.Verify(done1.OutputPath, done1.Sha256);
        Assert.True(report.Valid);
        Assert.Equal(done1.Sha256, report.Sha256);
    }

    [Fact]
    public async Task ProcessQueued_OneBadJob_FailedBehindContinues()
    {
        var jobs = new ExportJobRepository(_store);
        var bad = jobs.Enqueue(1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));
        var good = jobs.Enqueue(2, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store, FakeExecutor(
            failWhen: r => r.ChannelId == 1));
        var result = await svc.ProcessQueuedAsync(_dir);

        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("failed", jobs.Get(bad)!.Status);
        Assert.Equal("done", jobs.Get(good)!.Status);
        Assert.Equal("無錄影段落（模擬）", jobs.Get(bad)!.Error);
    }

    [Fact]
    public async Task ProcessQueued_OutputsUseJobIdInName()
    {
        var jobs = new ExportJobRepository(_store);
        var j1 = jobs.Enqueue(1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store, FakeExecutor());
        await svc.ProcessQueuedAsync(_dir);

        var done = jobs.Get(j1)!;
        Assert.Contains($"export-job{j1}", done.OutputPath);
    }
}