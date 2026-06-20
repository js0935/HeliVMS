// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IRecordingService
{
    bool IsRecording(string cameraId);
    bool StartRecording(Camera camera);
    bool StopRecording(string cameraId);
    void StopAll(Action<string>? onProgress = null);
    string? GetRecordingPath(string cameraId);
    List<RecordingSession> GetActiveRecordings();
    event Action<string, bool>? RecordingStatusChanged;
    string GetBasePath();
    void SetBasePath(string path);
    int SegmentLengthSeconds { get; set; }

    bool IsDiskLowSpace();
    bool IsInBackoff(string cameraId);
    void ResetBackoff(string cameraId);

    Task<(int MovedDirs, int UpdatedSegments)> MigrateToDateFirstStructureAsync();
}

public class RecordingSession
{
    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string FilePath { get; set; } = "";
    public long BytesWritten { get; set; }
    public int ProcessId { get; set; }
    public System.Diagnostics.Process? Process { get; set; }
}
