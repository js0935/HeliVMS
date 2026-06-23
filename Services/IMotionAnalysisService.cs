namespace HeliVMS.Services;

public interface IMotionAnalysisService {
    event Action<string, double>? MotionDetected;
    void StartMonitoring();
    void StopMonitoring();
}
