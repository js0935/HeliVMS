namespace HeliVMS.Shared;

public static class EventTaxonomy
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
    public const string Motion = "motion";
    public const string Sensor = "sensor";
    public const string Shelter = "shelter";
    public const string Tripwire = "tripwire";
    public const string Perimeter = "perimeter";
    public const string SmartMotion = "smart_motion";
    public const string Offline = "offline";
    public const string Online = "online";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All =
        [Motion, Sensor, Tripwire, Perimeter, SmartMotion, Shelter, Offline, Online, Manual, Schedule, System];

    public static string LabelKey(string category) => "EventTaxonomy." + category;

    public static string CategoryOf(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return System;

        var e = eventType.Trim().ToLowerInvariant();

        if (e is Motion or Manual or Schedule or Offline or Online or Sensor or Shelter or Tripwire or Perimeter or SmartMotion or System) return e;
        if (e is "schedule_start" or "schedule_recording") return Schedule;
        if (e.StartsWith("io_")) return Sensor;
        if (e is "tamper" or "shelter" or "signal_shelter") return Shelter;
        if (e is "line_cross" or "tripwire" or "smart_pass_line") return Tripwire;
        if (e is "intrusion" or "perimeter" or "smart_aoi_entry" or "smart_aoi_leave") return Perimeter;
        if (e.StartsWith("ai_") || e.StartsWith("smart_") || e is "analytics" or "audio") return SmartMotion;
        if (e is "disconnected") return Offline;
        if (e is "reconnected") return Online;
        return System;
    }

    public static string ToQccEventType(string category) => category switch
    {
        Motion => "motion", Sensor => "sensor", Tripwire => "tripwire",
        Perimeter => "perimeter", SmartMotion => "smart_motion", Shelter => "shelter",
        Offline or Online => "osc", Manual => "manual", Schedule => "schedule",
        _ => "manual",
    };

    public static int ToCmsTypeCode(string category) => category switch
    {
        Motion or SmartMotion or Tripwire or Perimeter => 4,
        Sensor => 5,
        Offline or Online => 3,
        Manual or Schedule => 2,
        _ => 1,
    };
}