// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Diagnostics;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public sealed class RecordingSchedulerService(
    ICameraService cameraService,
    IRecordingService recordingService,
    IEventService eventLog) : IDisposable {
    private readonly ICameraService _cameraService = cameraService;
    private readonly IRecordingService _recordingService = recordingService;
    private readonly IEventService _eventLog = eventLog;
    private Timer? _timer;
    private bool _isRunning;
    private bool _diskLowReported; // Prevents duplicate low-disk EventLog entries

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public void Start() {
        Log.Debug("[HeliVMS] RecordingSchedulerService started");
        _eventLog.LogInfo(EventCategories.Operation, "RecordingScheduler", "Recording scheduler started");

        _ = CheckSchedulesAsync();
        _timer = new Timer(_ => { _ = CheckSchedulesAsync(); }, null, CheckInterval, CheckInterval);
    }

    public void Stop(Action<string>? onProgress = null) {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _recordingService.StopAll(onProgress);
        Log.Debug("[HeliVMS] RecordingSchedulerService stopped");
    }

    public void RunOnce() {
        _ = CheckSchedulesAsync();
    }

    private async Task CheckSchedulesAsync() {
        if (_isRunning) { return; }
        _isRunning = true;

        try {
            var now = DateTime.Now;
            var cameras = _cameraService.GetAllCameras();
            var anyStarted = false;

            foreach (var camera in cameras) {
                try {
                    if (EvaluateCamera(camera, now))
                        anyStarted = true;
                } catch (Exception ex) {
                    Log.Debug("[HeliVMS] RecordingScheduler: error evaluating camera {Name}: {Msg}", camera.Name, ex.Message);
                }

                // Stagger start times to avoid flooding ffmpeg processes simultaneously
                if (anyStarted) {
                    await Task.Delay(300).ConfigureAwait(false);
                }
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] RecordingSchedulerService error: {Msg}", ex.Message);
        } finally {
            _isRunning = false;
        }
    }

    /// <summary>Evaluate whether a camera needs recording started or stopped</summary>
    /// <returns>true if recording was started</returns>
    private bool EvaluateCamera(Camera camera, DateTime now) {
        if (!camera.IsEnabled || string.IsNullOrEmpty(camera.RtspUrl) || !camera.ChannelNumber.HasValue) {
            if (_recordingService.IsRecording(camera.Id)) {
                _recordingService.StopRecording(camera.Id);
            }
            return false;
        }

        var config = CameraRecordingConfigData.Deserialize(camera.RecordingConfigJson)
                     ?? new CameraRecordingConfigData();
        var shouldRecord = ShouldRecordNow(config, now);
        var isRecording = _recordingService.IsRecording(camera.Id);

        if (shouldRecord && !isRecording) {
            // Check disk space before starting new recording
            if (_recordingService.IsDiskLowSpace()) {
                if (!_diskLowReported) {
                    _diskLowReported = true;
                    _eventLog.LogWarning(EventCategories.System, "RecordingScheduler",
                        $"Low disk space, path: {_recordingService.GetBasePath()}");
                }
                return false;
            }
            _diskLowReported = false;

            // Check backoff before starting recording
            if (_recordingService.IsInBackoff(camera.Id)) {
                Log.Debug("[HeliVMS] RecordingScheduler: skipping {Name} (in backoff)", camera.Name);
                return false;
            }

            Log.Debug("[HeliVMS] RecordingScheduler: starting recording for {Name}", camera.Name);
            _recordingService.StartRecording(camera);
            return true;
        } else if (!shouldRecord && isRecording) {
            Log.Debug("[HeliVMS] RecordingScheduler: stopping recording for {Name}", camera.Name);
            _recordingService.StopRecording(camera.Id);
            _recordingService.ResetBackoff(camera.Id);
        }
        return false;
    }

    private static bool ShouldRecordNow(CameraRecordingConfigData config, DateTime now) {
        if (config.IsContinuousEnabled) {
            var dayIndex = (int)now.DayOfWeek;
            var hour = now.Hour;

            if (config.ContinuousSchedule is not null &&
                dayIndex >= 0 && dayIndex < config.ContinuousSchedule.Length &&
                config.ContinuousSchedule[dayIndex].Hours is not null &&
                hour >= 0 && hour < config.ContinuousSchedule[dayIndex].Hours.Length &&
                config.ContinuousSchedule[dayIndex].Hours[hour]) {
                return true;
            }
        }

        return false;
    }

    public void Dispose() {
        _timer?.Dispose();
    }
}
