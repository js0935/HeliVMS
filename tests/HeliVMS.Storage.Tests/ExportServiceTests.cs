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

    [Fact]
    public void BuildFilterComplex_NoWatermark_EqualsRedactionFilter()
    {
        var rois = new[] { new RedactionRoi(10, 20, 30, 40) };

        var filter = ExportService.BuildFilterComplex(null, rois);

        Assert.Equal(RedactionFilter.Build(rois), filter);
        Assert.EndsWith("[vout]", filter);
    }

    [Fact]
    public void BuildFilterComplex_WithWatermark_ChainsDrawtextAfterMask()
    {
        var rois = new[] { new RedactionRoi(10, 20, 30, 40) };

        var filter = ExportService.BuildFilterComplex("機密", rois);

        Assert.Contains("[redact]drawtext=", filter);
        Assert.Contains("text='機密'", filter);
        Assert.EndsWith("[vout]", filter);
        // 遮蔽鏈在前、浮水印在後，且中間不殘留 [redact] 以外的輸出標籤。
        Assert.Contains("[0:v]split=2[s0][s1];", filter);
    }

    [Fact]
    public void BuildFilterComplex_Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExportService.BuildFilterComplex(null, Array.Empty<RedactionRoi>()));
        Assert.Throws<ArgumentNullException>(() =>
            ExportService.BuildFilterComplex(null, null!));
    }

    [Fact]
    public void LoadStoredRois_ReturnsOnlyRegionsInsideRange()
    {
        var redactions = new RedactionRepository(_store);
        var t = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        redactions.Add(RedactionSources.Clip, 9, ChId, t.AddMinutes(1), 5, 6, 7, 8, filled: true, t);
        redactions.Add(RedactionSources.Clip, 9, ChId, t.AddMinutes(30), 1, 1, 1, 1, filled: false, t);

        var rois = new ExportService(_store).LoadStoredRois(ChId, t, t.AddMinutes(5));

        var roi = Assert.Single(rois);
        Assert.Equal(new RedactionRoi(5, 6, 7, 8, true), roi);
    }

    [Fact]
    public void ExportRequest_RedactionRois_DefaultsToNull()
    {
        var request = new ExportRequest(ChId, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), "x.mp4", null, true);

        Assert.Null(request.RedactionRois);
    }
}