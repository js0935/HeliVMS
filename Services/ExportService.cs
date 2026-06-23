using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class ExportService : IExportService {
    private readonly IVideoIndexService _videoIndex;
    private readonly ICameraService _cameraService;

    public ExportService(IVideoIndexService videoIndex, ICameraService cameraService) {
        _videoIndex = videoIndex;
        _cameraService = cameraService;
    }

    public string GetDefaultExportPath() {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "HeliVMS_Export");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<string> ExportAsync(ExportRequest request, IProgress<double>? progress = null) {
        var segments = await _videoIndex.QuerySegmentsByCamerasAsync(
            request.CameraIds, request.StartTime, request.EndTime);
        if (segments.Count == 0)
            throw new InvalidOperationException("所選時間範圍內無錄影資料");

        var ffmpeg = FFmpegBinariesHelper.FindFfmpegExecutable();
        if (string.IsNullOrEmpty(ffmpeg))
            throw new InvalidOperationException("找不到 FFmpeg，請先在設定中安裝");

        var outputDir = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var fileListPath = Path.Combine(Path.GetTempPath(), $"helivms_export_{Guid.NewGuid():N}.txt");
        try {
            var ordered = segments.OrderBy(s => s.StartTime).ToList();
            await File.WriteAllLinesAsync(fileListPath,
                ordered.Select(s => $"file '{s.FilePath.Replace("'", "'\\''")}'"));

            var totalSec = (request.EndTime - request.StartTime).TotalSeconds;
            var args = $"-hide_banner -f concat -safe 0 -i \"{fileListPath}\" -c copy -y \"{request.OutputPath}\"";

            var psi = new ProcessStartInfo(ffmpeg, args) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("無法啟動 FFmpeg");

            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg 匯出失敗 (ExitCode={proc.ExitCode}): {stderr}");

            progress?.Report(1.0);
            return request.OutputPath;
        } finally {
            try { if (File.Exists(fileListPath)) File.Delete(fileListPath); } catch { }
        }
    }
}
