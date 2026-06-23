using System.Diagnostics;
using System.IO;

namespace HeliVMS.Services;

public sealed class SnapshotService {
    public async Task<string?> TakeSnapshotAsync(string rtspUrl, string outputDir) {
        var ffmpeg = FFmpegBinariesHelper.FindFfmpegExecutable();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var outputPath = Path.Combine(outputDir, $"snapshot_{timestamp}.jpg");

        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

        var args = $"-hide_banner -y -rtsp_transport tcp -i \"{rtspUrl}\" -frames:v 1 -q:v 2 \"{outputPath}\"";
        var psi = new ProcessStartInfo(ffmpeg, args) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var stderr = await proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(10000);

        return proc.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
    }
}
