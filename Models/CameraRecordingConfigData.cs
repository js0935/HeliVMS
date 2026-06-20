// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public enum ScheduleMode
{
    None,
    Continuous,
    Motion,
    Alarm,
    Smart,
    Weighted
}

public class DayOfWeekSchedule
{
    public int DayIndex { get; set; }
    public bool[] Hours { get; set; } = new bool[24];
}

public class CameraRecordingConfigData
{
    public bool IsContinuousEnabled { get; set; } = true;
    public bool IsAlarmEnabled { get; set; } = false;
    public bool IsMotionEnabled { get; set; } = false;
    public bool IsSmartEnabled { get; set; } = false;
    public bool IsWeightedEnabled { get; set; } = false;

    public DayOfWeekSchedule[] ContinuousSchedule { get; set; } = CreateDefaultSchedule();
    public DayOfWeekSchedule[] AlarmSchedule { get; set; } = CreateDefaultSchedule();
    public DayOfWeekSchedule[] MotionSchedule { get; set; } = CreateDefaultSchedule();
    public DayOfWeekSchedule[] SmartSchedule { get; set; } = CreateDefaultSchedule();
    public DayOfWeekSchedule[] WeightedSchedule { get; set; } = CreateEmptySchedule();

    private static DayOfWeekSchedule[] CreateEmptySchedule()
    {
        var arr = new DayOfWeekSchedule[7];
        for (int i = 0; i < 7; i++)
        {
            var hours = new bool[24];
            arr[i] = new DayOfWeekSchedule { DayIndex = i, Hours = hours };
        }
        return arr;
    }

    private static DayOfWeekSchedule[] CreateDefaultSchedule()
    {
        var arr = new DayOfWeekSchedule[7];
        for (int i = 0; i < 7; i++)
        {
            var hours = new bool[24];
            for (int h = 0; h < 24; h++) hours[h] = true;
            arr[i] = new DayOfWeekSchedule { DayIndex = i, Hours = hours };
        }
        return arr;
    }

    public int SmartStaticFps { get; set; } = 1;
    public int SmartMotionFps { get; set; } = 30;

    public bool EnableAudio { get; set; } = true;
    public string Quality { get; set; } = "Original";

    public int PreRecordSeconds { get; set; } = 5;
    public int PostRecordSeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    public int SegmentDuration { get; set; } = 3600;

    /// <summary>Gets the current recording mode based on schedule priority: Weighted &gt; Smart &gt; Alarm &gt; Motion &gt; Continuous &gt; None</summary>
    public ScheduleMode GetCurrentMode(DateTime now)
    {
        int dayIndex = (int)now.DayOfWeek;
        int hour = now.Hour;

        bool IsActive(DayOfWeekSchedule[]? schedule)
        {
            return schedule is not null &&
                   dayIndex >= 0 && dayIndex < schedule.Length &&
                   schedule[dayIndex].Hours is not null &&
                   hour >= 0 && hour < schedule[dayIndex].Hours.Length &&
                   schedule[dayIndex].Hours[hour];
        }

        if (IsWeightedEnabled && IsActive(WeightedSchedule))
            return ScheduleMode.Weighted;
        if (IsSmartEnabled && IsActive(SmartSchedule))
            return ScheduleMode.Smart;
        if (IsAlarmEnabled && IsActive(AlarmSchedule))
            return ScheduleMode.Alarm;
        if (IsMotionEnabled && IsActive(MotionSchedule))
            return ScheduleMode.Motion;
        if (IsContinuousEnabled && IsActive(ContinuousSchedule))
            return ScheduleMode.Continuous;
        return ScheduleMode.None;
    }

    [JsonIgnore]
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CameraRecordingConfigData? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CameraRecordingConfigData>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}
