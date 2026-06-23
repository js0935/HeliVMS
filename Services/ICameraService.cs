using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ICameraService {
    List<Camera> GetAllCameras();
    Camera? GetCameraById(string id);
    bool AddCamera(Camera camera);
    bool UpdateCamera(Camera camera);
    bool DeleteCamera(string id);
    void SwapCameraChannels(string id1, string id2);
    void SwapCameraGridOrder(string id1, string id2);
    void ReassignGridOrder(IReadOnlyList<(string id, int order)> orders, bool notify = true);
    void BatchUpdateCameras(IReadOnlyList<Camera> cameras, bool notify = true);
    bool IsIpDuplicate(string ip, string? excludeId = null);
    void MigrateFromLegacy(string legacyJsonPath);
    (int total, int online) GetHealthCounts();
    event Action? CamerasChanged;
}
