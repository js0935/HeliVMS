using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>魚眼矯正請求。</summary>
public sealed record DewarpRequest(
    int ChannelId,
    DateTime StartUtc,
    DateTime EndUtc,
    string OutputPath,
    DewarpSettings Settings,
    bool GenerateHash = true);

/// <summary>魚眼矯正結果。</summary>
public sealed record DewarpResult(
    string OutputPath,
    string? Sha256,
    long FileSizeBytes,
    double DurationSeconds);

/// <summary>
/// 魚眼矯正服務（§14.7 #2 Dewarping）：以 concat demuxer 拼接範圍內 final 區段，
/// 套用 <see cref="DewarpFilter.Build"/> 之 v360 矯正 filter 並重新編碼輸出 MP4。
/// </summary>
public sealed class DewarpService
{
    private readonly SegmentRepository _segRepo;

    public DewarpService(SqliteStore store)
    {
        _segRepo = new SegmentRepository(store);
    }

    public DewarpService(SegmentRepository segRepo)
    {
        _segRepo = segRepo;
    }

    public async Task<DewarpResult> DewarpAsync(
        DewarpRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var segments = _segRepo.ListByRange(request.ChannelId, "main", request.StartUtc, request.EndUtc)
            .OrderBy(s => s.StartUtc)
            .ToList();

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("所選範圍無錄影段落可供矯正。");
        }

        var filter = DewarpFilter.Build(request.Settings);

        var outputDir = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        progress?.Report(new ExportProgress(5, "準備矯正…"));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"helivms-dewarp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var concatFile = Path.Combine(tmpDir, "concat.txt");
            WriteConcatFile(concatFile, segments);

            progress?.Report(new ExportProgress(10, $"正在矯正 {segments.Count} 段錄影…"));

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
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filter);
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
                ?? throw new InvalidOperationException("無法啟動 ffmpeg 矯正進程");

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
                    progress?.Report(new ExportProgress(50, "ffmpeg 矯正中…"));
                    reported50 = true;
                }
            }

            var stderr = await stderrTask;
            var exitCode = proc.ExitCode;

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg 矯正失敗（exit={exitCode}）：{stderr.Trim()}");
            }

            progress?.Report(new ExportProgress(85, "矯正完成，計算 SHA-256…"));

            string? sha256 = null;
            if (request.GenerateHash)
            {
                sha256 = ComputeSha256(request.OutputPath);
            }

            var fi = new FileInfo(request.OutputPath);
            var duration = segments.Sum(s => s.DurationSec ?? 0);

            progress?.Report(new ExportProgress(100, "矯正完成。"));

            return new DewarpResult(request.OutputPath, sha256, fi.Length, duration);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>對單一影片檔抽一格並套用矯正 filter，輸出 PNG（供 UI 預覽）。</summary>
    public static void RenderPreview(string sourcePath, DewarpSettings settings, string outputPng)
    {
        var filter = DewarpFilter.Build(settings);

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
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(filter);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputPng);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg 預覽進程");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 預覽失敗（exit={proc.ExitCode}）：{stderr.Trim()}");
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
