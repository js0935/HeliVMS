using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public sealed class MediaMTXService : IDisposable {
    private readonly ICameraService _cameraService;
    private Process? _process;
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _binaryPath;
    private readonly SemaphoreSlim _sem = new(1, 1);
    private bool _disposed;
    private Timer? _healthTimer;

    private const int MtxRtspPort = 8554;

    public bool IsRunning => _process is { HasExited: false };

    public event Action? StatusChanged;

    public MediaMTXService(ICameraService cameraService) {
        _cameraService = cameraService;
        _configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaMTX");
        _configPath = Path.Combine(_configDir, "mediamtx.yml");
        _binaryPath = Path.Combine(_configDir, "mediamtx.exe");

        _cameraService.CamerasChanged += OnCamerasChanged;
    }

    public async Task StartAsync() {
        await _sem.WaitAsync().ConfigureAwait(false);
        try {
            if (IsRunning) { return; }
            EnsureBinaryExists();
            GenerateConfig();
            await StartProcessAsync().ConfigureAwait(false);
            StartHealthCheck();
        } finally { _sem.Release(); }
        StatusChanged?.Invoke();
    }

    public void Start() {
        _sem.Wait();
        try {
            if (IsRunning) { return; }
            EnsureBinaryExists();
            GenerateConfig();
            StartProcess();
            StartHealthCheck();
        } finally { _sem.Release(); }
        StatusChanged?.Invoke();
    }

    public async Task StopAsync() {
        await _sem.WaitAsync().ConfigureAwait(false);
        try {
            await StopCoreAsync().ConfigureAwait(false);
        } finally { _sem.Release(); }
    }

    public void Stop() {
        _sem.Wait();
        try {
            StopCoreAsync().GetAwaiter().GetResult();
        } finally { _sem.Release(); }
    }

    private async Task StopCoreAsync() {
        _healthTimer?.Dispose();
        _healthTimer = null;

        if (_process is { HasExited: false }) {
            try {
                _process.StandardInput.WriteLine("q");
                if (!await WaitForExitAsync(_process, 5000).ConfigureAwait(false)) {
                    _process.Kill();
                    await WaitForExitAsync(_process, 3000).ConfigureAwait(false);
                }
            } catch { try { _process.Kill(); } catch { } }
            _process.Dispose();
            _process = null;
            Log.Information("[MediaMTX] Process stopped");
        }
    }

    public async Task RestartAsync() {
        Log.Information("[MediaMTX] Restarting...");
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public void Restart() {
        Log.Information("[MediaMTX] Restarting...");
        Stop();
        Start();
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs) {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Get the RTSP URL for a camera through MediaMTX relay</summary>
    public static string GetRelayRtspUrl(Camera camera, bool useSubStream = false) {
        var pathName = GetPathName(camera);
        if (useSubStream && !string.IsNullOrEmpty(camera.RtspUrlSub)) {
            pathName += "_sub";
        }
        return $"rtsp://127.0.0.1:{MtxRtspPort}/{pathName}";
    }

    /// <summary>Get the RTSP URL with credentials for direct camera access (used by MediaMTX config)</summary>
    public static string GetDirectRtspUrl(Camera camera) =>
        BuildDirectRtspUrl(camera.RtspUrl, camera.Username, camera.Password);

    public static string BuildDirectRtspUrl(string rtspUrl, string? username, string? password) {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) {
            return rtspUrl;
        }

        var uri = new Uri(rtspUrl);
        var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
        return $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.AbsolutePath}{uri.Query}";
    }

    public static string GetPathName(Camera camera) {
        var ch = camera.ChannelNumber;
        var name = ch.HasValue ? $"CH{ch.Value:D2}" : $"cam_{camera.Id?[..8]}";
        return name.ToLowerInvariant();
    }

    private void EnsureBinaryExists() {
        if (File.Exists(_binaryPath)) { return; }

        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "MediaMTX", "mediamtx.exe");
        fallback = Path.GetFullPath(fallback);
        if (File.Exists(fallback)) {
            Directory.CreateDirectory(_configDir);
            File.Copy(fallback, _binaryPath, overwrite: true);

            var srcYml = Path.Combine(Path.GetDirectoryName(fallback)!, "mediamtx.yml");
            if (File.Exists(srcYml)) {
                File.Copy(srcYml, Path.Combine(_configDir, "mediamtx.yml.full"), overwrite: true);
            }
        } else {
            Log.Warning("[MediaMTX] Binary not found at {Path} or {Fallback}", _binaryPath, fallback);
        }
    }

    private void GenerateConfig() {
        Directory.CreateDirectory(_configDir);

        var cameras = _cameraService.GetAllCameras();
        var sb = new StringBuilder();
        sb.AppendLine("# HeliVMS - Auto-generated MediaMTX config");
        sb.AppendLine();

        // Protocol servers
        sb.AppendLine("logLevel: warn");
        sb.AppendLine("logDestinations: [stdout]");
        sb.AppendLine("rtspAddress: :8554");
        sb.AppendLine("rtmp: false");
        sb.AppendLine("hls: true");
        sb.AppendLine("hlsAddress: :8888");
        sb.AppendLine("webrtc: false");
        sb.AppendLine("api: true");
        sb.AppendLine("apiAddress: :9997");
        sb.AppendLine("readTimeout: 30s");
        sb.AppendLine("writeTimeout: 30s");
        sb.AppendLine();

        // Path defaults: pull from camera source, always-on
        sb.AppendLine("pathDefaults:");
        sb.AppendLine("  sourceOnDemand: false");
        sb.AppendLine();

        // Per-camera paths
        sb.AppendLine("paths:");
        var enabledCount = 0;
        for (var ci = 0; ci < cameras.Count; ci++) {
            var cam = cameras[ci];
            if (string.IsNullOrEmpty(cam.RtspUrl) || !cam.IsEnabled) { continue; }
            enabledCount++;
            var pathName = GetPathName(cam);
            var sourceUrl = GetDirectRtspUrl(cam);
            sb.AppendLine($"  {pathName}:");
            sb.AppendLine($"    source: {sourceUrl}");
            sb.AppendLine($"    sourceOnDemand: false");
            if (!string.IsNullOrEmpty(cam.RtspUrlSub)) {
                var subPathName = $"{pathName}_sub";
                sb.AppendLine($"  {subPathName}:");
                sb.AppendLine($"    source: {BuildDirectRtspUrl(cam.RtspUrlSub, cam.Username, cam.Password)}");
                sb.AppendLine($"    sourceOnDemand: false");
            }
        }

        File.WriteAllText(_configPath, sb.ToString(), new UTF8Encoding(false));
        Log.Information("[MediaMTX] Config written with {Count} cameras to {Path}", enabledCount, _configPath);
    }

    private static readonly SemaphoreSlim _killOrphanThrottle = new(4, 4);

    private static async Task KillOrphanedProcessesAsync(CancellationToken cancellationToken = default) {
        try {
            var procs = Process.GetProcessesByName("mediamtx");
            if (procs.Length == 0) return;

            var tasks = new Task[procs.Length];
            for (var pi = 0; pi < procs.Length; pi++) {
                var proc = procs[pi];
                tasks[pi] = KillOneOrphanedAsync(proc, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        } catch (OperationCanceledException) { } catch { }
    }

    private static async Task KillOneOrphanedAsync(Process proc, CancellationToken cancellationToken) {
        await _killOrphanThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (proc.HasExited) return;
            proc.Kill();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(3000);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        } catch { }
        finally { proc.Dispose(); _killOrphanThrottle.Release(); }
    }

    private static void KillOrphanedProcesses() {
        try { KillOrphanedProcessesAsync().GetAwaiter().GetResult(); } catch { }
    }

    private async Task StartProcessAsync() {
        await KillOrphanedProcessesAsync().ConfigureAwait(false);
        if (!File.Exists(_binaryPath)) {
            Log.Warning("[MediaMTX] Binary not found at {Path}, skipping start", _binaryPath);
            return;
        }
        var psi = new ProcessStartInfo(_binaryPath) {
            Arguments = $"\"{_configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _configDir
        };
        try {
            _process = new Process { StartInfo = psi };
            _process.Start();
            var cameraName = "MediaMTX";
            _ = Task.Factory.StartNew(
                () => ConsumeStream(_process.StandardOutput, cameraName, "out"),
                TaskCreationOptions.LongRunning).Unwrap();
            _ = Task.Factory.StartNew(
                () => ConsumeStream(_process.StandardError, cameraName, "err"),
                TaskCreationOptions.LongRunning).Unwrap();
            Log.Information("[MediaMTX] Process started (PID: {Pid})", _process.Id);
        } catch (Exception ex) {
            Log.Error(ex, "[MediaMTX] Failed to start process");
        }
    }

    private void StartProcess() {
        KillOrphanedProcesses();
        if (!File.Exists(_binaryPath)) {
            Log.Warning("[MediaMTX] Binary not found at {Path}, skipping start", _binaryPath);
            return;
        }

        var psi = new ProcessStartInfo(_binaryPath) {
            Arguments = $"\"{_configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _configDir
        };

        try {
            _process = new Process { StartInfo = psi };
            _process.Start();

            var cameraName = "MediaMTX";
            _ = Task.Factory.StartNew(
                () => ConsumeStream(_process.StandardOutput, cameraName, "out"),
                TaskCreationOptions.LongRunning).Unwrap();
            _ = Task.Factory.StartNew(
                () => ConsumeStream(_process.StandardError, cameraName, "err"),
                TaskCreationOptions.LongRunning).Unwrap();

            Log.Information("[MediaMTX] Process started (PID: {Pid})", _process.Id);
        } catch (Exception ex) {
            Log.Error(ex, "[MediaMTX] Failed to start process");
        }
    }

    private void StartHealthCheck() {
        _healthTimer?.Dispose();
        _healthTimer = new Timer(async _ => {
            if (_process is { HasExited: true }) {
                Log.Warning("[MediaMTX] Process exited unexpectedly (code: {ExitCode}), restarting...", _process.ExitCode);
                await _sem.WaitAsync().ConfigureAwait(false);
                try {
                    _process?.Dispose();
                    _process = null;
                    await StartProcessAsync().ConfigureAwait(false);
                } finally { _sem.Release(); }
                StatusChanged?.Invoke();
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void OnCamerasChanged() {
        Log.Information("[MediaMTX] Camera list changed, regenerating config");
        _ = OnCamerasChangedAsync();
    }

    private async Task OnCamerasChangedAsync() {
        await _sem.WaitAsync().ConfigureAwait(false);
        try {
            GenerateConfig();
            await RestartAsync().ConfigureAwait(false);
        } finally { _sem.Release(); }
    }

    private static async Task ConsumeStream(StreamReader reader, string _, string tag) {
        try {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null) {
                Log.Debug("[MediaMTX:{Tag}] {Line}", tag, line);
            }
        } catch { }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _cameraService.CamerasChanged -= OnCamerasChanged;
        Stop();
    }
}
