using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class ExportJobRepositoryTests
{
    private static SqliteStore NewStore(out string db)
    {
        db = Path.Combine(Path.GetTempPath(), $"helivms-job-{Guid.NewGuid():N}.db");
        var store = new SqliteStore(db);
        store.Initialize();
        new ChannelRepository(store).EnsureSeedChannels();
        return store;
    }

    [Fact]
    public void Enqueue_ReturnsIdAndPersistsAsQueued()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddMinutes(-60), DateTime.UtcNow);

        var job = repo.Get(id);
        Assert.NotNull(job);
        Assert.Equal("queued", job!.Status);
        Assert.Equal(1, job.ChannelId);
        Assert.Equal("main", job.Stream);
    }

    [Fact]
    public void List_OrdersNewestFirst()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var a = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        var b = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-2));

        var all = repo.List();
        Assert.Equal([b, a], all.Select(j => j.Id));
    }

    [Fact]
    public void ListQueued_OnlyQueuedOldestFirst()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var a = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
        var b = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        var now = DateTime.UtcNow;
        repo.MarkRunning(a, now);
        repo.SetResult(a, @"C:\tmp\a.mp4", "AAAA", 42, now);

        var queued = repo.ListQueued();
        Assert.Equal([b], queued.Select(j => j.Id));
    }

    [Fact]
    public void MarkRunning_SetResult_ProducesDoneJob()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
        var now = DateTime.UtcNow;

        repo.MarkRunning(id, now);
        repo.SetResult(id, @"C:\tmp\out.mp4", "0123456789ABCDEF", 777, now.AddSeconds(5));

        var job = repo.Get(id);
        Assert.Equal("done", job!.Status);
        Assert.Equal(@"C:\tmp\out.mp4", job.OutputPath);
        Assert.Equal("0123456789ABCDEF", job.Sha256);
        Assert.Equal(777, job.FileSizeBytes);
        Assert.NotNull(job.StartedUtc);
        Assert.NotNull(job.FinishedUtc);
    }

    [Fact]
    public void SetError_PersistsFailed()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
        repo.MarkRunning(id, DateTime.UtcNow);
        repo.SetError(id, "boom", DateTime.UtcNow);

        var job = repo.Get(id);
        Assert.Equal("failed", job!.Status);
        Assert.Equal("boom", job.Error);
    }

    [Fact]
    public void Delete_RemovesJob()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);

        repo.Delete(id);

        Assert.Null(repo.Get(id));
    }

    [Fact]
    public void PurgeFinished_RemovesOnlyOldFinished()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var oldDone = repo.Enqueue(1, "main", DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(-3).AddMinutes(5));
        var newFailed = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var queued = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        var now = DateTime.UtcNow;
        repo.MarkRunning(oldDone, now);
        repo.SetResult(oldDone, @"C:\tmp\x.mp4", "X", 1, now.AddDays(-2));
        repo.MarkRunning(newFailed, now);
        repo.SetError(newFailed, "err", now);

        var removed = repo.PurgeFinished(now.AddHours(-1));

        Assert.Equal(1, removed);
        Assert.Null(repo.Get(oldDone));
        Assert.NotNull(repo.Get(newFailed));
        Assert.NotNull(repo.Get(queued));
    }
}