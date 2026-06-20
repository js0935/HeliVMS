using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public sealed class MediaMTXService : IDisposable
{
    private readonly ICameraService _cameraService;
    private Process? _process;
    private string _configDir;
    private string _configPath;
    private string _binaryPath;
    private readonly object _lock = new();
    private bool _disposed;
    private Timer? _healthTimer;

    private const int MtxRtspPort = 8554;

    public bool IsRunning => _process is { HasExited: false };

    public event Action? StatusChanged;

    public MediaMTXService(ICameraService cameraService)
    {
        _cameraService = cameraService;
        _configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaMTX");
        _configPath = Path.Combine(_configDir, "mediamtx.yml");
        _binaryPath = Path.Combine(_configDir, "mediamtx.exe");

        _cameraService.CamerasChanged += OnCamerasChanged;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning) { return; }

            EnsureBinaryExists();
            GenerateConfig();
            StartProcess();
            StartHealthCheck();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _healthTimer?.Dispose();
            _healthTimer = null;

            if (_process is { HasExited: false })
            {
                try
                {
                    _process.StandardInput.WriteLine("q");
                    if (!_process.WaitForExit(5000))
                    {
                        _process.Kill();
                        _process.WaitForExit(3000);
                    }
                }
                catch { try { _process.Kill(); } catch { } }
                _process.Dispose();
                _process = null;
                Log.Information("[MediaMTX] Process stopped");
            }
        }
    }

    public void Restart()
    {
        Log.Information("[MediaMTX] Restarting...");
        Stop();
        Start();
    }

    /// <summary>Get the RTSP URL for a camera through MediaMTX relay</summary>
    public string GetRelayRtspUrl(Camera camera, bool useSubStream = false)
    {
        var pathName = GetPathName(camera);
        if (useSubStream && !string.IsNullOrEmpty(camera.RtspUrlSub))
        {
            pathName += "_sub";
        }
        return $"rtsp://127.0.0.1:{MtxRtspPort}/{pathName}";
    }

    /// <summary>Get the RTSP URL with credentials for direct camera access (used by MediaMTX config)</summary>
    public static string GetDirectRtspUrl(Camera camera) =>
        BuildDirectRtspUrl(camera.RtspUrl, camera.Username, camera.Password);

    public static string BuildDirectRtspUrl(string rtspUrl, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return rtspUrl;
        }

        var uri = new Uri(rtspUrl);
        var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
        return $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.AbsolutePath}{uri.Query}";
    }

    public static string GetPathName(Camera camera)
    {
        var ch = camera.ChannelNumber;
        var name = ch.HasValue ? $"CH{ch.Value:D2}" : $"cam_{camera.Id?[..8]}";
        return name.ToLowerInvariant();
    }

    private void EnsureBinaryExists()
    {
        if (File.Exists(_binaryPath)) { return; }

        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "MediaMTX", "mediamtx.exe");
        fallback = Path.GetFullPath(fallback);
        if (File.Exists(fallback))
        {
            Directory.CreateDirectory(_configDir);
            File.Copy(fallback, _binaryPath, overwrite: true);

            var srcYml = Path.Combine(Path.GetDirectoryName(fallback)!, "mediamtx.yml");
            if (File.Exists(srcYml))
            {
                File.Copy(srcYml, Path.Combine(_configDir, "mediamtx.yml.full"), overwrite: true);
            }
        }
        else
        {
            Log.Warning("[MediaMTX] Binary not found at {Path} or {Fallback}", _binaryPath, fallback);
        }
    }

    private void GenerateConfig()
    {
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
        for (int ci = 0; ci < cameras.Count; ci++)
        {
            var cam = cameras[ci];
            if (string.IsNullOrEmpty(cam.RtspUrl) || !cam.IsEnabled) { continue; }
            var pathName = GetPathName(cam);
            var sourceUrl = GetDirectRtspUrl(cam);
            sb.AppendLine($"  {pathName}:");
            sb.AppendLine($"    source: {sourceUrl}");
            sb.AppendLine($"    sourceOnDemand: false");
            if (!string.IsNullOrEmpty(cam.RtspUrlSub))
            {
                var subPathName = $"{pathName}_sub";
                sb.AppendLine($"  {subPathName}:");
                sb.AppendLine($"    source: {BuildDirectRtspUrl(cam.RtspUrlSub, cam.Username, cam.Password)}");
                sb.AppendLine($"    sourceOnDemand: false");
            }
        }

        File.WriteAllText(_configPath, sb.ToString(), new UTF8Encoding(false));
        int enabledCount = 0;
        for (int ci = 0; ci < cameras.Count; ci++)
        {
            if (cameras[ci].IsEnabled) { enabledCount++; }
        }
        Log.Information("[MediaMTX] Config written with {Count} cameras to {Path}", enabledCount, _configPath);
    }

    private void StartProcess()
    {
        if (!File.Exists(_binaryPath))
        {
            Log.Warning("[MediaMTX] Binary not found at {Path}, skipping start", _binaryPath);
            return;
        }

        var psi = new ProcessStartInfo(_binaryPath)
        {
            Arguments = $"\"{_configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _configDir
        };

        try
        {
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
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaMTX] Failed to start process");
        }
    }

    private void StartHealthCheck()
    {
        _healthTimer?.Dispose();
        _healthTimer = new Timer(_ =>
        {
            if (_process is { HasExited: true })
            {
                Log.Warning("[MediaMTX] Process exited unexpectedly (code: {ExitCode}), restarting...", _process.ExitCode);
                lock (_lock)
                {
                    _process?.Dispose();
                    _process = null;
                    StartProcess();
                }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void OnCamerasChanged()
    {
        Log.Information("[MediaMTX] Camera list changed, regenerating config (async)");
        _ = Task.Run(() =>
        {
            lock (_lock)
            {
                GenerateConfig();
                Restart();
            }
        });
    }

    private static async Task ConsumeStream(StreamReader reader, string name, string tag)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                Log.Debug("[MediaMTX:{Tag}] {Line}", tag, line);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _cameraService.CamerasChanged -= OnCamerasChanged;
        Stop();
    }
}
