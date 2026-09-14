using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>匯出進度回報。</summary>
public sealed record ExportProgress(double Percent, string Status);

/// <summary>匯出請求參數。</summary>
public sealed record ExportRequest(
    int ChannelId,
    DateTime StartUtc,
    DateTime EndUtc,
    string OutputPath,
    string? WatermarkText,
    bool GenerateHash);

/// <summary>匯出結果。</summary>
public sealed record ExportResult(
    string OutputPath,
    string? Sha256Hash,
    long FileSizeBytes,
    double DurationSeconds);

/// <summary>
/// 匯出服務（§8.5/§14）：ffmpeg concat demuxer + re-encode 輸出標準 MP4；
/// 可選浮水印（drawtext）、SHA-256 雜湊附檔。
/// </summary>
public sealed class ExportService
{
    private readonly SegmentRepository _segRepo;

    public ExportService(SqliteStore store)
    {
        _segRepo = new SegmentRepository(store);
    }

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var segments = _segRepo.ListByRange(request.ChannelId, "main", request.StartUtc, request.EndUtc)
            .OrderBy(s => s.StartUtc)
            .ToList();

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("所選範圍無錄影段落可匯出。");
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
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatFile);

            if (!string.IsNullOrWhiteSpace(request.WatermarkText))
            {
                var escapedText = request.WatermarkText.Replace("'", "\\'").Replace(":", "\\:");
                var fontPart = File.Exists(@"C:\Windows\Fonts\arial.ttf")
                    ? ":fontfile='C\\:/Windows/Fonts/arial.ttf'"
                    : string.Empty;
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(
                    $"drawtext=text='{escapedText}'{fontPart}:fontsize=20:fontcolor=white:x=10:y=10:box=1:boxcolor=black@0.5");
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
                throw new InvalidOperationException($"ffmpeg 匯出失敗（exit={exitCode}）：{stderr.Trim()}");
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
                progress?.Report(new ExportProgress(95, "SHA-256 附檔已建立。"));
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