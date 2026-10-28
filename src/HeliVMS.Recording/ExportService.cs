using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>匯出進度回報。</summary>
public sealed record ExportProgress(double Percent, string Status);

/// <summary>
/// 匯出請求參數。<paramref name="RedactionRois"/>（選填）為要套用的遮蔽區域（§14.7 #4/#5）；
/// 非空時匯出改用 <c>-filter_complex</c> 串接遮罩並與浮水印合併，空／null 維持原本無遮罩路徑。
/// 可用 <see cref="ExportService.LoadStoredRois"/> 取回該頻道/範圍已登錄的遮蔽區域後傳入，
/// 達成「匯出自動套用遮蔽」。
/// </summary>
public sealed record ExportRequest(
    int ChannelId,
    DateTime StartUtc,
    DateTime EndUtc,
    string OutputPath,
    string? WatermarkText,
    bool GenerateHash,
    IReadOnlyList<RedactionRoi>? RedactionRois = null);

/// <summary>匯出結果。</summary>
public sealed record ExportResult(
    string OutputPath,
    string? Sha256Hash,
    long FileSizeBytes,
    double DurationSeconds);

/// <summary>
/// 匯出服務（§8.5/§14）：ffmpeg concat demuxer + re-encode 輸出標準 MP4；
/// 可選浮水印（drawtext）、遮蔽區域遮罩（§14.7 #4/#5）、SHA-256 雜湊附檔。
/// </summary>
public sealed class ExportService
{
    private readonly SegmentRepository _segRepo;
    private readonly RedactionRepository _redactions;
    private readonly ExportReceiptService _receipts;

    public ExportService(SqliteStore store)
    {
        _segRepo = new SegmentRepository(store);
        _redactions = new RedactionRepository(store);
        _receipts = new ExportReceiptService(store);
    }

    /// <summary>取回該頻道/時間範圍已登錄的遮蔽區域（M101 倉儲）並轉為 filter 用 ROI。</summary>
    public IReadOnlyList<RedactionRoi> LoadStoredRois(int channelId, DateTime fromUtc, DateTime toUtc)
        => RedactionFilter.FromRegions(_redactions.QueryByTime(channelId, fromUtc, toUtc));

    /// <summary>
    /// 組出匯出用的 ffmpeg filter（末端必為 <c>[vout]</c>，已 <c>-map [vout]</c>）。
    /// 遮蔽區域先套、浮水印後疊；純字串建構，測試不需 ffmpeg。
    /// </summary>
    public static string BuildFilterComplex(string? watermarkText, IReadOnlyList<RedactionRoi> redactionRois)
    {
        ArgumentNullException.ThrowIfNull(redactionRois);
        if (redactionRois.Count == 0)
        {
            throw new InvalidOperationException("至少需一個遮蔽區域。");
        }

        var drawtext = BuildDrawtext(watermarkText);
        if (drawtext is null)
        {
            return RedactionFilter.Build(redactionRois);
        }

        return RedactionFilter.Build(redactionRois, "[redact]") + $";[redact]{drawtext}[vout]";
    }

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var segments = _segRepo.ListPlayableByRange(request.ChannelId, "main", request.StartUtc, request.EndUtc)
            .OrderBy(s => s.StartUtc)
            .ToList();

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("所選範圍無錄影段落可供匯出。");
        }

        // 匯出比播放更怕缺檔：concat 清單只要有一個檔案不存在，ffmpeg 就整批失敗，
        // 而症狀是「匯出壞掉」而不是「其中幾段被刪了」。這裡把已經不在磁碟上的段剔除，
        // 一段都不剩才報錯，並在進度訊息裡講清楚剔除了幾段——否則操作員拿到的成品
        // 會少一段而沒有任何說明。對帳（SegmentIndexReconciler）負責把這些段寫回索引，
        // 但匯出不能等那一輪，當下就要過濾。
        var skipped = segments.RemoveAll(s => !File.Exists(s.FilePath));
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("所選範圍的錄影檔案已不存在於磁碟。");
        }

        if (skipped > 0)
        {
            progress?.Report(new ExportProgress(4, $"已略過 {skipped} 段不存在的錄影。"));
        }

        progress?.Report(new ExportProgress(5, "準備匯出…"));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"helivms-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var concatFile = Path.Combine(tmpDir, "concat.txt");
            WriteConcatFile(concatFile, segments);

            progress?.Report(new ExportProgress(10, $"正在匯出 {segments.Count} 段錄影…"));

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("warning");
            psi.ArgumentList.Add("-stats");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatFile);

            var hasRedaction = request.RedactionRois is { Count: > 0 };
            if (hasRedaction)
            {
                // §14.7 #4/#5：有遮蔽區域時走 filter_complex；浮水印若有則接在遮蔽之後。
                psi.ArgumentList.Add("-filter_complex");
                psi.ArgumentList.Add(BuildFilterComplex(request.WatermarkText, request.RedactionRois!));
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("[vout]");
                // 有遮蔽時只 map 了視訊，音軌需另行 map；'?' 讓無音軌的來源不致命。
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a?");
            }
            else if (BuildDrawtext(request.WatermarkText) is { } drawtext)
            {
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(drawtext);
            }

            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("ultrafast");
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("23");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("128k");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(request.OutputPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("無法啟動 ffmpeg 匯出進程");

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            var reported50 = false;
            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); }
                    catch { }
                    ct.ThrowIfCancellationRequested();
                }

                await Task.Delay(500, ct);

                if (!reported50)
                {
                    progress?.Report(new ExportProgress(50, "ffmpeg 編碼中…"));
                    reported50 = true;
                }
            }

            var stderr = await stderrTask;
            var exitCode = proc.ExitCode;

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg 匯出失敗（exit={exitCode}）：{RtspUri.RedactText(stderr.Trim())}");
            }

            progress?.Report(new ExportProgress(85, "匯出完成，計算 SHA-256…"));

            string? sha256 = null;
            if (request.GenerateHash)
            {
                sha256 = ComputeSha256(request.OutputPath);
                var hashPath = request.OutputPath + ".sha256";
                await File.WriteAllTextAsync(hashPath,
                    $"{sha256}  {Path.GetFileName(request.OutputPath)}{Environment.NewLine}",
                    ct);

                // M240：雜湊只能證明「沒變」，簽章才證明「來自這套系統」（§14.3(2)）。
                _receipts.Write(
                    request.OutputPath,
                    sha256,
                    new FileInfo(request.OutputPath).Length,
                    segments.Sum(s => s.DurationSec ?? 0),
                    request.ChannelId,
                    "main",
                    request.StartUtc,
                    request.EndUtc,
                    DateTime.UtcNow);
                progress?.Report(new ExportProgress(95, "SHA-256 附檔與簽章收據已建立。"));
            }

            var fi = new FileInfo(request.OutputPath);
            var duration = segments.Sum(s => s.DurationSec ?? 0);

            progress?.Report(new ExportProgress(100, "匯出完成。"));

            return new ExportResult(request.OutputPath, sha256, fi.Length, duration);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>組出 drawtext 浮水印運算式；無浮水印回 null。</summary>
    private static string? BuildDrawtext(string? watermarkText)
    {
        if (string.IsNullOrWhiteSpace(watermarkText))
        {
            return null;
        }

        var escapedText = watermarkText.Replace("'", "\\'").Replace(":", "\\:");
        var fontPart = File.Exists(@"C:\Windows\Fonts\arial.ttf")
            ? ":fontfile='C\\:/Windows/Fonts/arial.ttf'"
            : string.Empty;
        return $"drawtext=text='{escapedText}'{fontPart}:fontsize=20:fontcolor=white:x=10:y=10:box=1:boxcolor=black@0.5";
    }

    private static void WriteConcatFile(string path, IReadOnlyList<SegmentRecord> segments)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            sb.Append("file '");
            sb.Append(seg.FilePath.Replace("'", "'\\''"));
            sb.Append('\'');
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
