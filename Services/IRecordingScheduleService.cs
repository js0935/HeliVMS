using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IRecordingScheduleService {
    RecordingScheduleData Load();
    void Save(RecordingScheduleData data);
    CameraSchedule? GetCameraSchedule(string cameraId);
    bool IsRecordingScheduled(string cameraId);
    bool IsMotionDetectionScheduled(string cameraId);
}
