using HeliVMS.Recording;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

public sealed class ExportServiceTests : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _dbPath;

    public ExportServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-export-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        var ch = new ChannelRepository(_store);
        ChId = ch.Add("CAM1", "rtsp://x/1");
    }

    private readonly int ChId;

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ExportAsync_NoSegmentsInRange_Throws()
    {
        var svc = new ExportService(_store);
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExportAsync(new ExportRequest(ChId, start, end, "x.mp4", null, true)));
    }

    [Fact]
    public async Task ExportAsync_OtherChannelSegments_Throws()
    {
        var segRepo = new SegmentRepository(_store);
        var otherCh = new ChannelRepository(_store).Add("CAM2", "rtsp://x/2");

        var file = Path.Combine(Path.GetTempPath(), $"helivms-exp-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[50]);

        try
        {
            var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var id = segRepo.BeginSegment(otherCh, "main", file, start);
            segRepo.CompleteSegment(id, start.AddSeconds(10), 50, 10, new string('a', 64));

            var svc = new ExportService(_store);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ExportAsync(new ExportRequest(ChId, start, start.AddMinutes(5), "x.mp4", null, true)));
        }
        finally
        {
            try { File.Delete(file); }
            catch { }
        }
    }
}