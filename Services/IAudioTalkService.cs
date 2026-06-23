namespace HeliVMS.Services;

public interface IAudioTalkService {
    bool IsTalking { get; }
    bool StartTalking(string cameraId);
    void StopTalking();
    event Action<float>? AudioLevelChanged;
}
