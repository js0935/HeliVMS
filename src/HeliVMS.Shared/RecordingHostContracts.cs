using System;
using System.Collections.Generic;

namespace HeliVMS.Shared;

public sealed record RecordingStallEvent(
    int ChannelId,
    string Message,
    string? Failure,
    DateTime Timestamp);

public sealed record RecordingStateSnapshot(
    IReadOnlyList<int> ActiveChannelIds,
    string? LastError,
    string? Activity,
    IReadOnlyList<RecordingStallEvent> RecentStalls,
    DateTime CapturedAt);

public interface IRecordingState
{
    RecordingStateSnapshot GetSnapshot();
    event EventHandler<RecordingStallEvent>? StallOccurred;
}

public interface IRecordingHost : IRecordingState
{
    void Start();
    void Stop();
    void Reconcile();
}
