using System.Text.Json;
using HeliVMS.Alarms;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms.Tests;

public class SynopsisTests
{
    private static string TempDir(Guid id, string name)
        => Path.Combine(Path.GetTempPath(), $"helivms-synopsis-{name}-{id:N}");

    private static BgrImage Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 3] = b;
            px[i * 3 + 1] = g;
            px[i * 3 + 2] = r;
        }

        return new BgrImage(w, h, px);
    }

    private static AlarmEventRecord Event(string type, DateTime utc, string? snapshot = null) => new()
    {
        ChannelId = 1,
        EventType = type,
        StartUtc = utc,
        SnapshotPath = snapshot,
    };

    [Fact]
    public void BmpFile_RoundTrips_PixelsAndDims()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "bmp");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Solid(37, 23, 7, 128, 255);
            var path = Path.Combine(dir, "t.bmp");
            BmpFile.Save(src.Pixels, src.Width, src.Height, path);
            var read = BmpFile.Read(path);
            Assert.Equal(37, read.Width);
            Assert.Equal(23, read.Height);
            Assert.Equal(src.Pixels, read.Pixels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BmpFile_SaveUsesPaddedRows_ReadBackAnySize()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "bmp2");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var w in new[] { 1, 2, 3, 4, 5, 40 })
            {
                var h = 9;
                var src = Solid(w, h, 11, 22, 33);
                var path = Path.Combine(dir, $"t{w}.bmp");
                BmpFile.Save(src.Pixels, w, h, path);
                var read = BmpFile.Read(path);
                Assert.Equal(src.Pixels, read.Pixels);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BmpFile_Read_RejectsNon24Bit()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "bad");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bad.bmp");
            File.WriteAllBytes(path, [0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            Assert.Throws<InvalidDataException>(() => BmpFile.Read(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImageOps_ResizeNearest_KeepsUniformColorAndSize()
    {
        var src = Solid(640, 360, 90, 160, 230);
        var dst = ImageOps.ResizeNearest(src.Pixels, 640, 360, 320, 180);
        Assert.Equal(320 * 180 * 3, dst.Length);
        foreach (var px in new[] { 0, 123, (320 * 180) - 1 })
        {
            Assert.Equal(90, dst[px * 3]);
            Assert.Equal(160, dst[px * 3 + 1]);
            Assert.Equal(230, dst[px * 3 + 2]);
        }
    }

    [Fact]
    public void ImageOps_ResizeNearest_UpSizesToo()
    {
        var src = Solid(2, 2, 1, 2, 3);
        var dst = ImageOps.ResizeNearest(src.Pixels, 2, 2, 4, 4);
        Assert.Equal(4 * 4 * 3, dst.Length);
        Assert.Equal(1, dst[0]);
        Assert.Equal(3, dst[2]);
    }

    [Fact]
    public void Build_SortsChronologically_AndRowMajorCells()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "build");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 0, 255).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[]
            {
                Event("motion", t0.AddMinutes(30), snap),
                Event("ai_person", t0, snap),
                Event("tamper", t0.AddMinutes(10), snap),
            };
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(1)), events, dir);
            Assert.NotNull(result);
            Assert.Equal(3, result.Manifest.FrameCount);
            Assert.Equal(["ai_person", "tamper", "motion"], result.Manifest.Cells.Select(c => c.EventType).ToArray());
            Assert.Equal([0, 1, 2], result.Manifest.Cells.Select(c => c.Index).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_FramesWithoutSnapshot_AreSkipped()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "nosnap");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[]
            {
                Event("motion", t0, null),
                Event("ai_person", t0.AddMinutes(1), snap),
            };
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(1)), events, dir);
            Assert.NotNull(result);
            Assert.Equal(1, result.Manifest.FrameCount);
            Assert.Equal("ai_person", Assert.Single(result.Manifest.Cells).EventType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_ColumnsAndRowsMath_Ceil()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "grid");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, 5).Select(i => Event("motion", t0.AddMinutes(i), snap)).ToArray();
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)) { Columns = 4 }, events, dir);
            Assert.NotNull(result);
            Assert.Equal(4, result.Manifest.Columns);
            Assert.Equal(2, result.Manifest.Rows);
            Assert.True(File.Exists(result.SheetPath));
            var sheet = BmpFile.Read(result.SheetPath);
            Assert.Equal(4 * 320, sheet.Width);
            Assert.Equal(2 * 180, sheet.Height);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_BlankCells_AreBlack()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "black");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, 5).Select(i => Event("motion", t0.AddMinutes(i), snap)).ToArray();
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)) { Columns = 4 }, events, dir);
            Assert.NotNull(result);
            var sheet = BmpFile.Read(result.SheetPath);
            var px = sheet.Pixels;
            var bottomRight = new byte[] { px[^3], px[^2], px[^1] };
            Assert.Equal(new byte[] { 0, 0, 0 }, bottomRight);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_MaxFrames_Truncates()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "max");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = Enumerable.Range(0, 12).Select(i => Event("motion", t0.AddMinutes(i), snap)).ToArray();
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)) { MaxFrames = 5 }, events, dir);
            Assert.NotNull(result);
            Assert.Equal(5, result.Manifest.FrameCount);
            Assert.True(result.Manifest.Truncated);
            Assert.Contains("截斷", result.SummaryText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_EventTypes_WhitelistFilters()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "filter");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[]
            {
                Event("motion", t0, snap),
                Event("tamper", t0.AddMinutes(1), snap),
            };
            var result = SynopsisBuilder.Build(
                new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)) { EventTypes = ["tamper"] }, events, dir);
            Assert.NotNull(result);
            Assert.Equal("tamper", Assert.Single(result.Manifest.Cells).EventType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_CorruptSnapshot_IsSkippedAndCounted()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "corrupt");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            var snap2 = Path.Combine(dir, "b.bmp");
            File.WriteAllBytes(snap2, [0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var events = new[]
            {
                Event("motion", t0, snap),
                Event("tamper", t0.AddMinutes(1), snap2),
            };
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)), events, dir);
            Assert.NotNull(result);
            Assert.Equal(1, result.Manifest.Skipped);
            Assert.Contains("讀取失敗", result.SummaryText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_EmptyInput_ReturnsNull_NoFiles()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "empty");
        Directory.CreateDirectory(dir);
        try
        {
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var result = SynopsisBuilder.Build(new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)), [], dir);
            Assert.Null(result);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_ManifestJson_RoundTrips()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "json");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var result = SynopsisBuilder.Build(
                new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)),
                [Event("motion", t0, snap)],
                dir);
            Assert.NotNull(result);
            var json = File.ReadAllText(result.ManifestPath);
            var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetProperty("channelId").GetInt32());
            Assert.Equal(4, doc.RootElement.GetProperty("columns").GetInt32());
            var cells = doc.RootElement.GetProperty("cells");
            Assert.Equal(1, cells.GetArrayLength());
            Assert.Equal("motion", cells[0].GetProperty("eventType").GetString());
            Assert.Equal(320, doc.RootElement.GetProperty("thumbWidth").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_SummaryText_ContainsFrameCountAndRange()
    {
        var id = Guid.NewGuid();
        var dir = TempDir(id, "summary");
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "a.bmp");
            BmpFile.Save(Solid(64, 36, 0, 255, 0).Pixels, 64, 36, snap);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var result = SynopsisBuilder.Build(
                new SynopsisRequest(1, t0.AddMinutes(-5), t0.AddHours(2)),
                [Event("motion", t0, snap)],
                dir);
            Assert.NotNull(result);
            Assert.Contains("摘要：1 幀", result.SummaryText);
            Assert.Contains("×", result.SummaryText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}