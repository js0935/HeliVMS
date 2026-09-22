using System.Data.Common;

namespace HeliVMS.Storage.Tests;

public class EdgeBackfillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly EdgeBackfillJobRepository _jobs;

    private static DateTime T(int hour) =>
        new DateTime(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public EdgeBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-edgebackfill-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _jobs = new EdgeBackfillJobRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    private sealed class FakeRunner : IEdgeBackfillRunner
    {
        private readonly Func<EdgeBackfillJob, EdgeBackfillResult> _fn;
        private int _max;
        private int _active;

        public FakeRunner(Func<EdgeBackfillJob, EdgeBackfillResult>? fn = null, int delayMs = 0)
        {
            _fn = fn ?? (_ => new EdgeBackfillResult(true));
            DelayMs = delayMs;
        }

        public int ActiveMax => _max;

        public int DelayMs { get; private set; }

        public async ValueTask<EdgeBackfillResult> RunAsync(EdgeBackfillJob job, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _active);
            var max = Volatile.Read(ref _max);
            while (now > max)
            {
                Interlocked.CompareExchange(ref _max, now, max);
                max = Volatile.Read(ref _max);
            }

            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }

            var result = _fn(job);
            Interlocked.Decrement(ref _active);
            return result;
        }
    }

    [Fact]
    public void CommandFactory_BuildsFfmpegArgs()
    {
        var args = EdgeBackfillCommandFactory.BuildArguments("http://cam/sd", @"D:\out\seg.mp4", T(8), T(9));

        Assert.Contains("-ss", args);
        Assert.Equal("2026-09-22T08:00:00.000", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.Contains("-t", args);
        Assert.Equal("3600", args[Array.IndexOf(args, "-t") + 1]);
        Assert.Equal("http://cam/sd", args[Array.IndexOf(args, "-i") + 1]);
        Assert.Equal("-c", args[Array.IndexOf(args, "-c")]);
        Assert.Equal("copy", args[Array.IndexOf(args, "-c") + 1]);
        Assert.EndsWith("seg.mp4", args[^1]);
    }

    [Fact]
    public void Create_Then_QueryDue_ReturnsPending()
    {
        var id = _jobs.Create(1, 2, T(8), T(9));

        Assert.True(id > 0);
        var due = Assert.Single(_jobs.QueryDue(T(10)));
        Assert.Equal(id, due.Id);
        Assert.Equal(EdgeBackfillStatus.Pending, due.Status);
        Assert.Equal(0, due.Attempts);
    }

    [Fact]
    public void Create_OverlappingConflict_IsSkipped()
    {
        _jobs.Create(1, 2, T(8), T(10));

        Assert.Equal(-1, _jobs.Create(1, 2, T(9), T(9).AddHours(2)));
        Assert.Single(_jobs.QueryAll(deviceId: 1));
    }

    [Fact]
    public void MarkDone_SetsCompleted_AndStatus()
    {
        var id = _jobs.Create(1, 1, T(8), T(9));
        _jobs.MarkDone(id, T(12));

        var job = Assert.Single(_jobs.QueryAll());
        Assert.Equal(EdgeBackfillStatus.Done, job.Status);
        Assert.Equal(T(12), job.CompletedUtc);
        Assert.Empty(_jobs.QueryDue(T(12))); // Done 不再取回
    }

    [Fact]
    public void MarkFailed_IncrementsAttempts_AndBacksOff()
    {
        var id = _jobs.Create(1, 1, T(8), T(9));
        _jobs.MarkFailed(id, "http 500", T(11));

        var job = Assert.Single(_jobs.QueryAll());
        Assert.Equal(EdgeBackfillStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("http 500", job.LastError);
        Assert.Empty(_jobs.QueryDue(T(10)));
        Assert.Single(_jobs.QueryDue(T(11)));
    }

    [Fact]
    public async Task Executor_CompletesDueJobs()
    {
        var idA = _jobs.Create(1, 1, T(8), T(9));
        var idB = _jobs.Create(2, 1, T(9), T(10));
        var ex = new EdgeBackfillExecutor(_jobs, new FakeRunner(), maxConcurrent: 2, TimeSpan.FromMinutes(5));

        var run = await ex.ExecuteOnceAsync(T(12));

        Assert.Equal(2, run.Items.Count);
        Assert.All(run.Items, i => Assert.True(i.Success));
        Assert.All(_jobs.QueryAll(), j => Assert.Equal(EdgeBackfillStatus.Done, j.Status));
        Assert.Equal(T(12), Assert.Single(_jobs.QueryAll(), j => j.Id == idA).CompletedUtc);
        Assert.Equal(T(12), Assert.Single(_jobs.QueryAll(), j => j.Id == idB).CompletedUtc);
    }

    [Fact]
    public async Task Executor_Failure_BackoffsNextAttempt()
    {
        _jobs.Create(1, 1, T(8), T(9));
        var ex = new EdgeBackfillExecutor(_jobs, new FakeRunner(_ => new EdgeBackfillResult(false, "conn fail")), 2, TimeSpan.FromMinutes(10));

        var run = await ex.ExecuteOnceAsync(T(12));

        Assert.Single(run.Items, i => !i.Success);
        var job = Assert.Single(_jobs.QueryAll());
        Assert.Equal(EdgeBackfillStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Empty(_jobs.QueryDue(T(12)));
        Assert.Single(_jobs.QueryDue(T(12).AddMinutes(10)));
    }

    [Fact]
    public async Task Executor_RetriesUntilSuccess()
    {
        var id = _jobs.Create(1, 1, T(8), T(9));
        var failFirst = true;
        var runner = new FakeRunner(job =>
        {
            if (failFirst)
            {
                failFirst = false;
                return new EdgeBackfillResult(false, "once");
            }

            return new EdgeBackfillResult(true);
        });
        var ex = new EdgeBackfillExecutor(_jobs, runner, 2, TimeSpan.FromMinutes(5));

        await ex.ExecuteOnceAsync(T(12));
        Assert.Single(_jobs.QueryAll(), j => j.Status == EdgeBackfillStatus.Failed && j.Attempts == 1);

        await ex.ExecuteOnceAsync(T(12).AddMinutes(5));
        var job = Assert.Single(_jobs.QueryAll(), j => j.Id == id);
        Assert.Equal(EdgeBackfillStatus.Done, job.Status);
        Assert.Equal(1, job.Attempts); // 重試成功後 attempts 維持既有累計
    }

    [Fact]
    public async Task Executor_RespectsMaxConcurrent()
    {
        for (var i = 0; i < 6; i++)
        {
            _jobs.Create(1, 1, T(8).AddHours(i), T(8).AddHours(i + 1));
        }

        var runner = new FakeRunner(delayMs: 250);
        var ex = new EdgeBackfillExecutor(_jobs, runner, maxConcurrent: 2, TimeSpan.FromMinutes(5));

        await ex.ExecuteOnceAsync(T(12));

        Assert.Equal(2, runner.ActiveMax);
        Assert.All(_jobs.QueryAll(), j => Assert.Equal(EdgeBackfillStatus.Done, j.Status));
    }
}