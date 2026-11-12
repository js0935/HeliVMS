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
    public void Retry_OnlyRequeuesFailedJob()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var failed = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
        var done = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-2));
        var now = DateTime.UtcNow;
        repo.MarkRunning(failed, now);
        repo.SetError(failed, "boom", now);
        repo.MarkRunning(done, now);
        repo.SetResult(done, @"C:\tmp\ok.mp4", "ABCD", 5, now);

        repo.Retry(failed);
        repo.Retry(done);

        Assert.Equal("queued", repo.Get(failed)!.Status);
        Assert.Null(repo.Get(failed)!.Error);
        Assert.Null(repo.Get(failed)!.StartedUtc);
        Assert.Null(repo.Get(failed)!.FinishedUtc);
        Assert.Equal("done", repo.Get(done)!.Status);
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

    /// <summary>取消排隊中的工作（QC_Live 的 ctrlRecBackupTask 對齊）：只取消 queued，取消的保留在清單上。</summary>
    [Fact]
    public void Cancel_只取消排隊中的工作且狀態保留在清單()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var queued = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);

        Assert.True(repo.Cancel(queued));

        var job = repo.Get(queued);
        Assert.NotNull(job);
        Assert.Equal("cancelled", job!.Status);
        Assert.Equal("cancelled", job.Error);
        Assert.NotNull(job.FinishedUtc);

        // 不在佇列裡：下一次「開始處理」不會把它撿起來跑。
        Assert.Empty(repo.ListQueued());

        // 已結束（done/failed/cancelled）與不存在的工作沒有可取消的東西。
        var other = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
        Assert.False(repo.Cancel(other * 100));
        Assert.False(repo.Cancel(queued));
    }

    /// <summary>取消後的紀錄也要能被清掃（不然取消累積幾個月就成堆）。</summary>
    [Fact]
    public void PurgeFinished_包含取消的工作()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(-3).AddMinutes(5));
        repo.Cancel(id);

        // 截止在「取消之後」：剛取消的列要落入清掃範圍（清掃看 finished_at 是否早於截止）。
        var removed = repo.PurgeFinished(DateTime.UtcNow.AddHours(1));

        Assert.Equal(1, removed);
        Assert.Null(repo.Get(id));
    }

    /// <summary>
    /// 行程孤兒對帳：程式於匯出途中被砍時，running 工作永遠不會有 finished_at——
    /// 實測 11-01 的 running 掛到 11-12，匯出中心列著一筆永遠不會完成的「執行中」。
    /// 啟動時必須結算成 failed 並寫明原因，操作員按重試即可重排。
    /// </summary>
    [Fact]
    public void 啟動時把孤兒的running工作結算成failed並寫明原因()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        repo.MarkRunning(id, DateTime.UtcNow.AddHours(-1));

        var now = DateTime.UtcNow;
        var settled = repo.FailRunningAsOrphaned(now, "程式於匯出途中結束，工作未完成");

        Assert.Equal(1, settled);
        var job = repo.Get(id)!;
        Assert.Equal("failed", job.Status);
        Assert.Equal("程式於匯出途中結束，工作未完成", job.Error);
        Assert.NotNull(job.FinishedUtc);

        // 結算要留稽核（export.orphan）：「這筆為什麼變成失敗」的唯一線索。
        var audit = store.Query(
            "SELECT COUNT(*) FROM audit_log WHERE action = 'export.orphan' AND target_id = $id;",
            r => { r.Read(); return r.GetInt32(0); },
            c => c.Parameters.AddWithValue("$id", id));
        Assert.Equal(1, audit);
    }

    /// <summary>queued 與 done 的工作不受啟動對帳影響：queued 仍可被佇列執行器接手。</summary>
    [Fact]
    public void queued與done的工作不受啟動對帳影響()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var queuedId = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        var doneId = repo.Enqueue(2, "main", DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-2));
        repo.MarkRunning(doneId, DateTime.UtcNow.AddHours(-2));
        repo.SetResult(doneId, @"C:\tmp\ok.mp4", "BBBB", 16, DateTime.UtcNow.AddHours(-2));

        repo.FailRunningAsOrphaned(DateTime.UtcNow, "程式於匯出途中結束，工作未完成");

        Assert.Equal("queued", repo.Get(queuedId)!.Status);
        Assert.Equal("done", repo.Get(doneId)!.Status);
    }

    /// <summary>孤兒工作結算成 failed 之後，「重試」接手把它排回佇列。</summary>
    [Fact]
    public void 孤兒工作結算後可重試重排()
    {
        using var store = NewStore(out var db);
        var repo = new ExportJobRepository(store);
        var id = repo.Enqueue(1, "main", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        repo.MarkRunning(id, DateTime.UtcNow.AddHours(-1));
        repo.FailRunningAsOrphaned(DateTime.UtcNow, "程式於匯出途中結束，工作未完成");

        repo.Retry(id);

        var job = repo.Get(id)!;
        Assert.Equal("queued", job.Status);
        Assert.Null(job.Error);
        Assert.Null(job.StartedUtc);
    }
}