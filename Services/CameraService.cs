// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.IO;
using System.Text.Json;
using HeliVMS.Helpers;
using HeliVMS.Models;
using Serilog;

namespace HeliVMS.Services;

public class CameraService : ICameraService
{
    private static readonly string _dataPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "cameras.json");

    private List<Camera> _cameras = new();
    private readonly object _lock = new();
    private readonly IEventService _eventLog;
    private readonly ILicenseService _licenseService;

    public event Action? CamerasChanged;

    public CameraService(IEventService eventLog, ILicenseService licenseService)
    {
        _eventLog = eventLog;
        _licenseService = licenseService;
        LoadFromDisk();
    }

    public List<Camera> GetAllCameras()
    {
        lock (_lock)
        {
            var result = new List<Camera>(_cameras.Count);
            for (int i = 0; i < _cameras.Count; i++)
            {
                result.Add(CloneCamera(_cameras[i]));
            }
            return result;
        }
    }

    public Camera? GetCameraById(string id)
    {
        lock (_lock)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                if (_cameras[i].Id == id) { return _cameras[i]; }
            }
            return null;
        }
    }

    public int GetLicenseMaxCameras()
    {
        return _licenseService.GetMaxCameras();
    }

    public bool IsIpDuplicate(string ip, string? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(ip)) { return false; }
        lock (_lock)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                var c = _cameras[i];
                if (c.Id != excludeId && string.Equals(c.IpAddress, ip, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public bool AddCamera(Camera camera)
    {
        lock (_lock)
        {
            var maxCameras = GetLicenseMaxCameras();
            if (_cameras.Count >= maxCameras)
            {
                return false;
            }

            if (IsIpDuplicate(camera.IpAddress!))
            {
                return false;
            }

            if (string.IsNullOrEmpty(camera.Id))
            {
                camera.Id = Guid.NewGuid().ToString();
            }
            camera.CreatedAt = DateTime.Now;
            camera.UpdatedAt = DateTime.Now;
            _cameras.Add(camera);
            SaveToDisk();
        }
        CamerasChanged?.Invoke();
        _eventLog.LogInfo(EventCategories.Create, "CameraService", $"新增攝影機 {camera.Name}", $"IP: {camera.IpAddress}");
        return true;
    }

    public bool UpdateCamera(Camera camera)
    {
        lock (_lock)
        {
            var idx = _cameras.FindIndex(c => c.Id == camera.Id);
            if (idx < 0) { return false; }

            if (IsIpDuplicate(camera.IpAddress!, camera.Id))
            {
                return false;
            }

            camera.UpdatedAt = DateTime.Now;
            _cameras[idx] = camera;
            SaveToDisk();
        }
        CamerasChanged?.Invoke();
        _eventLog.LogInfo(EventCategories.Update, "CameraService", $"更新攝影機 {camera.Name}", $"IP: {camera.IpAddress}");
        return true;
    }

    public void BatchUpdateCameras(IReadOnlyList<Camera> cameras, bool notify = true)
    {
        if (cameras.Count == 0) { return; }
        lock (_lock)
        {
            for (int ci = 0; ci < cameras.Count; ci++)
            {
                var camera = cameras[ci];
                int idx = _cameras.FindIndex(c => c.Id == camera.Id);
                if (idx < 0) { continue; }
                camera.UpdatedAt = DateTime.Now;
                _cameras[idx] = camera;
            }
            SaveToDisk();
        }
        if (notify) { CamerasChanged?.Invoke(); }
    }

    public bool DeleteCamera(string id)
    {
        string? name = null;
        bool removed = false;
        lock (_lock)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                if (_cameras[i].Id != id) { continue; }
                name = _cameras[i].Name;
                _cameras.RemoveAt(i);
                removed = true;
                SaveToDisk();
                break;
            }
        }
        CamerasChanged?.Invoke();
        if (removed)
        {
            _eventLog.LogInfo(EventCategories.Delete, "CameraService", $"刪除攝影機 {name ?? id}", $"ID: {id}");
        }
        return removed;
    }

    public void SwapCameraChannels(string id1, string id2)
    {
        Camera? cam1 = null, cam2 = null;
        lock (_lock)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                var c = _cameras[i];
                if (c.Id == id1) { cam1 = c; }
                if (c.Id == id2) { cam2 = c; }
                if (cam1 is not null && cam2 is not null) { break; }
            }
            if (cam1 is null || cam2 is null) { return; }

            (cam1.ChannelNumber, cam2.ChannelNumber) = (cam2.ChannelNumber, cam1.ChannelNumber);
            cam1.UpdatedAt = DateTime.Now;
            cam2.UpdatedAt = DateTime.Now;
            SaveToDisk();
        }
        CamerasChanged?.Invoke();
        _eventLog.LogInfo(EventCategories.Update, "CameraService",
            $"交換攝影機頻道: {cam1.Name} <-> {cam2.Name}");
    }

    public void ReassignGridOrder(IReadOnlyList<(string id, int order)> orders, bool notify = true)
    {
        lock (_lock)
        {
            var lookup = new Dictionary<string, Camera>(_cameras.Count);
            for (int ci = 0; ci < _cameras.Count; ci++)
                lookup[_cameras[ci].Id] = _cameras[ci];
            foreach (var (id, order) in orders)
            {
                if (lookup.TryGetValue(id, out var cam))
                {
                    cam.GridOrder = order;
                    cam.UpdatedAt = DateTime.Now;
                }
            }
            SaveToDisk();
        }
        if (notify)
        {
            CamerasChanged?.Invoke();
        }
        _eventLog.LogInfo(EventCategories.Update, "CameraService", $"批次更新 {orders.Count} 台攝影機順序");
    }

    public void SwapCameraGridOrder(string id1, string id2)
    {
        Camera? cam1 = null, cam2 = null;
        lock (_lock)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                var c = _cameras[i];
                if (c.Id == id1) { cam1 = c; }
                if (c.Id == id2) { cam2 = c; }
                if (cam1 is not null && cam2 is not null) { break; }
            }
            if (cam1 is null || cam2 is null) { return; }
            if (cam1 == cam2) { return; }

            if (cam1.GridOrder <= 0 && cam2.GridOrder <= 0)
            {
                cam1.GridOrder = cam1.ChannelNumber ?? (int)(cam1.Id?.GetHashCode() ?? 0);
                cam2.GridOrder = cam2.ChannelNumber ?? (int)(cam2.Id?.GetHashCode() ?? 0);
            }
            else if (cam1.GridOrder == cam2.GridOrder)
            {
                // 當兩者 GridOrder 相同（舊資料殘留導致），
                // 從 ChannelNumber 重新指定，確保 swap 後產生不同值
                cam1.GridOrder = cam1.ChannelNumber ?? (int)(cam1.Id?.GetHashCode() ?? 0);
                cam2.GridOrder = cam2.ChannelNumber ?? (int)(cam2.Id?.GetHashCode() ?? 0);
            }

            (cam1.GridOrder, cam2.GridOrder) = (cam2.GridOrder, cam1.GridOrder);
            cam1.UpdatedAt = DateTime.Now;
            cam2.UpdatedAt = DateTime.Now;
            SaveToDisk();
        }
        CamerasChanged?.Invoke();
        if (cam1 is not null && cam2 is not null)
        {
            _eventLog.LogInfo(EventCategories.Update, "CameraService",
                $"交換攝影機順序: {cam1.Name} <-> {cam2.Name}");
        }
    }

    public void MigrateFromLegacy(string legacyJsonPath)
    {
        try
        {
            if (!File.Exists(legacyJsonPath)) { return; }
            var json = File.ReadAllText(legacyJsonPath, System.Text.Encoding.UTF8);
            var legacyCameras = JsonSerializer.Deserialize<List<Camera>>(json);
            if (legacyCameras is null || legacyCameras.Count == 0) { return; }

            lock (_lock)
            {
                _cameras = legacyCameras;
                SaveToDisk();
            }
            CamerasChanged?.Invoke();
            _eventLog.LogInfo(EventCategories.Operation, "CameraService", $"從舊專案匯入 {legacyCameras.Count} 台攝影機", $"來源: {legacyJsonPath}");
        }
        catch (Exception ex)
        {
            _eventLog.LogError(EventCategories.Operation, "CameraService", "匯入舊專案攝影機失敗", ex.Message);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_dataPath)) { return; }
            var json = File.ReadAllText(_dataPath, System.Text.Encoding.UTF8);
            var raw = JsonSerializer.Deserialize<List<Camera>>(json) ?? new();
            _cameras = new List<Camera>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var c = raw[i];
                if (!string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Name))
                {
                    _cameras.Add(c);
                }
            }

            bool fixedAny = false;
            foreach (var cam in _cameras)
            {
                if (string.IsNullOrEmpty(cam.RtspUrl)) { continue; }
                var mfr = cam.Manufacturer?.ToLowerInvariant() ?? "";
                if (!mfr.Contains("vivotek")) { continue; }

                if (Uri.TryCreate(cam.RtspUrl, UriKind.Absolute, out var uri))
                {
                    var path = uri.AbsolutePath.TrimEnd('/');
                    if (path == "/live")
                    {
                        cam.RtspUrl = cam.RtspUrl.Replace("/live", "/live1s1.sdp");
                        fixedAny = true;
                    }
                }

                if (!string.IsNullOrEmpty(cam.RtspUrlSub) && Uri.TryCreate(cam.RtspUrlSub, UriKind.Absolute, out var subUri))
                {
                    var subPath = subUri.AbsolutePath.TrimEnd('/');
                    if (subPath == "/live" || subPath == "/live2")
                    {
                        cam.RtspUrlSub = cam.RtspUrlSub.Replace(subPath, "/live1s2.sdp");
                        fixedAny = true;
                    }
                }
            }

            if (fixedAny)
            {
                SaveToDisk();
            }

            if (_cameras.Count != raw.Count)
            {
                SaveToDisk();
            }
        }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.CatCamera, "LoadFromDisk", "Failed to load cameras, starting fresh", ex); _cameras = new(); }
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dataPath);
            if (dir is not null && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var valid = new List<Camera>(_cameras.Count);
            for (int i = 0; i < _cameras.Count; i++)
            {
                if (!string.IsNullOrEmpty(_cameras[i].Id))
                {
                    valid.Add(_cameras[i]);
                }
            }
            var json = JsonSerializer.Serialize(valid, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.CatCamera, "SaveToDisk", "Failed to save cameras", ex); }
    }

    private static Camera CloneCamera(Camera c) => new()
    {
        Id = c.Id,
        CameraId = c.CameraId,
        Name = c.Name,
        IpAddress = c.IpAddress,
        Port = c.Port,
        OnvifPort = c.OnvifPort,
        Username = c.Username,
        Password = c.Password,
        Manufacturer = c.Manufacturer,
        Model = c.Model,
        SerialNumber = c.SerialNumber,
        RtspUrl = c.RtspUrl,
        RtspUrlSub = c.RtspUrlSub,
        OnvifResolvedUrl = c.OnvifResolvedUrl,
        OnvifResolvedUrlSub = c.OnvifResolvedUrlSub,
        IsEnabled = c.IsEnabled,
        IsVisible = c.IsVisible,
        HasPTZ = c.HasPTZ,
        IsRecordingEnabled = c.IsRecordingEnabled,
        IsAlarmEnabled = c.IsAlarmEnabled,
        IsMotionDetectionEnabled = c.IsMotionDetectionEnabled,
        RecordingConfigJson = c.RecordingConfigJson,
        ChannelNumber = c.ChannelNumber,
        GridOrder = c.GridOrder,
        Group = c.Group,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };
}
