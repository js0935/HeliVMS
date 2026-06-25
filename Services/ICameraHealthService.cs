using System.Collections.ObjectModel;
using System.Windows.Media;
using HeliVMS.Models;

namespace HeliVMS.Services;

public interface ICameraHealthService {
    ObservableCollection<CameraHealthItem> HealthItems { get; }
    int OnlineCount { get; }
    int OfflineCount { get; }
    int TotalCount { get; }
    event Action? HealthChanged;
    void StartMonitoring();
    void StopMonitoring();
    void MarkCameraOnline(string cameraId);
    void MarkCameraOffline(string cameraId, string? error = null);
}

public class CameraHealthItem {
    public string CameraId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsOnline { get; set; }
    public int DisconnectCount { get; set; }
    public DateTime? LastConnectedAt { get; set; }
    public DateTime? LastDisconnectedAt { get; set; }
    public string? LastError { get; set; }
    public string StatusText => IsOnline ? "上線" : "離線";
    public TimeSpan? Uptime => IsOnline && LastConnectedAt.HasValue
        ? DateTime.Now - LastConnectedAt.Value : null;
    public string IndicatorColor => IsOnline ? "#FF00C897" : "#FFFF4444";
    public Brush StatusBrush => IsOnline
        ? new SolidColorBrush(Color.FromRgb(0, 200, 151))
        : new SolidColorBrush(Color.FromRgb(255, 68, 68));
}
