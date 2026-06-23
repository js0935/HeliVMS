using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ILayoutService {
    void SaveLayout(string name, int gridSize, List<string?> slotCameraIds);
    void DeleteLayout(string layoutId);
    List<CameraLayout> GetAllLayouts();
    CameraLayout? GetLayoutById(string layoutId);
}
