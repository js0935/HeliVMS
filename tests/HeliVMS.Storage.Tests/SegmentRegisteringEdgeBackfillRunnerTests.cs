namespace HeliVMS.Storage.Tests;

/// <summary>
/// M94 邊緣補抓回灌：<see cref="SegmentRegisteringEdgeBackfillRunner"/> 必須在內層下載成功後
/// 才把落盤檔登記為 final 區段，失敗則不登記。先前 <see cref="EdgeBackfillExecutor"/> 直接跑
/// <see cref="EdgeFfmpegBackfillRunner"/>，下載成功卻不建索引，補抓檔在時間軸上永遠看不到。
/// </summary>
public class SegmentRegisteringEdgeBackfillRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    private static readonly DateTime Start = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

    public SegmentRegisteringEdgeBackfillRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-edgeregister-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private sealed class StubRunner : IEdgeBackfillRunner
    {
        private readonly EdgeBackfillResult _result;

        public StubRunner(EdgeBackfillResult result) => _result = result;

        public ValueTask<EdgeBackfillResult> RunAsync(EdgeBackfillJob job, CancellationToken ct) =>
            ValueTask.FromResult(_result);
    }

    private static EdgeBackfillJob Job(int channelId) =>
        new(1, 1, channelId, Start, Start.AddHours(1), EdgeBackfillStatus.Downloading, 0, null, null, null);

    [Fact]
    public async Task 下載成功後把檔案登記為final區段()
    {
        var channelId = new ChannelRepository(_store).Add("cam", "rtsp://x/main");
        var file = Path.Combine(Path.GetTempPath(), $"helivms-backfill-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5]);
        try
        {
            var resolver = (EdgeBackfillJob _) => new EdgePullTarget("rtsp://cam/playback", file);
            var runner = new SegmentRegisteringEdgeBackfillRunner(
                new StubRunner(new EdgeBackfillResult(true)), resolver, new SegmentRepository(_store));

            var result = await runner.RunAsync(Job(channelId), CancellationToken.None);

            Assert.True(result.Success);
            var segment = Assert.Single(new SegmentRepository(_store).ListFinal(channelId));
            Assert.Equal(file, segment.FilePath);
            Assert.Equal(Start.AddHours(1), segment.EndUtc);
            Assert.Equal(5, segment.SizeBytes);
            Assert.False(string.IsNullOrEmpty(segment.Sha256));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task 下載失敗不登記區段()
    {
        var channelId = new ChannelRepository(_store).Add("cam2", "rtsp://x/main2");
        var resolver = (EdgeBackfillJob _) => new EdgePullTarget("rtsp://cam/playback", "nowhere.mp4");
        var runner = new SegmentRegisteringEdgeBackfillRunner(
            new StubRunner(new EdgeBackfillResult(false, "連線失敗")), resolver, new SegmentRepository(_store));

        var result = await runner.RunAsync(Job(channelId), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(new SegmentRepository(_store).ListFinal(channelId));
    }
}
