namespace HeliVMS.Models;

public class CameraSchedule {
    public string CameraId { get; set; } = "";
    public List<ScheduleRule> Rules { get; set; } = [];
    public List<ScheduleException> Exceptions { get; set; } = [];
}

public class ScheduleRule {
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public TimeSpan StartTime { get; set; } = TimeSpan.FromHours(0);
    public TimeSpan EndTime { get; set; } = TimeSpan.FromHours(24);
    public bool RecordingEnabled { get; set; } = true;
    public bool MotionDetectionEnabled { get; set; }
}

public class ScheduleException {
    public DateTime Date { get; set; }
    public ExceptionOverride Override { get; set; } = ExceptionOverride.StopRecording;
    public string Label { get; set; } = "";
}

public enum ExceptionOverride {
    StopRecording,
    RecordFullDay,
    Ignore
}

public class RecordingScheduleData {
    public List<CameraSchedule> CameraSchedules { get; set; } = [];
}
