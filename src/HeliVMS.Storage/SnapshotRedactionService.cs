using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace HeliVMS.Storage;

/// <summary>
/// Snapshot one-click redaction outcome (M116, section 14.7 #16).
/// </summary>
public sealed record SnapshotRedactionResult(byte[] Image, int AppliedRegions);

/// <summary>
/// Applies recorded <see cref="RedactionRegion"/> rectangles onto an encoded still image
/// (M116). Pure BCL: decode -> fill every region solid via <see cref="RedactionProcessor.Apply"/>
/// (filled=true, the solid-mask pass) -> re-encode preserving the original format
/// (JPEG/PNG). The input buffer is never mutated.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SnapshotRedactor
{
    /// <summary>
    /// Overlays <paramref name="regions"/> onto a decoded image and returns a re-encoded copy.
    /// Out-of-bounds regions are clamped to the canvas; degenerate areas are skipped.
    /// </summary>
    public static SnapshotRedactionResult Apply(byte[] imageBytes, IReadOnlyList<RedactionRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(regions);

        using var input = new MemoryStream(imageBytes);
        using var bitmap = new Bitmap(input);

        var width = bitmap.Width;
        var height = bitmap.Height;
        var bounds = new Rectangle(0, 0, width, height);

        var rect = bounds;
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(data.Stride);
        var pixels = new byte[stride * height];
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int applied = 0;
        foreach (var region in regions)
        {
            var regionRect = new Rectangle(region.X, region.Y, region.Width, region.Height);
            if (regionRect.Width <= 0 || regionRect.Height <= 0)
            {
                continue;
            }

            regionRect.Intersect(bounds);
            if (regionRect.Width <= 0 || regionRect.Height <= 0)
            {
                continue;
            }

            RedactionProcessor.Apply(
                pixels, width, height,
                regionRect.X, regionRect.Y, regionRect.Width, regionRect.Height,
                filled: true);
            applied++;
        }

        data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, SnapshotFormat(imageBytes));

        return new SnapshotRedactionResult(output.ToArray(), applied);
    }

    private static ImageFormat SnapshotFormat(byte[] imageBytes)
    {
        bool IsPng =
            imageBytes.Length >= 8 &&
            imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47 &&
            imageBytes[4] == 0x0D && imageBytes[5] == 0x0A && imageBytes[6] == 0x1A && imageBytes[7] == 0x0A;
        return IsPng ? ImageFormat.Png : ImageFormat.Jpeg;
    }
}

/// <summary>
/// Snapshot masking facade over persisted regions (M116): resolves the recorded
/// <see cref="RedactionRegion"/> list for a snapshot row via the redaction store and
/// applies it with <see cref="SnapshotRedactor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SnapshotRedactionService
{
    private readonly RedactionRepository _redactions;

    public SnapshotRedactionService(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _redactions = new RedactionRepository(store);
    }

    /// <summary>
    /// Masks every region recorded for <paramref name="snapshotRefId"/> (source type
    /// <see cref="RedactionSources.Snapshot"/>) onto <paramref name="imageBytes"/>.
    /// </summary>
    public SnapshotRedactionResult Redact(byte[] imageBytes, long snapshotRefId)
    {
        var regions = _redactions.QueryBySource(RedactionSources.Snapshot, snapshotRefId);
        return SnapshotRedactor.Apply(imageBytes, regions);
    }
}