using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class RecordingScheduleService : IRecordingScheduleService {
    private readonly string _filePath;
    private RecordingScheduleData _data;

    public RecordingScheduleService() {
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "recording_schedule.json");
        _data = Load();
    }

    public RecordingScheduleData Load() {
        try {
            if (File.Exists(_filePath)) {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<RecordingScheduleData>(json) ?? new RecordingScheduleData();
            }
        } catch {
            Serilog.Log.Warning("[Schedule] Failed to load recording schedule, using defaults");
        }
        return new RecordingScheduleData();
    }

    public void Save(RecordingScheduleData data) {
        _data = data;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public CameraSchedule? GetCameraSchedule(string cameraId) {
        return _data.CameraSchedules.FirstOrDefault(s => s.CameraId == cameraId);
    }

    public bool IsRecordingScheduled(string cameraId) {
        var schedule = GetCameraSchedule(cameraId);
        if (schedule is null || schedule.Rules.Count == 0) return true;
        var now = DateTime.Now;
        var exception = schedule.Exceptions?.FirstOrDefault(e => e.Date.Date == now.Date);
        if (exception is not null) {
            return exception.Override switch {
                ExceptionOverride.RecordFullDay => true,
                ExceptionOverride.StopRecording => false,
                _ => exception.Override == ExceptionOverride.Ignore && CheckRules(schedule.Rules, now.DayOfWeek, now.TimeOfDay, r => r.RecordingEnabled)
            };
        }
        return CheckRules(schedule.Rules, now.DayOfWeek, now.TimeOfDay, r => r.RecordingEnabled);
    }

    public bool IsMotionDetectionScheduled(string cameraId) {
        var schedule = GetCameraSchedule(cameraId);
        if (schedule is null || schedule.Rules.Count == 0) return false;
        var now = DateTime.Now;
        var exception = schedule.Exceptions?.FirstOrDefault(e => e.Date.Date == now.Date);
        if (exception is not null) {
            if (exception.Override == ExceptionOverride.RecordFullDay) return true;
            if (exception.Override == ExceptionOverride.StopRecording) return false;
        }
        return CheckRules(schedule.Rules, now.DayOfWeek, now.TimeOfDay, r => r.MotionDetectionEnabled);
    }

    private static bool CheckRules(List<ScheduleRule> rules, DayOfWeek day, TimeSpan time, Func<ScheduleRule, bool> selector) {
        return rules.Any(r => r.Day == day && r.StartTime <= time && time <= r.EndTime && selector(r));
    }
}
