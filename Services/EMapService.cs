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

    public EMapService(ICameraService cameras) {
        _cameras = cameras;
    }

    public void Load() {
        try {
            if (File.Exists(DataFile)) {
                var json = File.ReadAllText(DataFile);
                _data = JsonSerializer.Deserialize<EMapData>(json) ?? new EMapData();
            }
        } catch {
            _data = new EMapData();
        }
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
        _data.BackgroundImagePath = path;
        Save();
    }

    public void SetCameraPosition(string cameraId, double x, double y) {
        var existing = _data.Cameras.Find(c => c.CameraId == cameraId);
        if (existing is not null) {
            existing.X = x;
            existing.Y = y;
        } else {
            _data.Cameras.Add(new EMapCameraPosition { CameraId = cameraId, X = x, Y = y });
        }
        Save();
    }

    public void RemoveCamera(string cameraId) {
        _data.Cameras.RemoveAll(c => c.CameraId == cameraId);
        Save();
    }

    public void SetViewState(double zoom, double offsetX, double offsetY) {
        _data.ZoomLevel = zoom;
        _data.OffsetX = offsetX;
        _data.OffsetY = offsetY;
        Save();
    }
}
