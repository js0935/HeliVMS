using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class MotionTriggeredRecordingService : IDisposable {
    private readonly IMotionAnalysisService _motion;
    private readonly IRecordingService _recording;
    private readonly ICameraService _cameras;
    private readonly IEventService _events;
    private readonly Dictionary<string, DateTime> _lastTrigger = [];
    private const int CooldownSeconds = 30;
    private const int PreRecordSeconds = 5;
    private const int PostRecordSeconds = 15;
    private bool _disposed;

    public MotionTriggeredRecordingService(
        IMotionAnalysisService motion,
        IRecordingService recording,
        ICameraService cameras,
        IEventService events) {
        _motion = motion;
        _recording = recording;
        _cameras = cameras;
        _events = events;
        _motion.MotionDetected += OnMotionDetected;
    }

    private void OnMotionDetected(string cameraId, double confidence) {
        if (_disposed) return;
        var cam = _cameras.GetCameraById(cameraId);
        if (cam is null || !cam.IsMotionDetectionEnabled || cam.IsRecordingEnabled) return;
        if (_lastTrigger.TryGetValue(cameraId, out var last) &&
            (DateTime.Now - last).TotalSeconds < CooldownSeconds) return;
        _lastTrigger[cameraId] = DateTime.Now;

        if (!_recording.IsRecording(cameraId)) {
            _recording.StartRecording(cam);
            _events.LogInfo("Recording", "MotionTrigger",
                $"Motion-triggered recording started for {cam.Name}",
                $"confidence={confidence:F2}, pre={PreRecordSeconds}s, post={PostRecordSeconds}s");
            _ = DelayedStopAsync(cameraId);
        }
    }

    private async Task DelayedStopAsync(string cameraId) {
        await Task.Delay((PreRecordSeconds + PostRecordSeconds) * 1000);
        if (_disposed) return;
        if (_recording.IsRecording(cameraId)) {
            _recording.StopRecording(cameraId);
        }
    }

    public void Dispose() {
        _disposed = true;
        _motion.MotionDetected -= OnMotionDetected;
    }
}
