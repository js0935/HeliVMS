using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ILayoutService {
    void SaveLayout(string name, int gridSize, List<string?> slotCameraIds);
    void DeleteLayout(string layoutId);
    List<CameraLayout> GetAllLayouts();
    CameraLayout? GetLayoutById(string layoutId);

    List<LayoutTab> GetAllTabs();
    LayoutTab? GetTab(string id);
    void SaveTab(LayoutTab tab);
    void DeleteTab(string id);
    LayoutTab CreateTab(string name);
}
