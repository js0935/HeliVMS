using System.Drawing;
using System.Drawing.Imaging;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// M116 (#14.7 #16): redact recorded mask regions onto an encoded still image with
/// pure-BCL decode/fill/re-encode plus the repository-backed facade.
/// </summary>
public class SnapshotRedactionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;

    public SnapshotRedactionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-snapshot-redaction-{Guid.NewGuid():N}.db");
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

    private static byte[] WhiteImage(int width, int height, ImageFormat format)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, format);
        return ms.ToArray();
    }

    private static Color PixelAt(byte[] imageBytes, int x, int y)
    {
        using var ms = new MemoryStream(imageBytes);
        using var bitmap = new Bitmap(ms);
        return bitmap.GetPixel(x, y);
    }

    private static RedactionRegion Region(int x, int y, int width, int height, bool filled = false) =>
        new(0, RedactionSources.Snapshot, 1, 1, DateTime.UtcNow, x, y, width, height, filled, DateTime.UtcNow);

    [Fact]
    public void Apply_BlackensRegionAndLeavesRestUntouched()
    {
        var image = WhiteImage(40, 30, ImageFormat.Jpeg);

        var result = SnapshotRedactor.Apply(image, [Region(4, 4, 12, 8)]);

        Assert.Equal(1, result.AppliedRegions);
        var masked = PixelAt(result.Image, 10, 8);
        Assert.True(masked.R < 32 && masked.G < 32 && masked.B < 32, $"masked pixel should be black, got {masked}");
        var intact = PixelAt(result.Image, 32, 24);
        Assert.True(intact.R > 200 && intact.G > 200 && intact.B > 200, $"outside region should stay white, got {intact}");
    }

    [Fact]
    public void Apply_ClampsOutOfBoundsRegion()
    {
        var image = WhiteImage(20, 20, ImageFormat.Jpeg);

        var result = SnapshotRedactor.Apply(image, [Region(15, 15, 12, 12)]);

        Assert.Equal(1, result.AppliedRegions);
        var corner = PixelAt(result.Image, 19, 19);
        Assert.True(corner.R < 32 && corner.G < 32 && corner.B < 32);
        var edge = PixelAt(result.Image, 0, 0);
        Assert.True(edge.R > 200 && edge.G > 200 && edge.B > 200);
    }

    [Fact]
    public void Apply_SkipsDegenerateAndFullyOutsideRegions()
    {
        var image = WhiteImage(20, 20, ImageFormat.Jpeg);

        var result = SnapshotRedactor.Apply(image, [Region(2, 2, -4, 4), Region(200, 200, 5, 5)]);

        Assert.Equal(0, result.AppliedRegions);
        Assert.True(PixelAt(result.Image, 10, 10).R > 200);
    }

    [Fact]
    public void Apply_MultipleRegions_AllApplied()
    {
        var image = WhiteImage(40, 30, ImageFormat.Jpeg);

        var result = SnapshotRedactor.Apply(image, [Region(0, 0, 8, 8), Region(30, 20, 9, 9)]);

        Assert.Equal(2, result.AppliedRegions);
        Assert.True(PixelAt(result.Image, 4, 4).R < 32);
        Assert.True(PixelAt(result.Image, 34, 24).R < 32);
    }

    [Fact]
    public void Apply_PngRoundTrip_MasksExactPixels()
    {
        var image = WhiteImage(16, 12, ImageFormat.Png);

        var result = SnapshotRedactor.Apply(image, [Region(2, 2, 6, 4)]);

        var masked = PixelAt(result.Image, 4, 3);
        Assert.Equal(0, masked.R);
        Assert.Equal(0, masked.G);
        Assert.Equal(0, masked.B);
        Assert.True(PixelAt(result.Image, 14, 10).R == 255);
    }

    [Fact]
    public void Apply_DoesNotMutateInputBytes()
    {
        var image = WhiteImage(20, 20, ImageFormat.Jpeg);
        var before = (byte[])image.Clone();

        SnapshotRedactor.Apply(image, [Region(0, 0, 5, 5)]);

        Assert.Equal(before, image);
    }

    [Fact]
    public void Redact_UsesRegionsRecordedForSnapshotRefId()
    {
        var redactions = new RedactionRepository(_store);
        long snapshotId = 77;
        var a = redactions.Add(new RedactionRegion(0, RedactionSources.Snapshot, snapshotId, 1, DateTime.UtcNow, 0, 0, 6, 6, false, DateTime.UtcNow));
        var b = redactions.Add(new RedactionRegion(0, RedactionSources.Snapshot, snapshotId, 1, DateTime.UtcNow, 10, 10, 6, 6, false, DateTime.UtcNow));
        Assert.True(a > 0 && b > 0);
        var service = new SnapshotRedactionService(_store);

        var result = service.Redact(WhiteImage(30, 30, ImageFormat.Jpeg), snapshotId);

        Assert.Equal(2, result.AppliedRegions);
        Assert.True(PixelAt(result.Image, 2, 2).R < 32);
        Assert.True(PixelAt(result.Image, 12, 12).R < 32);
    }

    [Fact]
    public void Redact_NoRegions_ReturnsUnmaskedCopy()
    {
        var service = new SnapshotRedactionService(_store);

        var result = service.Redact(WhiteImage(16, 16, ImageFormat.Jpeg), 1234);

        Assert.Equal(0, result.AppliedRegions);
        Assert.True(PixelAt(result.Image, 8, 8).R > 200);
    }
}