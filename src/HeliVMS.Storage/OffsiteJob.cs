namespace HeliVMS.Storage;

/// <summary>異地備援複製工作（offsite_jobs 表；M55）。</summary>
public sealed record OffsiteJob(
    long Id,
    string SourcePath,
    string DestinationPath,
    int IntervalMinutes,
    bool Enabled,
    DateTime? LastRunUtc,
    string? LastResult,
    string? LastError,
    int ConsecutiveFailures,
    DateTime? CreatedUtc);