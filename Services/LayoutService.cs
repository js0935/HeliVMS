using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class LayoutService : ILayoutService {
    private readonly List<CameraLayout> _layouts = [];
    private readonly string _filePath;
    private readonly object _lock = new();

    public LayoutService() {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "layouts.json");
        LoadFromDisk();
    }

    public void SaveLayout(string name, int gridSize, List<string?> slotCameraIds) {
        lock (_lock) {
            var existing = _layouts.FirstOrDefault(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) {
                existing.GridSize = gridSize;
                existing.Slots = slotCameraIds;
                existing.UpdatedAt = DateTime.Now;
            } else {
                _layouts.Add(new CameraLayout {
                    Name = name,
                    GridSize = gridSize,
                    Slots = slotCameraIds,
                });
            }
            SaveToDisk();
        }
    }

    public void DeleteLayout(string layoutId) {
        lock (_lock) {
            _layouts.RemoveAll(l => l.Id == layoutId);
            SaveToDisk();
        }
    }

    public List<CameraLayout> GetAllLayouts() {
        lock (_lock) return [.. _layouts];
    }

    public CameraLayout? GetLayoutById(string layoutId) {
        lock (_lock) return _layouts.FirstOrDefault(l => l.Id == layoutId);
    }

    private void LoadFromDisk() {
        try {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<CameraLayout>>(json);
            if (list is not null) {
                lock (_lock) {
                    _layouts.Clear();
                    _layouts.AddRange(list);
                }
            }
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Load failed: {Msg}", ex.Message);
        }
    }

    private void SaveToDisk() {
        try {
            var json = JsonSerializer.Serialize(_layouts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Save failed: {Msg}", ex.Message);
        }
    }
}
