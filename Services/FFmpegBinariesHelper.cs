// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using Serilog;

namespace HeliVMS.Services {
    public partial class FFmpegBinariesHelper {
        /// <summary>Get ffmpeg version string, or null if not found (async, safe for UI thread)</summary>
        public static async Task<string?> GetVersionAsync() {
            try {
                var exe = FindFfmpegExecutable();
                var psi = new ProcessStartInfo(exe, "-version") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) { return null; }
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                using var cts = new CancellationTokenSource(3000);
                try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { try { proc.Kill(); } catch { } return null; }
                var output = (await outputTask.ConfigureAwait(false));
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var first = lines.Length > 0 ? lines[0] : null;
                if (first is not null) {
                    var m = MyRegex().Match(first);
                    if (m.Success) { return m.Groups[1].Value; }
                    return first;
                }
                return null;
            } catch {
                return null;
            }
        }

        /// <summary>Get ffmpeg version string, or null if not found (sync fallback)</summary>
        public static string? GetVersion() {
            try {
                var exe = FindFfmpegExecutable();
                var psi = new ProcessStartInfo(exe, "-version") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) { return null; }
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var first = lines.Length > 0 ? lines[0] : null;
                if (first is not null) {
                    var m = MyRegex().Match(first);
                    if (m.Success) { return m.Groups[1].Value; }
                    return first;
                }
                return null;
            } catch {
                return null;
            }
        }

        /// <summary>Find ffmpeg.exe path; falls back to "ffmpeg" via PATH</summary>
        public static string FindFfmpegExecutable() {
            // 0) Check built-in FFmpeg/ directory first
            var builtIn = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "ffmpeg.exe");
            if (File.Exists(builtIn)) { return builtIn; }

            // 1) Check near DLL location
            var libPath = DynamicallyLoadedBindings.LibrariesPath;
            if (!string.IsNullOrEmpty(libPath)) {
                var exe = Path.Combine(libPath, "ffmpeg.exe");
                if (File.Exists(exe)) { return exe; }
            }

            // 2) Check environment variable
            var envPath = Environment.GetEnvironmentVariable("HELIVMS_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(envPath)) {
                var exe = Path.Combine(envPath, "ffmpeg.exe");
                if (File.Exists(exe)) { return exe; }
                exe = Path.Combine(envPath, "bin", Environment.Is64BitProcess ? "x64" : "x86", "ffmpeg.exe");
                if (File.Exists(exe)) { return exe; }
            }

            // 3) Fallback to PATH
            return "ffmpeg";
        }

        internal static void RegisterFFmpegBinaries() {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                throw new NotSupportedException("FFmpeg probing is only supported on Windows.");
            }

            // 0) Check built-in FFmpeg/ directory first
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var builtIn = Path.Combine(baseDir, "FFmpeg");
            if (Directory.Exists(builtIn)) {
                DynamicallyLoadedBindings.LibrariesPath = builtIn;
                return;
            }

            // 1) Check environment variable
            var envPath = Environment.GetEnvironmentVariable("HELIVMS_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath)) {
                DynamicallyLoadedBindings.LibrariesPath = envPath;
                return;
            }

            // 2) Check relative FFmpeg/bin/x64 path
            var probe = Path.Combine("FFmpeg", "bin", Environment.Is64BitProcess ? "x64" : "x86");
            var candidate = Path.Combine(Environment.CurrentDirectory, probe);
            if (Directory.Exists(candidate)) {
                DynamicallyLoadedBindings.LibrariesPath = candidate;
                return;
            }

            // Walk up parent directories
            var dir = new DirectoryInfo(Environment.CurrentDirectory);
            while (dir.Parent is not null) {
                dir = dir.Parent;
                var p = Path.Combine(dir.FullName, "FFmpeg", "bin", Environment.Is64BitProcess ? "x64" : "x86");
                if (Directory.Exists(p)) {
                    DynamicallyLoadedBindings.LibrariesPath = p;
                    return;
                }
            }

            // 3) PATH environment variable
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
                try {
                    if (File.Exists(Path.Combine(p, "avformat.dll"))) {
                        DynamicallyLoadedBindings.LibrariesPath = p;
                        return;
                    }
                } catch { }
            }

            Log.Warning("FFmpeg DLL not found. Place FFmpeg DLLs in the FFmpeg/ directory");
        }

        [GeneratedRegex(@"ffmpeg\s+version\s+(\S+)")]
        private static partial Regex MyRegex();
    }
}
