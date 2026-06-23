// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Serilog;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HeliVMS.Dialog;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Views;

public partial class DeviceManagementView : UserControl {
    private readonly ICameraService _cameraService;
    private readonly IOnvifService _onvifService;
    private readonly ILicenseService _licenseService;
    private readonly IEventService _eventLog;

    public DeviceManagementView() {
        InitializeComponent();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _onvifService = App.Services.GetRequiredService<IOnvifService>();
        _licenseService = App.Services.GetRequiredService<ILicenseService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();

        Loaded += (_, _) => {
            _cameraService.CamerasChanged += OnDataChanged;
            _ = RefreshAllAsync();
        };
        Unloaded += (_, _) => {
            _cameraService.CamerasChanged -= OnDataChanged;
        };
    }

    private int _currentTab;
    private List<Camera>? _camerasWithStatus;

    private void TabButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) {
            ShowTab(index);
        }
    }

    private void ShowTab(int index) {
        if (index == _currentTab) { return; }
        _currentTab = index;

        TabDevice.BorderBrush = index == 0
            ? TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue
            : Brushes.Transparent;
        TabDevice.Foreground = index == 0
            ? TryFindResource("TextBrush") as Brush ?? Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        TabRecording.BorderBrush = index == 1
            ? TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue
            : Brushes.Transparent;
        TabRecording.Foreground = index == 1
            ? TryFindResource("TextBrush") as Brush ?? Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        TabStorage.BorderBrush = index == 2
            ? TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue
            : Brushes.Transparent;
        TabStorage.Foreground = index == 2
            ? TryFindResource("TextBrush") as Brush ?? Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        TabChannel.BorderBrush = index == 3
            ? TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue
            : Brushes.Transparent;
        TabChannel.Foreground = index == 3
            ? TryFindResource("TextBrush") as Brush ?? Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        DeviceSettingsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingSettingsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        StorageManagerPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ChannelManagementPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 1) {
            RecordingSettingsPanel.RefreshCameraList();
        } else if (index == 3) {
            ChannelManagementPanel.LoadChannels();
        }
    }

    private void OnDataChanged() {
        _ = RefreshAllAsync();
    }

    private async Task RefreshAllAsync() {
        try {
            var cameras = _cameraService.GetAllCameras();
            _camerasWithStatus = cameras;
            await Dispatcher.InvokeAsync(() => ApplyFilters(cameras));
            await CheckConnectivityAsync();
            await Dispatcher.InvokeAsync(() => ApplyFilters(cameras));
        } catch (Exception ex) {
            _eventLog?.LogError(EventCategories.Operation, "DeviceManagement", $"清單整理失敗: {ex.Message}");
        }
    }

    private void ApplyFilters(List<Camera> allCameras) {
        var searchText = SearchBox?.Text?.Trim() ?? "";

        var items = new List<CameraDisplayItem>();
        var idx = 1;
        foreach (var c in allCameras) {
            if (!string.IsNullOrEmpty(searchText) &&
                !(c.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(c.IpAddress?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)) {
                continue;
            }

            items.Add(new CameraDisplayItem {
                Index = idx++,
                Camera = c
            });
        }
        CameraGrid.ItemsSource = items;
    }

    private void CameraGrid_Sorting(object sender, DataGridSortingEventArgs e) {
        if (e.Column.SortMemberPath != nameof(Camera.IpAddress)) { return; }

        var view = CollectionViewSource.GetDefaultView(CameraGrid.ItemsSource);
        if (view is not ListCollectionView listView) { return; }

        e.Handled = true;

        var newDir = e.Column.SortDirection switch {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => ListSortDirection.Ascending
        };

        listView.CustomSort = new IpComparer(newDir);
        e.Column.SortDirection = newDir;
    }

    private async Task CheckConnectivityAsync() {
        if (_camerasWithStatus is null) { return; }
        var withIp = new List<Camera>();
        foreach (var c in _camerasWithStatus) {
            if (!string.IsNullOrEmpty(c.IpAddress)) {
                withIp.Add(c);
            }
        }
        if (withIp.Count == 0) { return; }

        var tasks2 = new List<Task>(withIp.Count);
        for (var ci = 0; ci < withIp.Count; ci++) {
            var c = withIp[ci];
            tasks2.Add(((Func<Task>)(async () => {
                try {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var port = c.Port > 0 ? c.Port : 554;
                    var connectTask = tcp.ConnectAsync(c.IpAddress!, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask) {
                        c.IsConnected = tcp.Connected;
                        return;
                    }
                } catch { }

                try {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(c.IpAddress ?? "", 5000);
                    c.IsConnected = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
                } catch {
                    c.IsConnected = false;
                }
            }))());
        }
        await Task.WhenAll(tasks2);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        if (_cameraService is null) { return; }
        var cameras = _camerasWithStatus ?? _cameraService.GetAllCameras();
        ApplyFilters(cameras);
    }

    private Camera? SelectedCamera {
        get {
            if (CameraGrid.SelectedItem is CameraDisplayItem item) {
                return item.Camera;
            }
            return null;
        }
    }

    private void AddCameraButton_Click(object sender, RoutedEventArgs e) {
        try {
            var currentCount = _cameraService.GetAllCameras().Count;
            var maxCameras = _licenseService.GetMaxCameras();
            if (currentCount >= maxCameras) {
                MessageBox.Show($"目前已達授權上限 {maxCameras} 台，無法再新增攝影機",
                    "授權限制", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new CameraEditDialog {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true && dlg.Camera is not null) {
                if (_cameraService.IsIpDuplicate(dlg.Camera.IpAddress!)) {
                    MessageBox.Show($"IP {dlg.Camera.IpAddress} 已存在，無法重複新增攝影機",
                        "IP 重複", MessageBoxButton.OK, MessageBoxImage.Warning);
                } else if (!_cameraService.AddCamera(dlg.Camera)) {
                    MessageBox.Show($"目前已達授權上限 {maxCameras} 台，無法再新增攝影機",
                        "授權限制", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        } catch (Exception ex) {
            MessageBox.Show($"新增攝影機時發生例外狀況：{ex.Message}\n\n{ex.GetType().Name}",
                "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditCameraButton_Click(object sender, RoutedEventArgs e) {
        var camera = SelectedCamera;
        if (camera is null) { return; }

        var dlg = new CameraEditDialog(camera) {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && dlg.Camera is not null) {
            if (_cameraService.IsIpDuplicate(dlg.Camera.IpAddress!, dlg.Camera.Id)) {
                MessageBox.Show($"IP {dlg.Camera.IpAddress} 已存在，無法更新",
                    "IP 重複", MessageBoxButton.OK, MessageBoxImage.Warning);
            } else {
                _cameraService.UpdateCamera(dlg.Camera);
            }
        }
    }

    private void DeleteCameraButton_Click(object sender, RoutedEventArgs e) {
        var camera = SelectedCamera;
        if (camera is null) { return; }

        var result = MessageBox.Show($"確定要刪除攝影機 {camera.Name}？",
            "刪除確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) {
            _cameraService.DeleteCamera(camera.Id);
        }
    }

    private async void OnvifScanButton_Click(object sender, RoutedEventArgs e) {
        var window = Window.GetWindow(this);
        if (window is null) { return; }

        var dlg = new OnvifScanDialog {
            Owner = window
        };
        if (dlg.ShowDialog() != true) { return; }

        var subnet = dlg.Subnet;
        var onvifPort = dlg.Port;
        var username = dlg.Username;
        var password = dlg.Password;

        if (subnet.Split('.').Length < 3) {
            MessageBox.Show("子網段格式不正確，請輸入 x.x.x 格式", "錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OnvifScanButton.IsEnabled = false;
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "正在掃描...";

        try {
            var progress = new Progress<(int current, int total, string ip)>(p => {
                Dispatcher.InvokeAsync(() => {
                    ScanProgressBar.Value = p.current;
                    ScanProgressText.Text = $"掃描中... {p.current}/{p.total} 於 {p.ip}";
                });
            });

            var results = await _onvifService.ScanSubnetAsync(subnet, onvifPort, username, password, progress);

            ScanProgressText.Text = $"掃描完成，發現 {results.Count} 台裝置";
            ScanProgressBar.Value = 254;

            // QCTek fallback when ONVIF WS fails to return RTSP URL
            var qctek = App.Services.GetService<QCTekService>();
            qctek?.Initialize();

            var addedCount = 0;
            var skippedCount = 0;
            foreach (var result in results) {
                // Select profiles by resolution (highest width first)
                OnvifProfile? mainProfile = null, secondProfile = null;
                int bestWidth = -1, bestHeight = -1;
                int secondWidth = -1, secondHeight = -1;
                for (var pi = 0; pi < result.Profiles.Count; pi++) {
                    var p = result.Profiles[pi];
                    if (p.Width > bestWidth || (p.Width == bestWidth && p.Height > bestHeight)) {
                        secondProfile = mainProfile;
                        secondWidth = bestWidth;
                        secondHeight = bestHeight;
                        mainProfile = p;
                        bestWidth = p.Width;
                        bestHeight = p.Height;
                    } else if (p.Width > secondWidth || (p.Width == secondWidth && p.Height > secondHeight)) {
                        secondProfile = p;
                        secondWidth = p.Width;
                        secondHeight = p.Height;
                    }
                }

                // Replace ONVIF WS host with actual camera IP
                var onvifMainUrl = mainProfile is not null
                    ? OnvifService.SubstituteRtspHost(mainProfile.RtspUrl, result.IpAddress ?? "")
                    : null;

                var brandKey = NormalizeBrand(result.Manufacturer);
                string? subUrl = null;

                // Brand-specific URL override for sub-stream naming
                // Brand override: live_st1 / live_st2 naming convention
                if (ShouldOverrideBrandUrl(brandKey)) {
                    var brandMain = RtspUrlBuilder.BuildRtspUrlWithBrand(
                        result.IpAddress ?? "", 554, username, password, brandKey);
                    if (!string.IsNullOrWhiteSpace(brandMain)) {
                        Log.Debug("[HeliVMS] Brand override main: {IpAddress} -> {BrandMain}", result.IpAddress, brandMain);
                        onvifMainUrl = brandMain;
                    }
                    subUrl = RtspUrlBuilder.BuildRtspUrlSubWithBrand(
                        result.IpAddress ?? "", 554, username, password, brandKey);
                    Log.Debug("[HeliVMS] Brand override sub: {IpAddress} -> {SubUrl}", result.IpAddress, subUrl);
                }

                // QCTek fallback: resolve RTSP URL via QC_Onvif.dll
                if (string.IsNullOrWhiteSpace(onvifMainUrl) && qctek?.IsInitialized == true) {
                    var qctekUrl = qctek.OnvifQueryRtspUrl(
                        result.IpAddress ?? "", username, password);
                    if (!string.IsNullOrWhiteSpace(qctekUrl)) {
                        onvifMainUrl = OnvifService.SubstituteRtspHost(qctekUrl, result.IpAddress ?? "");
                        Log.Debug("[HeliVMS] QCTek fallback resolved URL for {IpAddress}: {OnvifMainUrl}", result.IpAddress, onvifMainUrl);
                    }
                }

                if (string.IsNullOrWhiteSpace(onvifMainUrl)) { continue; }

                // Assign sub-stream from second ONVIF profile or derive from main URL
                if (subUrl is null) {
                    if (secondProfile is not null && !string.IsNullOrWhiteSpace(secondProfile.RtspUrl)) {
                        subUrl = OnvifService.SubstituteRtspHost(secondProfile.RtspUrl, result.IpAddress ?? "");
                        if (string.Equals(subUrl, onvifMainUrl, StringComparison.OrdinalIgnoreCase)) {
                            subUrl = null;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(subUrl)) {
                        subUrl = TryBuildSubUrlByBrand(result, username, password)
                            ?? RtspUrlBuilder.DeriveSubStreamUrl(onvifMainUrl);
                    }
                }

                var camera = new Camera {
                    Id = Guid.NewGuid().ToString(),
                    CameraId = Guid.NewGuid().ToString(),
                    Name = $"{result.Manufacturer} {result.Model}",
                    IpAddress = result.IpAddress ?? "",
                    Port = 554,
                    OnvifPort = result.ProbedPort,
                    Username = username,
                    Password = password,
                    Manufacturer = result.Manufacturer,
                    Model = result.Model,
                    SerialNumber = result.SerialNumber,
                    RtspUrl = onvifMainUrl,
                    RtspUrlSub = subUrl,
                    HasPTZ = false,
                    IsEnabled = true,
                    IsVisible = true
                };
                if (_cameraService.AddCamera(camera)) {
                    addedCount++;
                } else {
                    skippedCount++;
                }
            }

            var msg = $"裝置新增 {addedCount} 台攝影機";
            if (skippedCount > 0) {
                msg += $"\n{skippedCount} 台因重複或授權上限而略過";
            }
            MessageBox.Show(msg, "ONVIF 掃描完成", MessageBoxButton.OK, MessageBoxImage.Information);

            _eventLog.LogInfo(EventCategories.Connection, "DeviceManagement", $"ONVIF scan complete", $"added {addedCount} skipped {skippedCount}");
        } catch (Exception ex) {
            ScanProgressText.Text = $"掃描錯誤：{ex.Message}";
            MessageBox.Show($"ONVIF 掃描錯誤：{ex.Message}", "錯誤",
                MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            OnvifScanButton.IsEnabled = true;
        }
    }

    private void ImportLegacyButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "匯入舊版 cameras.json",
            Filter = "JSON 檔案|*.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true) {
            _cameraService.MigrateFromLegacy(dialog.FileName);
            MessageBox.Show("攝影機已從舊版檔案成功匯入", "完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _eventLog.LogInfo(EventCategories.Operation, "DeviceManagement", $"已從舊版檔案匯入: {dialog.FileName}");
        }
    }

    private void CameraGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        var selected = CameraGrid.SelectedItem as CameraDisplayItem;
        var camera = selected?.Camera;
        EditCameraButton.IsEnabled = camera is not null;
        DeleteCameraButton.IsEnabled = camera is not null;
        ShowDetail(camera);
    }

    private void ShowDetail(Camera? camera) {
        if (camera is null) {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = camera.Name;
        DetailIp.Text = camera.IpAddress ?? "-";
        DetailManufacturer.Text = !string.IsNullOrEmpty(camera.Manufacturer)
            ? $"{camera.Manufacturer} {camera.Model ?? ""}".Trim()
            : "-";
        DetailSerial.Text = camera.SerialNumber ?? "-";
        DetailRtspUrl.Text = camera.RtspUrl;
        DetailRtspUrlSub.Text = camera.RtspUrlSub ?? "無子串流";
        DetailOnvifPort.Text = camera.OnvifPort.ToString();
    }

    /// <summary>Whether to override ONVIF GetStreamUri URL with brand-specific path</summary>
    private static bool ShouldOverrideBrandUrl(string? brandKey) {
        return brandKey switch {
            "aver" => true,
            _ => false,
        };
    }

    /// <summary>Normalize manufacturer string to brand key</summary>
    private static string NormalizeBrand(string? manufacturer) {
        if (string.IsNullOrEmpty(manufacturer)) { return ""; }
        var m = manufacturer.ToLowerInvariant();
        if (m.Contains("hikvision")) { return "hikvision"; }
        if (m.Contains("dahua") || m.Contains("大華")) { return "dahua"; }
        if (m.Contains("axis")) { return "axis"; }
        if (m.Contains("foscam")) { return "foscam"; }
        if (m.Contains("vivotek")) { return "vivotek"; }
        if (m.Contains("panasonic")) { return "panasonic"; }
        if (m.Contains("sony")) { return "sony"; }
        if (m.Contains("samsung") || m.Contains("hanwha")) { return "samsung"; }
        if (m.Contains("tplink") || m.Contains("tp-link")) { return "tplink"; }
        if (m.Contains("amcrest")) { return "amcrest"; }
        if (m.Contains("reolink")) { return "reolink"; }
        if (m.Contains("bosch")) { return "bosch"; }
        if (m.Contains("pelco")) { return "pelco"; }
        if (m.Contains("aver") || m.Contains("avermedia") || m.Contains("aver information")) { return "aver"; }
        return "";
    }

    private static string? TryBuildSubUrlByBrand(OnvifDiscoveryResult result, string username, string password) {
        var brandKey = NormalizeBrand(result.Manufacturer);
        if (string.IsNullOrEmpty(brandKey)) { return null; }
        return RtspUrlBuilder.BuildRtspUrlSubWithBrand(
            result.IpAddress ?? "", 554, username, password, brandKey);
    }
}

public class CameraDisplayItem {
    public int Index { get; set; }
    public Camera? Camera { get; set; }

    public string Model => Camera?.Model ?? "";
    public string IpAddress => Camera?.IpAddress ?? "";
    public bool IsEnabled => Camera?.IsEnabled ?? false;
    public bool IsConnected => Camera?.IsConnected ?? false;
    public int? ChannelNumber => Camera?.ChannelNumber;
    public string RtspUrl => Camera?.RtspUrl ?? "";
    public string? RtspUrlSub => Camera?.RtspUrlSub;
}

file sealed class IpComparer(ListSortDirection dir) : IComparer {
    private readonly ListSortDirection _direction = dir;

    public int Compare(object? a, object? b) {
        var ipA = TryParseIp(GetIp(a));
        var ipB = TryParseIp(GetIp(b));
        var result = Comparer<long>.Default.Compare(ipA, ipB);
        return _direction == ListSortDirection.Ascending ? result : -result;
    }

    private static string? GetIp(object? obj) {
        return obj switch {
            Camera cam => cam.IpAddress,
            CameraDisplayItem item => item.IpAddress,
            _ => null
        };
    }

    private static long TryParseIp(string? ipStr) {
        if (string.IsNullOrEmpty(ipStr)) { return 0; }
        if (IPAddress.TryParse(ipStr, out var ip)) {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4) {
                return (long)bytes[0] << 24 | (long)bytes[1] << 16 | (long)bytes[2] << 8 | bytes[3];
            }
        }
        return 0;
    }
}
