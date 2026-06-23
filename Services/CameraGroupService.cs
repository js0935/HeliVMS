using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class CameraGroupService {
    private readonly string _filePath;
    private CameraGroupData _data;

    public IReadOnlyList<CameraGroup> Groups => _data.Groups.AsReadOnly();

    public CameraGroupService() {
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "camera_groups.json");
        _data = Load();
    }

    public CameraGroupData Load() {
        try {
            if (File.Exists(_filePath)) {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<CameraGroupData>(json) ?? new CameraGroupData();
            }
        } catch {
            Serilog.Log.Warning("[Group] Failed to load camera groups");
        }
        return new CameraGroupData();
    }

    public void Save(CameraGroupData data) {
        _data = data;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void AddGroup(string name, string color = "#4CAF50") {
        _data.Groups.Add(new CameraGroup { Name = name, Color = color });
        Save(_data);
    }

    public void DeleteGroup(string groupId) {
        _data.Groups.RemoveAll(g => g.Id == groupId);
        Save(_data);
    }

    public void AddCameraToGroup(string groupId, string cameraId) {
        var group = _data.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is not null && !group.CameraIds.Contains(cameraId)) {
            group.CameraIds.Add(cameraId);
            Save(_data);
        }
    }

    public void RemoveCameraFromGroup(string groupId, string cameraId) {
        var group = _data.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is not null) {
            group.CameraIds.Remove(cameraId);
            Save(_data);
        }
    }

    public string[] GetGroupNamesForCamera(string cameraId) {
        return _data.Groups.Where(g => g.CameraIds.Contains(cameraId)).Select(g => g.Name).ToArray();
    }
}
