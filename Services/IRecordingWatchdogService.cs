namespace HeliVMS.Services;

public interface IRecordingWatchdogService {
    void Start();
    void Stop();
    int RestartedCount { get; }
    event Action<string>? RecordingRestarted;
}
