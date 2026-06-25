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
    private string _currentSubTab = "detail";
    private List<Camera>? _camerasWithStatus;
    private CameraGroupService? _lazyGroupService;
    private CameraGroupService GroupService => _lazyGroupService ??= App.Services.GetRequiredService<CameraGroupService>();
    private string? _selectedGroupId;

    private void TabButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) {
            ShowTab(index);
        }
    }

    public void ShowTab(int index) {
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

        TabGroup.BorderBrush = index == 4
            ? TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue
            : Brushes.Transparent;
        TabGroup.Foreground = index == 4
            ? TryFindResource("TextBrush") as Brush ?? Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        DeviceSettingsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingSettingsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        StorageManagerPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ChannelManagementPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        GroupPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 1) {
            RecordingSettingsPanel.RefreshCameraList();
        } else if (index == 3) {
            ChannelManagementPanel.LoadChannels();
        } else if (index == 4) {
            LoadGroupTab();
        }
    }

    private void SubTabButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button btn && btn.Tag is string tag) {
            _currentSubTab = tag;
            UpdateSubTabVisual();
        }
    }

    private void UpdateSubTabVisual() {
        var active = _currentSubTab;
        var primary = TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var secondaryText = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        SubTabDetail.BorderBrush = active == "detail" ? primary : Brushes.Transparent;
        SubTabDetail.Foreground = active == "detail" ? text : secondaryText;
        SubTabRecording.BorderBrush = active == "recording" ? primary : Brushes.Transparent;
        SubTabRecording.Foreground = active == "recording" ? text : secondaryText;
        SubTabChannels.BorderBrush = active == "channels" ? primary : Brushes.Transparent;
        SubTabChannels.Foreground = active == "channels" ? text : secondaryText;

        var hasSelection = CameraListBox.SelectedItem is CameraDisplayItem;
        DetailScrollViewer.Visibility = active == "detail" && hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SubRecordingPanel.Visibility = active == "recording" && hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SubChannelPanel.Visibility = active == "channels" && hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (active == "recording" && hasSelection) {
            SubRecordingPanel.RefreshCameraList();
        } else if (active == "channels" && hasSelection) {
            SubChannelPanel.LoadChannels();
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
        foreach (var c in allCameras) {
            if (!string.IsNullOrEmpty(searchText) &&
                !(c.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(c.IpAddress?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)) {
                continue;
            }

            items.Add(new CameraDisplayItem {
                Camera = c
            });
        }
        CameraListBox.ItemsSource = items;
        CameraCountBadge.Text = $"{items.Count}/{allCameras.Count}";
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
            if (CameraListBox.SelectedItem is CameraDisplayItem item) {
                return item.Camera;
            }
            return null;
        }
    }

    private void CameraListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        var camera = SelectedCamera;
        EditCameraButton.IsEnabled = camera is not null;
        DeleteCameraButton.IsEnabled = camera is not null;
        ShowDetail(camera);
    }

    private void ShowDetail(Camera? camera) {
        if (camera is null) {
            DetailScrollViewer.Visibility = Visibility.Collapsed;
            SubRecordingPanel.Visibility = Visibility.Collapsed;
            SubChannelPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        _currentSubTab = "detail";
        UpdateSubTabVisual();

        DetailName.Text = camera.Name;
        DetailIp.Text = camera.IpAddress ?? "-";
        DetailManufacturer.Text = !string.IsNullOrEmpty(camera.Manufacturer)
            ? $"{camera.Manufacturer} {camera.Model ?? ""}".Trim()
            : "-";
        DetailSerial.Text = camera.SerialNumber ?? "-";
        DetailRtspUrl.Text = camera.RtspUrl;
        DetailRtspUrlSub.Text = camera.RtspUrlSub ?? "無子串流";
        DetailOnvifPort.Text = camera.OnvifPort.ToString();
        DetailChannelNumber.Text = camera.ChannelNumber?.ToString() ?? "-";

        DetailEnabledText.Text = camera.IsEnabled ? "是" : "否";
        DetailPtzText.Text = camera.HasPTZ ? "支援" : "不支援";

        var successBrush = TryFindResource("SuccessBrush") as Brush ?? Brushes.LimeGreen;
        var errorBrush = TryFindResource("ErrorBrush") as Brush ?? Brushes.OrangeRed;

        if (camera.IsConnected) {
            DetailStatusDot.Fill = successBrush;
            DetailStatusText.Text = "連線中";
            DetailStatusText.Foreground = successBrush;
        } else {
            DetailStatusDot.Fill = errorBrush;
            DetailStatusText.Text = "離線";
            DetailStatusText.Foreground = errorBrush;
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

    private void CopyRtspUrl_Click(object sender, RoutedEventArgs e) {
        var camera = SelectedCamera;
        if (camera is null) { return; }
        try {
            Clipboard.SetText(camera.RtspUrl);
        } catch { }
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e) {
        var camera = SelectedCamera;
        if (camera is null) { return; }
        _ = ConnectionTestAsync(camera);
    }

    private async Task ConnectionTestAsync(Camera camera) {
        try {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync(camera.IpAddress ?? "", camera.Port > 0 ? camera.Port : 554);
            if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask && tcp.Connected) {
                MessageBox.Show($"連線成功：{camera.IpAddress}:{camera.Port}",
                    "連線測試", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                MessageBox.Show($"連線逾時：{camera.IpAddress}:{camera.Port}",
                    "連線測試", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        } catch (Exception ex) {
            MessageBox.Show($"連線失敗：{ex.Message}",
                "連線測試", MessageBoxButton.OK, MessageBoxImage.Error);
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

            var qctek = App.Services.GetService<QCTekService>();
            qctek?.Initialize();

            var addedCount = 0;
            var skippedCount = 0;
            foreach (var result in results) {
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

                var onvifMainUrl = mainProfile is not null
                    ? OnvifService.SubstituteRtspHost(mainProfile.RtspUrl, result.IpAddress ?? "")
                    : null;

                var brandKey = NormalizeBrand(result.Manufacturer);
                string? subUrl = null;

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

                if (string.IsNullOrWhiteSpace(onvifMainUrl) && qctek?.IsInitialized == true) {
                    var qctekUrl = qctek.OnvifQueryRtspUrl(
                        result.IpAddress ?? "", username, password);
                    if (!string.IsNullOrWhiteSpace(qctekUrl)) {
                        onvifMainUrl = OnvifService.SubstituteRtspHost(qctekUrl, result.IpAddress ?? "");
                        Log.Debug("[HeliVMS] QCTek fallback resolved URL for {IpAddress}: {OnvifMainUrl}", result.IpAddress, onvifMainUrl);
                    }
                }

                if (string.IsNullOrWhiteSpace(onvifMainUrl)) { continue; }

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

    private static bool ShouldOverrideBrandUrl(string? brandKey) {
        return brandKey switch {
            "aver" => true,
            _ => false,
        };
    }

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

    private void LoadGroupTab() {
        GroupListBox.ItemsSource = null;
        GroupListBox.ItemsSource = GroupService.Groups.ToList();
        GroupListBox.DisplayMemberPath = "Name";
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (GroupListBox.SelectedItem is CameraGroup group) {
            _selectedGroupId = group.Id;
            GroupCameraHeader.Text = $"群組：{group.Name}";
            var cameras = _cameraService.GetAllCameras();
            var checkItems = cameras.Select(c => new CameraCheckItem {
                Id = c.Id,
                Name = c.Name ?? c.Id,
                IsInGroup = group.CameraIds.Contains(c.Id),
            }).ToList();
            GroupCameraCheckList.ItemsSource = checkItems;
        } else {
            _selectedGroupId = null;
            GroupCameraHeader.Text = "選取群組以編輯攝影機";
            GroupCameraCheckList.ItemsSource = null;
        }
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e) {
        var name = NewGroupNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("請輸入群組名稱", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        GroupService.AddGroup(name);
        NewGroupNameBox.Clear();
        LoadGroupTab();
        GroupListBox.SelectedIndex = GroupListBox.Items.Count - 1;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e) {
        if (_selectedGroupId is null) { MessageBox.Show("請先選取要刪除的群組", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show("確定刪除此群組？攝影機不會被刪除。", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
            GroupService.DeleteGroup(_selectedGroupId);
            _selectedGroupId = null;
            LoadGroupTab();
            GroupCameraCheckList.ItemsSource = null;
        }
    }

    private void GroupCameraCheck_Changed(object sender, RoutedEventArgs e) {
        if (_selectedGroupId is null || sender is not CheckBox { DataContext: CameraCheckItem item }) return;
        if (item.IsInGroup) {
            GroupService.AddCameraToGroup(_selectedGroupId, item.Id);
        } else {
            GroupService.RemoveCameraFromGroup(_selectedGroupId, item.Id);
        }
    }

    private class CameraCheckItem {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsInGroup { get; set; }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) {
        var dlg = new Microsoft.Win32.SaveFileDialog {
            Filter = "CSV 檔案 (*.csv)|*.csv",
            FileName = $"cameras_{DateTime.Now:yyyyMMdd}.csv",
            Title = "匯出攝影機清單"
        };
        if (dlg.ShowDialog() != true) return;
        try {
            var cameras = _cameraService.GetAllCameras();
            using var sw = new System.IO.StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            sw.WriteLine("Name,IP,Port,RtspUrl,Username,Password,Group,Enabled,MotionEnabled");
            foreach (var cam in cameras) {
                var escapedName = cam.Name?.Replace("\"", "\"\"") ?? "";
                sw.WriteLine($"\"{escapedName}\",{cam.IpAddress},{cam.Port},{cam.RtspUrl},{cam.Username},{cam.Password},{cam.Group},{cam.IsEnabled},{cam.IsMotionDetectionEnabled}");
            }
            System.Windows.MessageBox.Show($"已匯出 {cameras.Count} 台攝影機", "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            System.Windows.MessageBox.Show($"匯出失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e) {
        var dlg = new Microsoft.Win32.OpenFileDialog {
            Filter = "CSV 檔案 (*.csv)|*.csv",
            Title = "匯入攝影機清單"
        };
        if (dlg.ShowDialog() != true) return;
        try {
            var lines = System.IO.File.ReadAllLines(dlg.FileName, System.Text.Encoding.UTF8);
            if (lines.Length < 2) {
                System.Windows.MessageBox.Show("CSV 檔案格式不正確（缺少標題或資料行）", "匯入失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var imported = 0;
            var skipped = 0;
            for (var i = 1; i < lines.Length; i++) {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var fields = ParseCsvLine(line);
                if (fields.Length < 4) { skipped++; continue; }
                var name = fields[0];
                var ip = fields[1];
                var port = int.TryParse(fields[2], out var p) ? p : 554;
                var rtsp = fields[3];
                var user = fields.Length > 4 ? fields[4] : "";
                var pass = fields.Length > 5 ? fields[5] : "";
                var group = fields.Length > 6 ? fields[6] : "";
                var enabled = fields.Length <= 7 || !bool.TryParse(fields[7], out var en) || en;
                var motionEnabled = fields.Length > 8 && bool.TryParse(fields[8], out var m) && m;
                if (_cameraService.IsIpDuplicate(ip)) { skipped++; continue; }
                var cam = new Camera {
                    Name = name,
                    IpAddress = ip,
                    Port = port,
                    RtspUrl = rtsp,
                    Username = user,
                    Password = pass,
                    Group = group,
                    IsEnabled = enabled,
                    IsMotionDetectionEnabled = motionEnabled
                };
                if (_cameraService.AddCamera(cam)) imported++; else skipped++;
            }
            System.Windows.MessageBox.Show($"匯入完成：{imported} 台成功，{skipped} 台略過", "匯入結果", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            System.Windows.MessageBox.Show($"匯入失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string[] ParseCsvLine(string line) {
        var result = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++) {
            var c = line[i];
            if (c == '"') {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') {
                    current.Append('"');
                    i++;
                } else {
                    inQuotes = !inQuotes;
                }
            } else if (c == ',' && !inQuotes) {
                result.Add(current.ToString());
                current.Clear();
            } else {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return [.. result];
    }

    private static string? TryBuildSubUrlByBrand(OnvifDiscoveryResult result, string username, string password) {
        var brandKey = NormalizeBrand(result.Manufacturer);
        if (string.IsNullOrEmpty(brandKey)) { return null; }
        return RtspUrlBuilder.BuildRtspUrlSubWithBrand(
            result.IpAddress ?? "", 554, username, password, brandKey);
    }
}

public class CameraDisplayItem {
    public Camera? Camera { get; set; }

    public string DisplayName => Camera?.Name ?? "未知";
    public string IpAddress => Camera?.IpAddress ?? "";
    public bool IsConnected => Camera?.IsConnected ?? false;
    public bool IsEnabled => Camera?.IsEnabled ?? false;

    public Brush StatusColor {
        get {
            if (IsConnected) {
                var brush = Application.Current.TryFindResource("SuccessBrush") as Brush;
                return brush ?? Brushes.LimeGreen;
            }
            if (!IsEnabled) {
                var brush = Application.Current.TryFindResource("SecondaryTextBrush") as Brush;
                return brush ?? Brushes.Gray;
            }
            var errorBrush = Application.Current.TryFindResource("ErrorBrush") as Brush;
            return errorBrush ?? Brushes.OrangeRed;
        }
    }
}
