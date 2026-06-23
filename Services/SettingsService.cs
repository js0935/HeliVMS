using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public class SettingsService : ISettingsService {
    private static readonly string _dataPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public SettingsService() {
        LoadFromDisk();
    }

    public void Save() {
        SaveToDisk();
    }

    public void Reload() {
        LoadFromDisk();
    }

    private void LoadFromDisk() {
        try {
            if (!File.Exists(_dataPath)) { return; }
            var json = File.ReadAllText(_dataPath, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is not null) { _settings = loaded; }
        } catch {
            _settings = new();
        }
    }

    private void SaveToDisk() {
        try {
            var dir = Path.GetDirectoryName(_dataPath);
            if (dir is not null && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(_dataPath, json, System.Text.Encoding.UTF8);
        } catch { }
    }
}
