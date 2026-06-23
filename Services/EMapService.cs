using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public class EMapService {
    private static readonly string DataFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeliVMS", "emap.json");

    private readonly ICameraService _cameras;
    private EMapData _data = new();

    public EMapData Data => _data;
    public EMapFloor? CurrentFloor => _data.Floors.Count > 0 ? _data.Floors[_data.ActiveFloorIndex] : null;

    public EMapService(ICameraService cameras) {
        _cameras = cameras;
    }

    public void Load() {
        try {
            if (File.Exists(DataFile)) {
                var json = File.ReadAllText(DataFile);
                var data = JsonSerializer.Deserialize<EMapData>(json);
                if (data is not null) {
                    _data = data;
                    if (_data.Floors.Count == 0)
                        _data.Floors.Add(new EMapFloor());
                    return;
                }
            }
        } catch {
            _data = new EMapData();
        }
        if (_data.Floors.Count == 0)
            _data.Floors.Add(new EMapFloor());
    }

    public void Save() {
        try {
            var dir = Path.GetDirectoryName(DataFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_data);
            File.WriteAllText(DataFile, json);
        } catch (Exception ex) {
            Serilog.Log.Error(ex, "EMapService.Save failed");
        }
    }

    public void SetBackground(string? path) {
        var floor = CurrentFloor;
        if (floor is not null) {
            floor.BackgroundImagePath = path;
            Save();
        }
    }

    public void SetCameraPosition(string cameraId, double x, double y) {
        var floor = CurrentFloor;
        if (floor is null) return;
        var existing = floor.Cameras.Find(c => c.CameraId == cameraId);
        if (existing is not null) {
            existing.X = x;
            existing.Y = y;
        } else {
            floor.Cameras.Add(new EMapCameraPosition { CameraId = cameraId, X = x, Y = y });
        }
        Save();
    }

    public void RemoveCamera(string cameraId) {
        var floor = CurrentFloor;
        if (floor is null) return;
        floor.Cameras.RemoveAll(c => c.CameraId == cameraId);
        Save();
    }

    public void SetViewState(double zoom, double offsetX, double offsetY) {
        var floor = CurrentFloor;
        if (floor is null) return;
        floor.ZoomLevel = zoom;
        floor.OffsetX = offsetX;
        floor.OffsetY = offsetY;
        Save();
    }

    public void AddFloor(string name) {
        _data.Floors.Add(new EMapFloor { Name = name });
        Save();
    }

    public void RemoveFloor(int index) {
        if (_data.Floors.Count <= 1) return;
        if (index < 0 || index >= _data.Floors.Count) return;
        _data.Floors.RemoveAt(index);
        if (_data.ActiveFloorIndex >= _data.Floors.Count)
            _data.ActiveFloorIndex = _data.Floors.Count - 1;
        Save();
    }

    public void RenameFloor(int index, string name) {
        if (index < 0 || index >= _data.Floors.Count) return;
        _data.Floors[index].Name = name;
        Save();
    }

    public void SwitchFloor(int index) {
        if (index < 0 || index >= _data.Floors.Count) return;
        _data.ActiveFloorIndex = index;
    }
}
