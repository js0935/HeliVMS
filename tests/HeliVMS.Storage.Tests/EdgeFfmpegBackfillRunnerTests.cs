namespace HeliVMS.Storage.Tests;

public class EdgeFfmpegBackfillRunnerTests
{
    private static readonly EdgeBackfillJob SampleJob =
        new(1, 1, 2, new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc),
            EdgeBackfillStatus.Pending, 0, null, null, null);

    private static EdgePullTarget SampleTarget() => new("http://cam/sd/seg.ts", @"D:\out\seg.mp4");

    [Fact]
    public async Task Run_Success_Exit0_UsesResolvedTargetAndFactoryArgs()
    {
        string? seenFile = null;
        IReadOnlyList<string>? seenArgs = null;
        var runner = new EdgeFfmpegBackfillRunner(
            _ => SampleTarget(),
            "ffmpeg",
            (file, args, _, _) =>
            {
                seenFile = file;
                seenArgs = args;
                return (0, "", "");
            });

        var result = await runner.RunAsync(SampleJob, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ffmpeg", seenFile);
        Assert.Contains("-ss", seenArgs!);
        Assert.Contains("http://cam/sd/seg.ts", seenArgs!);
        Assert.EndsWith("seg.mp4", seenArgs![^1]);
    }

    [Fact]
    public async Task Run_NonZeroExit_ReturnsErrorWithStderrTail()
    {
        var runner = new EdgeFfmpegBackfillRunner(_ => SampleTarget(), "ffmpeg",
            (file, args, _, _) => args is ["-version"]
                ? (0, "ffmpeg version n6.1", "")
                : (2, "", "http error: 404 not found"));

        var result = await runner.RunAsync(SampleJob, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("404", result.Error);
        Assert.Contains("exit=2", result.Error);
    }

    [Fact]
    public async Task Run_Exception_ReturnsFailure()
    {
        var runner = new EdgeFfmpegBackfillRunner(_ => SampleTarget(), "ffmpeg",
            (file, args, _, _) => args is ["-version"]
                ? (0, "ffmpeg version n6.1", "")
                : throw new InvalidOperationException("boom"));

        var result = await runner.RunAsync(SampleJob, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public void IsToolAvailable_True_WhenExitZero()
    {
        var runner = new EdgeFfmpegBackfillRunner(_ => SampleTarget(), "ffmpeg",
            (_, _, _, _) => (0, "ffmpeg version n6.1", ""));

        Assert.True(runner.IsToolAvailable());
    }

    [Fact]
    public async Task IsToolAvailable_False_WhenProcessThrows()
    {
        var runner = new EdgeFfmpegBackfillRunner(_ => SampleTarget(), "ffmpeg",
            (_, _, _, _) => throw new InvalidOperationException("cannot start"));

        Assert.False(runner.IsToolAvailable());
        Assert.False((await runner.RunAsync(SampleJob, CancellationToken.None)).Success);
    }
}