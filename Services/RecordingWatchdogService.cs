using Serilog;

namespace HeliVMS.Services;

public sealed class RecordingWatchdogService : IRecordingWatchdogService, IDisposable {
    private readonly IRecordingService _recordingService;
    private readonly ICameraService _cameraService;
    private readonly IEventService _eventLog;
    private Timer? _timer;
    private int _restartedCount;
    private readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    public int RestartedCount => _restartedCount;
    public event Action<string>? RecordingRestarted;

    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

    public RecordingWatchdogService(IRecordingService recordingService, ICameraService cameraService, IEventService eventLog) {
        _recordingService = recordingService;
        _cameraService = cameraService;
        _eventLog = eventLog;
    }

    public void Start() {
        _timer?.Dispose();
        _timer = new Timer(_ => WatchLoop(), null, WatchInterval, WatchInterval);
        Log.Debug("[Watchdog] Recording watchdog started");
    }

    public void Stop() {
        _timer?.Dispose();
        _timer = null;
    }

    private void WatchLoop() {
        try {
            var cameras = _cameraService.GetAllCameras();
            var active = _recordingService.GetActiveRecordings();
            var activeIds = new HashSet<string>(active.Select(a => a.CameraId), StringComparer.OrdinalIgnoreCase);

            foreach (var cam in cameras) {
                if (!cam.IsEnabled || !cam.IsRecordingEnabled) continue;
                if (!cam.IsConnected) continue;
                if (activeIds.Contains(cam.Id)) continue;
                if (_suppressed.Contains(cam.Id)) continue;

                var ok = _recordingService.StartRecording(cam);
                if (ok) {
                    _restartedCount++;
                    _eventLog.LogWarning("Recording", "Watchdog",
                        $"自動重啟錄影：{cam.Name} (重啟次數：{_restartedCount})");
                    RecordingRestarted?.Invoke(cam.Id);
                    Log.Debug("[Watchdog] Restarted recording for {Name}", cam.Name);
                    _suppressed.Remove(cam.Id);
                } else {
                    _suppressed.Add(cam.Id);
                }
            }

            // Trim suppressed list when cameras become active again
            _suppressed.RemoveWhere(id => activeIds.Contains(id));
        } catch (Exception ex) {
            Log.Debug("[Watchdog] Watch loop error: {Msg}", ex.Message);
        }
    }

    public void Dispose() {
        _timer?.Dispose();
    }
}
