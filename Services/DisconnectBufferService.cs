using System.Collections.Concurrent;
using System.IO;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public sealed class DisconnectBufferService : IDisconnectBufferService, IDisposable {
    private readonly ICameraHealthService _health;
    private readonly ICameraService _cameraService;
    private readonly IRecordingService _recordingService;
    private readonly IVideoIndexService _videoIndex;
    private readonly IEventService _eventLog;
    private readonly ConcurrentDictionary<string, BufferedRecording> _buffers = new();
    private readonly string _bufferDir;
    private Timer? _timer;
    private int _bufferedCount;
    private int _flushedCount;

    public int BufferedCount => _bufferedCount;
    public int FlushedCount => _flushedCount;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private sealed class BufferedRecording {
        public string CameraId { get; set; } = "";
        public string CameraName { get; set; } = "";
        public DateTime DisconnectedAt { get; set; }
        public string? TempDir { get; set; }
        public List<string> Segments { get; set; } = [];
    }

    public DisconnectBufferService(ICameraHealthService health, ICameraService cameraService,
        IRecordingService recordingService, IVideoIndexService videoIndex, IEventService eventLog) {
        _health = health;
        _cameraService = cameraService;
        _recordingService = recordingService;
        _videoIndex = videoIndex;
        _eventLog = eventLog;
        _bufferDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Buffer");
        Directory.CreateDirectory(_bufferDir);
    }

    public void Start() {
        _health.HealthChanged += OnHealthChanged;
        _timer = new Timer(_ => FlushBuffers(), null, FlushInterval, FlushInterval);
        Log.Debug("[Buffer] Disconnect buffer service started");
    }

    public void Stop() {
        _health.HealthChanged -= OnHealthChanged;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnHealthChanged() {
        try {
            var items = _health.HealthItems;
            foreach (var item in items) {
                if (!item.IsOnline && item.LastDisconnectedAt.HasValue) {
                    var elapsed = DateTime.Now - item.LastDisconnectedAt.Value;
                    if (elapsed.TotalSeconds < 30 && !_buffers.ContainsKey(item.CameraId)) {
                        StartBuffering(item);
                    }
                } else if (item.IsOnline) {
                    if (_buffers.TryRemove(item.CameraId, out var buf)) {
                        FlushBuffer(buf);
                    }
                }
            }
        } catch (Exception ex) {
            Log.Debug("[Buffer] HealthChanged handler error: {Msg}", ex.Message);
        }
    }

    private void StartBuffering(CameraHealthItem item) {
        try {
            var camera = _cameraService.GetCameraById(item.CameraId);
            if (camera is null) return;

            var tempDir = Path.Combine(_bufferDir, item.CameraId, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(tempDir);

            var buf = new BufferedRecording {
                CameraId = item.CameraId,
                CameraName = item.Name,
                DisconnectedAt = DateTime.Now,
                TempDir = tempDir,
            };
            _buffers[item.CameraId] = buf;
            _bufferedCount++;
            _eventLog.LogWarning("Recording", "DisconnectBuffer",
                $"開始緩衝錄影：{item.Name} (離線緩衝)");
        } catch (Exception ex) {
            Log.Debug("[Buffer] StartBuffering error: {Msg}", ex.Message);
        }
    }

    private void FlushBuffers() {
        foreach (var kv in _buffers) {
            try {
                var buf = kv.Value;
                if (buf.TempDir is not null && Directory.Exists(buf.TempDir)) {
                    var files = Directory.GetFiles(buf.TempDir, "*.ts");
                    foreach (var file in files) {
                        var targetDir = Path.Combine(
                            _recordingService.GetBasePath(),
                            DateTime.Now.ToString("yyyy-MM-dd"),
                            SanitizeName(buf.CameraName));
                        Directory.CreateDirectory(targetDir);
                        var target = Path.Combine(targetDir, Path.GetFileName(file));
                        File.Move(file, target, overwrite: true);
                        buf.Segments.Add(target);
                    }
                }
            } catch (Exception ex) {
                Log.Debug("[Buffer] Flush error for {Camera}: {Msg}", kv.Key, ex.Message);
            }
        }
    }

    private void FlushBuffer(BufferedRecording buf) {
        try {
            FlushBuffers();

            if (buf.Segments.Count > 0) {
                _flushedCount += buf.Segments.Count;
                _eventLog.LogInfo("Recording", "DisconnectBuffer",
                    $"緩衝錄影已寫入：{buf.CameraName} ({buf.Segments.Count} 片段)");
            }

            if (buf.TempDir is not null && Directory.Exists(buf.TempDir)) {
                try { Directory.Delete(buf.TempDir, recursive: true); } catch { }
            }
        } catch (Exception ex) {
            Log.Debug("[Buffer] FlushBuffer error: {Msg}", ex.Message);
        }
    }

    private static string SanitizeName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Where(c => !invalid.Contains(c)));
    }

    public void Dispose() {
        Stop();
    }
}
