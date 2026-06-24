using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class LayoutService : ILayoutService {
    private readonly List<CameraLayout> _layouts = [];
    private readonly List<LayoutTab> _tabs = [];
    private readonly string _layoutsPath;
    private readonly string _tabsPath;
    private readonly object _lock = new();

    public LayoutService() {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _layoutsPath = Path.Combine(dir, "layouts.json");
        _tabsPath = Path.Combine(dir, "tabs.json");
        LoadLayouts();
        LoadTabs();
        if (_tabs.Count == 0) {
            _tabs.Add(new LayoutTab { Name = "預設佈局" });
            SaveTabs();
        }
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
            SaveLayouts();
        }
    }

    public void DeleteLayout(string layoutId) {
        lock (_lock) {
            _layouts.RemoveAll(l => l.Id == layoutId);
            SaveLayouts();
        }
    }

    public List<CameraLayout> GetAllLayouts() {
        lock (_lock) return [.. _layouts];
    }

    public CameraLayout? GetLayoutById(string layoutId) {
        lock (_lock) return _layouts.FirstOrDefault(l => l.Id == layoutId);
    }

    public List<LayoutTab> GetAllTabs() {
        lock (_lock) return [.. _tabs];
    }

    public LayoutTab? GetTab(string id) {
        lock (_lock) return _tabs.FirstOrDefault(t => t.Id == id);
    }

    public void SaveTab(LayoutTab tab) {
        lock (_lock) {
            var idx = _tabs.FindIndex(t => t.Id == tab.Id);
            if (idx >= 0)
                _tabs[idx] = tab;
            else
                _tabs.Add(tab);
            SaveTabs();
        }
    }

    public void DeleteTab(string id) {
        lock (_lock) {
            _tabs.RemoveAll(t => t.Id == id);
            SaveTabs();
        }
    }

    public LayoutTab CreateTab(string name) {
        var tab = new LayoutTab { Name = name };
        lock (_lock) {
            _tabs.Add(tab);
            SaveTabs();
        }
        return tab;
    }

    private void LoadLayouts() {
        try {
            if (!File.Exists(_layoutsPath)) return;
            var json = File.ReadAllText(_layoutsPath);
            var list = JsonSerializer.Deserialize<List<CameraLayout>>(json);
            if (list is not null) {
                lock (_lock) {
                    _layouts.Clear();
                    _layouts.AddRange(list);
                }
            }
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Load layouts failed: {Msg}", ex.Message);
        }
    }

    private void SaveLayouts() {
        try {
            var json = JsonSerializer.Serialize(_layouts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_layoutsPath, json);
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Save layouts failed: {Msg}", ex.Message);
        }
    }

    private void LoadTabs() {
        try {
            if (!File.Exists(_tabsPath)) return;
            var json = File.ReadAllText(_tabsPath);
            var list = JsonSerializer.Deserialize<List<LayoutTab>>(json);
            if (list is not null) {
                lock (_lock) {
                    _tabs.Clear();
                    _tabs.AddRange(list);
                }
            }
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Load tabs failed: {Msg}", ex.Message);
        }
    }

    private void SaveTabs() {
        try {
            var json = JsonSerializer.Serialize(_tabs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_tabsPath, json);
        } catch (Exception ex) {
            Serilog.Log.Debug("[Layout] Save tabs failed: {Msg}", ex.Message);
        }
    }
}
