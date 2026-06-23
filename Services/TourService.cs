using System.IO;
using System.Text.Json;
using HeliVMS.Controls;

namespace HeliVMS.Services;

public sealed class TourService {
    private readonly string _filePath;
    private Dictionary<string, List<PtzTour>> _cameraTours = [];

    public TourService() {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        _filePath = Path.Combine(dir, "ptz_tours.json");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Load();
    }

    public List<PtzTour> GetTours(string cameraId) {
        return _cameraTours.TryGetValue(cameraId, out var tours) ? tours : [];
    }

    public void SaveTour(string cameraId, PtzTour tour) {
        if (!_cameraTours.ContainsKey(cameraId))
            _cameraTours[cameraId] = [];
        var existing = _cameraTours[cameraId].FindIndex(t => t.Name == tour.Name);
        if (existing >= 0)
            _cameraTours[cameraId][existing] = tour;
        else
            _cameraTours[cameraId].Add(tour);
        Persist();
    }

    public void DeleteTour(string cameraId, string tourName) {
        if (_cameraTours.TryGetValue(cameraId, out var tours)) {
            tours.RemoveAll(t => t.Name == tourName);
            Persist();
        }
    }

    public void LoadToursForCamera(string cameraId, List<PtzTour> targetList) {
        targetList.Clear();
        var stored = GetTours(cameraId);
        targetList.AddRange(stored);
    }

    private void Load() {
        try {
            if (File.Exists(_filePath)) {
                var json = File.ReadAllText(_filePath);
                _cameraTours = JsonSerializer.Deserialize<Dictionary<string, List<PtzTour>>>(json) ?? [];
            }
        } catch {
            _cameraTours = [];
        }
    }

    private void Persist() {
        try {
            var json = JsonSerializer.Serialize(_cameraTours, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        } catch { }
    }
}
