using System.Diagnostics;
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

    private int AddChannel(string name)
    {
        return new ChannelRepository(_store).Add(name, $"rtsp://x/{Guid.NewGuid():N}");
    }

    private void SeedVideoSegment(int channelId, DateTime startUtc, string color)
    {
        var file = Path.Combine(_dir, $"seg-{Guid.NewGuid():N}.mp4");
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"color={color}:s=160x120:d=2");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add(file);

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);

        var segRepo = new SegmentRepository(_store);
        var id = segRepo.BeginSegment(channelId, "main", file, startUtc);
        segRepo.CompleteSegment(id, startUtc.AddSeconds(2), new FileInfo(file).Length, 2, "seed");
    }

    [Fact]
    public async Task ProcessQueued_Empty_Noop()
    {
        var svc = new ExportJobService(_store);
        var result = await svc.ProcessQueuedAsync(_dir);

        Assert.Equal(0, result.Processed);
    }

    [Fact]
    public async Task ProcessQueued_TwoJobs_BothDoneWithVerifiableOutput()
    {
        var ch1 = AddChannel("A");
        var ch2 = AddChannel("B");
        SeedVideoSegment(ch1, Base, "blue");
        SeedVideoSegment(ch2, Base, "green");

        var jobs = new ExportJobRepository(_store);
        var j1 = jobs.Enqueue(ch1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));
        var j2 = jobs.Enqueue(ch2, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store);
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
        var ch1 = AddChannel("A");
        var ch2 = AddChannel("B");
        SeedVideoSegment(ch2, Base, "red");

        var jobs = new ExportJobRepository(_store);
        var bad = jobs.Enqueue(ch1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));
        var good = jobs.Enqueue(ch2, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store);
        var result = await svc.ProcessQueuedAsync(_dir);

        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("failed", jobs.Get(bad)!.Status);
        Assert.Equal("done", jobs.Get(good)!.Status);
        Assert.Contains("無錄影段落", jobs.Get(bad)!.Error);
    }

    [Fact]
    public async Task ProcessQueued_OutputsUseJobIdInName()
    {
        var ch1 = AddChannel("A");
        SeedVideoSegment(ch1, Base, "orange");

        var jobs = new ExportJobRepository(_store);
        var j1 = jobs.Enqueue(ch1, "main", Base.AddMinutes(-2), Base.AddMinutes(2));

        var svc = new ExportJobService(_store);
        await svc.ProcessQueuedAsync(_dir);

        var done = jobs.Get(j1)!;
        Assert.Contains($"export-job{j1}", done.OutputPath);
    }
}