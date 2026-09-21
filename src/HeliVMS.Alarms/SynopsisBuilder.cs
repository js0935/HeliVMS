using System.Text.Json;
using System.Text.Json.Serialization;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>影片摘要請求參數（M63 §5.9）。</summary>
public sealed record SynopsisRequest(
    int ChannelId,
    DateTime FromUtc,
    DateTime ToUtc)
{
    /// <summary>拼貼中每格縮圖寬（預設 320，16:9）。</summary>
    public int ThumbWidth { get; init; } = 320;

    /// <summary>拼貼中每格縮圖高（預設 180，16:9）。</summary>
    public int ThumbHeight { get; init; } = 180;

    /// <summary>網格欄數（預設 4）。</summary>
    public int Columns { get; init; } = 4;

    /// <summary>最大納入幀數（預設 200；超出截斷並在摘要註記）。</summary>
    public int MaxFrames { get; init; } = 200;

    /// <summary>事件類型白名單（null／空＝不限定，凡有快照皆納入）。</summary>
    public IReadOnlyList<string>? EventTypes { get; init; }
}

/// <summary>拼貼中一格（一幀）之清單項目。</summary>
public sealed record SynopsisCell(int Index, DateTime Utc, string EventType, string SourceSnapshot);

/// <summary>影片摘要產物清單（manifest JSON 內容對應欄位）。</summary>
public sealed record SynopsisManifest(
    int ChannelId,
    DateTime FromUtc,
    DateTime ToUtc,
    int ThumbWidth,
    int ThumbHeight,
    int Columns,
    int Rows,
    DateTime FirstUtc,
    DateTime LastUtc,
    int FrameCount,
    int Skipped,
    bool Truncated,
    string SheetPath,
    string ManifestPath,
    IReadOnlyList<SynopsisCell> Cells);

/// <summary>影片摘要建構結果。</summary>
public sealed record SynopsisResult(
    SynopsisManifest Manifest,
    string SheetPath,
    string ManifestPath,
    string SummaryText);

/// <summary>
/// 影片摘要建構器（M63 §5.9）：將一段時間內「偵測物件之動態幀」（事件快照 BMP）縮圖並以 row-major
/// 時間序排入 N 欄網格，輸出拼貼 BMP＋manifest JSON＋摘要文字。純 C# 位圖處理、零外部依賴；
/// 無輸入時回傳 null 且不產任何檔。
/// </summary>
public static class SynopsisBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SynopsisResult? Build(SynopsisRequest request, IReadOnlyList<AlarmEventRecord> events, string outputDir)
    {
        var frameEvents = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SnapshotPath))
            .Where(e => request.EventTypes is null || request.EventTypes.Count == 0 || request.EventTypes.Contains(e.EventType))
            .OrderBy(e => e.StartUtc)
            .Take(request.MaxFrames)
            .ToList();

        if (frameEvents.Count == 0)
        {
            return null;
        }

        var rows = (frameEvents.Count + request.Columns - 1) / request.Columns;
        var sheetW = request.Columns * request.ThumbWidth;
        var sheetH = rows * request.ThumbHeight;
        var sheet = new byte[sheetW * sheetH * 3];

        var cells = new List<SynopsisCell>(frameEvents.Count);
        var skipped = 0;
        for (var i = 0; i < frameEvents.Count; i++)
        {
            var evt = frameEvents[i];
            var col = i % request.Columns;
            var row = i / request.Columns;
            var ok = TryPlaceSnapshot(request, evt.SnapshotPath!, sheet, sheetW, col, row);
            if (!ok)
            {
                skipped++;
            }

            cells.Add(new SynopsisCell(i, evt.StartUtc, evt.EventType, evt.SnapshotPath!));
        }

        Directory.CreateDirectory(outputDir);
        var baseName = $"synopsis-ch{request.ChannelId}-{request.FromUtc:yyyyMMddHHmmss}-{request.ToUtc:yyyyMMddHHmmss}";
        var sheetPath = Path.Combine(outputDir, baseName + ".bmp");
        var manifestPath = Path.Combine(outputDir, baseName + ".json");
        BmpFile.Save(sheet, sheetW, sheetH, sheetPath);

        var first = frameEvents[0].StartUtc.ToLocalTime();
        var last = frameEvents[^1].StartUtc.ToLocalTime();
        var manifest = new SynopsisManifest(
            request.ChannelId,
            request.FromUtc,
            request.ToUtc,
            request.ThumbWidth,
            request.ThumbHeight,
            request.Columns,
            rows,
            frameEvents[0].StartUtc,
            frameEvents[^1].StartUtc,
            frameEvents.Count,
            skipped,
            frameEvents.Count >= request.MaxFrames && events.Count > request.MaxFrames,
            sheetPath,
            manifestPath,
            cells);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json);

        var summary = $"摘要：{frameEvents.Count} 幀（{rows}×{request.Columns} 網格）；{first:HH:mm}–{last:HH:mm}";
        if (manifest.Truncated)
        {
            summary += $"；已截斷至 {request.MaxFrames} 幀";
        }

        if (skipped > 0)
        {
            summary += $"；{skipped} 幀讀取失敗略過";
        }

        return new SynopsisResult(manifest, sheetPath, manifestPath, summary);
    }

    private static bool TryPlaceSnapshot(SynopsisRequest request, string snapshotPath, byte[] sheet, int sheetW, int col, int row)
    {
        try
        {
            var img = BmpFile.Read(snapshotPath);
            var thumb = ImageOps.ResizeNearest(img.Pixels, img.Width, img.Height, request.ThumbWidth, request.ThumbHeight);
            var dstX = col * request.ThumbWidth;
            var dstY = row * request.ThumbHeight;
            var dstRowStride = request.ThumbWidth * 3;
            for (var y = 0; y < request.ThumbHeight; y++)
            {
                Buffer.BlockCopy(thumb, y * dstRowStride, sheet, (dstY + y) * sheetW * 3 + dstX * 3, dstRowStride);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}