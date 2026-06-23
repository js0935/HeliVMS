// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Controls;

public partial class ChannelManagementPage : UserControl {
    private const int TotalChannels = 64;
    private readonly ICameraService _cameraService;
    private readonly IEventService _eventLog;

    /// <summary>Snapshot of channels shared with RecordingSettingsPage / PlaybackView / CameraGrid</summary>
    /// <remarks>Updated after each Apply; use caution when reading from other threads; returns a frozen list</remarks>
    public static IReadOnlyList<ChannelItem>? CurrentChannels { get; private set; }

    private List<ChannelItem> _channelItems = [];
    private List<Camera> _originalCameras = [];

    public ChannelManagementPage() {
        InitializeComponent();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        Loaded += (_, _) => LoadChannels();
    }

    internal void LoadChannels() {
        _originalCameras = _cameraService.GetAllCameras();

        _channelItems = new List<ChannelItem>(TotalChannels);
        for (var ch = 1; ch <= TotalChannels; ch++) {
            Camera? cam = null;
            foreach (var c in _originalCameras) {
                if (c.ChannelNumber == ch) {
                    cam = c;
                    break;
                }
            }
            _channelItems.Add(new ChannelItem {
                ChannelNumber = ch,
                DisplayName = cam?.Name ?? $"CH{ch}",
                IpAddress = cam?.IpAddress ?? "",
                Manufacturer = cam?.Manufacturer ?? "",
                Model = cam?.Model ?? "",
                Camera = cam
            });
        }

        ChannelGrid.ItemsSource = _channelItems;
        UpdateChannelCount();
        CurrentChannels = [.. _channelItems];

        RefreshCameraList();
    }

    private void RefreshCameraList() {
        _originalCameras = _cameraService.GetAllCameras();
        var cameraItems = new List<CameraListItem>(_originalCameras.Count);
        foreach (var c in _originalCameras) {
            cameraItems.Add(new CameraListItem(c));
        }
        CameraListBox.ItemsSource = cameraItems;
        var assignedCamCount = 0;
        for (var i = 0; i < cameraItems.Count; i++) {
            if (cameraItems[i].Camera?.ChannelNumber is not null) {
                assignedCamCount++;
            }
        }
        CameraCountText.Text = $"共 {cameraItems.Count} 台攝影機，{assignedCamCount} 台已指派頻道";
    }

    private void UpdateChannelCount() {
        var assigned = 0;
        for (var i = 0; i < _channelItems.Count; i++) {
            if (_channelItems[i].Camera is not null) {
                assigned++;
            }
        }
        ChannelCountText.Text = $"已指派 {assigned} / {TotalChannels} 頻道";
    }

    private void CameraListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        UpdateAssignButtonState();
    }

    private void ChannelGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        UpdateAssignButtonState();
    }

    private void UpdateAssignButtonState() {
        var hasChannel = ChannelGrid.SelectedItem is ChannelItem;
        var hasCamera = CameraListBox.SelectedItem is CameraListItem;
        AssignButton.IsEnabled = hasChannel && hasCamera;

        if (ChannelGrid.SelectedItem is ChannelItem ch) {
            SelectedChannelInfo.Text = $"選擇頻道：CH{ch.ChannelNumber}";
            if (!string.IsNullOrEmpty(ch.IpAddress)) {
                SelectedChannelInfo.Text += $"，IP {ch.IpAddress}";
            }
            ClearButton.IsEnabled = !string.IsNullOrEmpty(ch.IpAddress);
        } else {
            SelectedChannelInfo.Text = "未選擇或未指派";
            ClearButton.IsEnabled = false;
        }
    }

    private void AssignButton_Click(object sender, RoutedEventArgs e) {
        var cameraItem = CameraListBox.SelectedItem as CameraListItem;

        if (ChannelGrid.SelectedItem is not ChannelItem channel || cameraItem?.Camera is null) { return; }

        var camera = cameraItem.Camera;

        ChannelItem? conflict = null;
        foreach (var c in _channelItems) {
            if (c.ChannelNumber != channel.ChannelNumber &&
                c.IpAddress == camera.IpAddress &&
                !string.IsNullOrEmpty(c.IpAddress)) {
                conflict = c;
                break;
            }
        }

        if (conflict is not null) {
            var result = MessageBox.Show(
$"IP {camera.IpAddress} 已指派給頻道 {conflict.ChannelNumber}（{conflict.DisplayName}），" +
$"是否要將 IP 重新指派給頻道 {channel.ChannelNumber}？",
                "IP 衝突", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { return; }

            conflict.IpAddress = "";
            conflict.Manufacturer = "";
            conflict.Model = "";
            conflict.DisplayName = $"CH{conflict.ChannelNumber}";
            conflict.Camera = null;
        }

        channel.IpAddress = camera.IpAddress ?? "";
        channel.Manufacturer = camera.Manufacturer ?? "";
        channel.Model = camera.Model ?? "";
        channel.Camera = camera;
        camera.ChannelNumber = channel.ChannelNumber;

        RefreshChannelGrid();
        RefreshCameraList();
        UpdateAssignButtonState();
    }

    private void ChannelGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) {
        if (e.EditingElement is TextBox tb && e.Row.Item is ChannelItem channel) {
            channel.DisplayName = tb.Text;
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e) {
        try {
            ChannelGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ChannelGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var changedCount = 0;

            foreach (var item in _channelItems) {
                var camera = item.Camera;

                if (camera is not null) {
                    camera.ChannelNumber = item.ChannelNumber;
                    camera.Name = item.DisplayName;
                    _cameraService.UpdateCamera(camera);
                    changedCount++;
                } else if (!string.IsNullOrEmpty(item.IpAddress)) {
                    Camera? dup = null;
                    foreach (var d in _originalCameras) {
                        if (d.IpAddress == item.IpAddress) {
                            dup = d;
                            break;
                        }
                    }
                    if (dup is not null) {
                        dup.ChannelNumber = item.ChannelNumber;
                        _cameraService.UpdateCamera(dup);
                        item.Camera = dup;
                        changedCount++;
                        continue;
                    }

                    var newCam = new Camera {
                        Id = Guid.NewGuid().ToString(),
                        CameraId = Guid.NewGuid().ToString(),
                        Name = item.DisplayName,
                        IpAddress = item.IpAddress,
                        ChannelNumber = item.ChannelNumber,
                        Manufacturer = item.Manufacturer,
                        Model = item.Model,
                        IsEnabled = true,
                        IsVisible = true,
                        Port = 554,
                        OnvifPort = 80,
                        RtspUrl = $"rtsp://{item.IpAddress}:554/stream1"
                    };

                    if (_cameraService.AddCamera(newCam)) {
                        item.Camera = newCam;
                        _originalCameras.Add(newCam);
                        changedCount++;
                    }
                }
            }

            foreach (var item in _channelItems) {
                if (item.Camera is null || !string.IsNullOrEmpty(item.IpAddress)) { continue; }
                var cam = item.Camera!;
                cam.ChannelNumber = null;
                _cameraService.UpdateCamera(cam);
                item.Camera = null;
                changedCount++;
            }

            UpdateChannelCount();
            CurrentChannels = [.. _channelItems];

            _eventLog.LogInfo("ChannelManagement", "ChannelManagementPage",
                $"頻道配置已套用成功", $"共 {changedCount} 台已變更");

            RefreshCameraList();

            MessageBox.Show($"已成功套用變更，共 {changedCount} 台攝影機",
                "已完成", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            MessageBox.Show($"頻道配置已套用時發生例外狀況：{ex.Message}",
                "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshChannelGrid() {
        ChannelGrid.ItemsSource = null;
        ChannelGrid.ItemsSource = _channelItems;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) {
        if (ChannelGrid.SelectedItem is not ChannelItem channel) { return; }
        if (string.IsNullOrEmpty(channel.IpAddress)) { return; }

        var cam = channel.Camera;
        if (cam is not null) {
            cam.ChannelNumber = null;
            _cameraService.UpdateCamera(cam);
        }

        channel.IpAddress = "";
        channel.Manufacturer = "";
        channel.Model = "";
        channel.DisplayName = $"CH{channel.ChannelNumber}";
        channel.Camera = null;

        RefreshChannelGrid();
        RefreshCameraList();
        UpdateAssignButtonState();
    }

    private void AutoAssignBtn_Click(object sender, RoutedEventArgs e) {
        var unassigned = new List<Camera>(_originalCameras.Count);
        foreach (var c in _originalCameras) {
            if (!c.ChannelNumber.HasValue) {
                unassigned.Add(c);
            }
        }
        unassigned.Sort((a, b) => {
            var aIs192 = a.IpAddress?.StartsWith("192.") == true;
            var bIs192 = b.IpAddress?.StartsWith("192.") == true;
            if (aIs192 != bIs192)
                return aIs192 ? -1 : 1;
            return IpToInt(a.IpAddress).CompareTo(IpToInt(b.IpAddress));
        });

        if (unassigned.Count == 0) {
            MessageBox.Show("No unassigned cameras found", "Channel Management",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var emptySlots = new List<ChannelItem>(_channelItems.Count);
        foreach (var c in _channelItems) {
            if (c.Camera is null) {
                emptySlots.Add(c);
            }
        }

        if (emptySlots.Count == 0) {
            MessageBox.Show("No empty channel slots available", "Channel Management",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"共 {unassigned.Count} 台未指派攝影機，將指派到 {emptySlots.Count} 個空頻道。\n" +
            "指派完成後攝影機將按 IP 排序自動對應？",
            "自動指派？", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { return; }

        var assigned = 0;
        var batch = new List<Camera>(unassigned.Count);
        foreach (var camera in unassigned) {
            if (assigned >= emptySlots.Count) { break; }

            var slot = emptySlots[assigned];
            slot.IpAddress = camera.IpAddress ?? "";
            slot.Manufacturer = camera.Manufacturer ?? "";
            slot.Model = camera.Model ?? "";
            slot.DisplayName = $"CH{slot.ChannelNumber}";
            slot.Camera = camera;
            camera.ChannelNumber = slot.ChannelNumber;
            batch.Add(camera);
            assigned++;
        }

        if (batch.Count > 0) {
            _cameraService.BatchUpdateCameras(batch);
        }

        RefreshChannelGrid();
        RefreshCameraList();
        UpdateAssignButtonState();

        _eventLog.LogInfo("ChannelManagement", "ChannelManagementPage",
            $"自動指派完成", $"共 {assigned} 台攝影機已自動綁定");

        MessageBox.Show($"已完成，共 {assigned} 台攝影機已指派至頻道" +
            (unassigned.Count > assigned
                ? $"，另有 {unassigned.Count - assigned} 台因頻道不足未指派"
                : ""),
            "已完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearAllBtn_Click(object sender, RoutedEventArgs e) {
        var assigned = 0;
        for (var ci = 0; ci < _channelItems.Count; ci++) {
            if (_channelItems[ci].Camera is not null) { assigned++; }
        }
        if (assigned == 0) {
            MessageBox.Show("目前沒有任何頻道有指派 IP", "清除全部 IP",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"確定要清除全部 {assigned} 個頻道的 IP 指派？\n\n" +
            "攝影機資料不會被刪除，僅取消頻道綁定。",
            "清除全部 IP", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { return; }

        var cleared = 0;
        var batch = new List<Camera>(_channelItems.Count);
        for (var ci = 0; ci < _channelItems.Count; ci++) {
            var channel = _channelItems[ci];
            if (channel.Camera is null) { continue; }

            var cam = channel.Camera;
            cam.ChannelNumber = null;
            batch.Add(cam);

            channel.IpAddress = "";
            channel.Manufacturer = "";
            channel.Model = "";
            channel.DisplayName = $"CH{channel.ChannelNumber}";
            channel.Camera = null;
            cleared++;
        }

        if (batch.Count > 0) {
            _cameraService.BatchUpdateCameras(batch);
        }

        RefreshChannelGrid();
        RefreshCameraList();
        UpdateAssignButtonState();
        UpdateChannelCount();

        _eventLog.LogInfo("ChannelManagement", "ChannelManagementPage",
            $"清除全部 IP", $"共 {cleared} 個頻道的 IP 指派已清除");

        MessageBox.Show($"已清除 {cleared} 個頻道的 IP 指派", "清除全部 IP",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static long IpToInt(string? ip) {
        if (string.IsNullOrEmpty(ip)) { return 0; }
        if (IPAddress.TryParse(ip, out var addr)) {
            var b = addr.GetAddressBytes();
            if (b.Length == 4) {
                return (long)b[0] << 24 | (long)b[1] << 16 | (long)b[2] << 8 | b[3];
            }
        }
        return 0;
    }
}

public class ChannelItem : INotifyPropertyChanged {
    public int ChannelNumber { get; set; }

    private string _displayName = string.Empty;
    public string DisplayName {
        get => _displayName;
        set {
            if (_displayName != value) {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    public string IpAddress { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public Camera? Camera { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class CameraListItem(Camera camera) {
    public Camera? Camera { get; } = camera;
    public string Name { get; } = camera.Name;
    public string IpAddress { get; } = camera.IpAddress ?? "";

    public string DisplayInfo {
        get {
            if (Camera is null) { return ""; }
            return Camera.ChannelNumber.HasValue
                ? $"{Camera.ChannelNumber}"
                : "";
        }
    }

    public Visibility ChannelVisibility =>
        Camera?.ChannelNumber.HasValue == true ? Visibility.Visible : Visibility.Collapsed;
}
