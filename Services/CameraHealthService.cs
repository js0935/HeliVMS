// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using Serilog;

namespace HeliVMS.Services;

public sealed class CameraHealthService : ICameraHealthService, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IEventService _eventLog;
    private readonly ConcurrentDictionary<string, CameraHealthItem> _items = new();
    private Timer? _timer;

    public ObservableCollection<CameraHealthItem> HealthItems { get; } = new();

    public int OnlineCount { get; private set; }
    public int OfflineCount { get; private set; }
    public int TotalCount => HealthItems.Count;

    public event Action? HealthChanged;

    public CameraHealthService(ICameraService cameraService, IEventService eventLog)
    {
        _cameraService = cameraService;
        _eventLog = eventLog;
    }

    public void StartMonitoring()
    {
        DispatchSyncCameras();
        _timer?.Dispose();
        _timer = new Timer(_timerCallback, this, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    private static readonly TimerCallback _timerCallback = static state =>
    {
        var self = (CameraHealthService)state!;
        try { self.DispatchSyncCameras(); }
        catch (Exception ex) { Log.Debug("[HeliVMS] CameraHealthService sync error: {Msg}", ex.Message); }
    };

    public void StopMonitoring()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void MarkCameraOnline(string cameraId)
    {
        if (_items.TryGetValue(cameraId, out var item) && !item.IsOnline)
        {
            item.IsOnline = true;
            item.LastConnectedAt = DateTime.Now;
            item.LastError = null;
            _eventLog.LogInfo("Camera", "CameraHealth", $"{item.Name} 已恢復連線");
            NotifyChanged();
        }
    }

    public void MarkCameraOffline(string cameraId, string? error = null)
    {
        if (_items.TryGetValue(cameraId, out var item) && item.IsOnline)
        {
            item.IsOnline = false;
            item.DisconnectCount++;
            item.LastDisconnectedAt = DateTime.Now;
            item.LastError = error;
            _eventLog.LogWarning("Camera", "CameraHealth", $"{item.Name}: connection lost");
            NotifyChanged();
        }
    }

    private void DispatchSyncCameras()
    {
        if (Application.Current?.Dispatcher is null)
        {
            SyncCameras();
            return;
        }
        if (Application.Current.Dispatcher.CheckAccess())
        {
            SyncCameras();
        }
        else
        {
            Application.Current.Dispatcher.BeginInvoke(SyncCameras);
        }
    }

    private void SyncCameras()
    {
        var cameras = _cameraService.GetAllCameras();

        // Remove cameras that no longer exist
        foreach (var id in _items.Keys)
        {
            bool found = false;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i].Id == id) { found = true; break; }
            }
            if (!found && _items.TryRemove(id, out var item))
            {
                _ = HealthItems.Remove(item);
            }
        }

        int online = 0, offline = 0;
        for (int i = 0; i < cameras.Count; i++)
        {
            var cam = cameras[i];
            if (!_items.TryGetValue(cam.Id, out var item))
            {
                item = new CameraHealthItem
                {
                    CameraId = cam.Id,
                    Name = cam.Name,
                    IsOnline = cam.IsConnected,
                    DisconnectCount = cam.DisconnectCount,
                    LastConnectedAt = cam.LastConnectedAt,
                    LastDisconnectedAt = cam.LastDisconnectedAt
                };
                _items[cam.Id] = item;
                HealthItems.Add(item);
            }
            else
            {
                item.Name = cam.Name;
            }
            if (item.IsOnline) { online++; } else { offline++; }
        }

        OnlineCount = online;
        OfflineCount = offline;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        HealthChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
