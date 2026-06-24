// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Windows;
using Serilog;
using System.Windows.Controls;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Dialog;

public partial class CameraEditDialog : Window {
    public Camera? Camera { get; private set; }
    private readonly Camera? _existingCamera;
    private readonly ICameraService? _cameraService;

    // ONVIF detection cache (stored into Camera on save)
    private string? _detectedManufacturer;
    private string? _detectedModel;

    public CameraEditDialog() : this(null) { }

    public CameraEditDialog(Camera? existingCamera) {
        InitializeComponent();
        _existingCamera = existingCamera;
        _cameraService = App.Services.GetService<ICameraService>();

        LoadBrands();
        LoadGroups();

        if (existingCamera is not null) {
            DialogTitle.Text = "編輯攝影機";
            LoadExisting(existingCamera);
        }

        Loaded += async (_, _) => {
            try {
                Activate();
                IpBox.Focus();

                // Auto-detect dual streams via ONVIF
                if (!string.IsNullOrWhiteSpace(IpBox.Text)) {
                    await AutoDetectStreamsAsync();
                }
            } catch (Exception ex) {
                Log.Debug(ex, "[HeliVMS] CameraEditDialog Loaded error");
            }
        };
    }

    private void LoadBrands() {
        var brands = new List<BrandEntry>(14)
        {
            new("通用", "", 554, 80),
            new("Hikvision（海康威視）", "hikvision", 554, 80),
            new("Dahua（大華）", "dahua", 554, 80),
            new("Axis", "axis", 554, 80),
            new("Foscam", "foscam", 554, 88),
            new("Vivotek", "vivotek", 554, 80),
            new("Panasonic", "panasonic", 554, 80),
            new("Sony", "sony", 554, 80),
            new("Samsung / Hanwha", "samsung", 554, 80),
            new("TP-Link", "tplink", 554, 80),
            new("Amcrest", "amcrest", 554, 80),
            new("Reolink", "reolink", 554, 80),
            new("Bosch", "bosch", 554, 80),
            new("Pelco", "pelco", 554, 80),
        };
        BrandCombo.ItemsSource = brands;
        BrandCombo.DisplayMemberPath = "DisplayName";
        BrandCombo.SelectedIndex = 0;
    }

    private void LoadGroups() {
        if (_cameraService is not null) {
            var allCamsForGroup = _cameraService.GetAllCameras();
            var existingGroups = new List<string>();
            var seenEditGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var gi = 0; gi < allCamsForGroup.Count; gi++) {
                var g = allCamsForGroup[gi].Group;
                if (!string.IsNullOrWhiteSpace(g) && seenEditGroups.Add(g)) {
                    existingGroups.Add(g);
                }
            }
            existingGroups.Sort(StringComparer.OrdinalIgnoreCase);
            GroupCombo.ItemsSource = existingGroups;
        }
    }

    private void LoadExisting(Camera camera) {
        NameBox.Text = camera.Name;
        IpBox.Text = camera.IpAddress ?? "";
        PortBox.Text = camera.Port.ToString();
        OnvifPortBox.Text = camera.OnvifPort.ToString();
        UsernameBox.Text = camera.Username ?? "";
        PasswordBox.Password = camera.Password ?? "";
        RtspUrlBox.Text = camera.RtspUrl;
        RtspUrlSubBox.Text = camera.RtspUrlSub ?? "";
        GroupCombo.Text = camera.Group ?? "";

        MotionEnabledCheck.IsChecked = camera.IsMotionDetectionEnabled;
        MotionSensitivitySlider.Value = camera.MotionSensitivity;
        MotionSensitivityLabel.Text = camera.MotionSensitivity.ToString("F2");
        MotionSensitivitySlider.IsEnabled = camera.IsMotionDetectionEnabled;

        AutoReconnectCheck.IsChecked = camera.AutoReconnectEnabled;
        MaxRetryBox.Text = camera.MaxReconnectAttempts.ToString();
        ReconnectIntervalBox.Text = camera.ReconnectIntervalSeconds.ToString();

        if (!string.IsNullOrEmpty(camera.Manufacturer)) {
            SelectBrandByPorts(camera.Port, camera.OnvifPort);
        }
    }

    private void SelectBrandByPorts(int rtspPort, int onvifPort) {
        if (BrandCombo.ItemsSource is System.Collections.IList items) {
            foreach (BrandEntry b in items) {
                if (b.RtspPort == rtspPort && b.OnvifPort == onvifPort) {
                    BrandCombo.SelectedItem = b;
                    return;
                }
            }
        }
        BrandCombo.SelectedIndex = 0;
    }

    /// <summary>Auto-detect dual streams via ONVIF and fill RTSP URL fields</summary>
    private async Task AutoDetectStreamsAsync() {
        var ip = IpBox.Text.Trim();
        var onvifPort = int.TryParse(OnvifPortBox.Text, out var op) ? op : 80;
        var user = UsernameBox.Text.Trim();
        var pass = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(ip)) { return; }

        try {
            var onvif = App.Services.GetRequiredService<IOnvifService>();
            var (mainUrl, subUrl) = await onvif.TryResolveStreamUrlsAsync(
                ip, onvifPort, user, pass, RtspUrlBox.Text, RtspUrlSubBox.Text);

            if (!string.IsNullOrWhiteSpace(mainUrl)) {
                RtspUrlBox.Text = mainUrl;
            }
            if (!string.IsNullOrWhiteSpace(subUrl)) {
                RtspUrlSubBox.Text = subUrl;
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] CameraEditDialog auto-detect failed: {Msg}", ex.Message);
        }
    }

    /// <summary>Get main/sub stream URLs via ONVIF (with QCTek fallback)</summary>
    private async Task<(string MainUrl, string SubUrl)> DetectBothRtspUrlsAsync(
        string ip, int onvifPort, string? username, string? password) {
        try {
            var onvif = App.Services.GetRequiredService<IOnvifService>();
            return await onvif.TryResolveStreamUrlsAsync(
                ip, onvifPort, username ?? "", password ?? "",
                RtspUrlBox.Text, RtspUrlSubBox.Text);
        } catch {
            // Fallback: generate URL via brand rules
            var brandKey = (BrandCombo.SelectedItem as BrandEntry)?.BrandKey ?? "";
            var mainUrl = RtspUrlBuilder.BuildRtspUrlWithBrand(ip,
                int.TryParse(PortBox.Text, out var p) ? p : 554,
                username ?? "", password ?? "", brandKey);
            var subUrl = RtspUrlBuilder.BuildRtspUrlSubWithBrand(ip,
                int.TryParse(PortBox.Text, out var p2) ? p2 : 554,
                username ?? "", password ?? "", brandKey);
            return (mainUrl, subUrl);
        }
    }

    private void BrandCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (BrandCombo.SelectedItem is BrandEntry brand && !string.IsNullOrEmpty(brand.BrandKey)) {
            PortBox.Text = brand.RtspPort.ToString();
            OnvifPortBox.Text = brand.OnvifPort.ToString();
        }

        UpdateRtspPreview();
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e) => UpdateRtspPreview();

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateRtspPreview();

    private void UpdateRtspPreview() {
        try {
            if (IpBox is null || PortBox is null || UsernameBox is null ||
                PasswordBox is null || BrandCombo is null || RtspUrlBox is null)
                return;

            var ip = IpBox.Text.Trim();
            var portText = PortBox.Text.Trim();
            var user = UsernameBox.Text.Trim();
            var pass = PasswordBox.Password;
            var brandKey = (BrandCombo.SelectedItem as BrandEntry)?.BrandKey ?? "";

            if (string.IsNullOrEmpty(ip) || !int.TryParse(portText, out var port)) {
                RtspUrlBox.Text = "";
                return;
            }

            RtspUrlBox.Text = !string.IsNullOrEmpty(brandKey)
                ? RtspUrlBuilder.BuildRtspUrlWithBrand(ip, port, user, pass, brandKey)
                : RtspUrlBuilder.BuildRtspUrl(ip, port, user, pass);
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] CameraEditDialog UpdateRtspPreview error: {Ex}", ex);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) {
        if (!ValidateInput()) { return; }

        var ip = IpBox.Text.Trim();
        var port = int.Parse(PortBox.Text.Trim());
        var onvifPort = int.Parse(OnvifPortBox.Text.Trim());
        var brandKey = (BrandCombo.SelectedItem as BrandEntry)?.BrandKey ?? "";

        // Prefer RtspUrlBox.Text (ONVIF auto-detect / manual input), fallback to Builder if empty
        var rtspUrl = !string.IsNullOrWhiteSpace(RtspUrlBox.Text)
            ? RtspUrlBox.Text.Trim()
            : !string.IsNullOrEmpty(brandKey)
                ? RtspUrlBuilder.BuildRtspUrlWithBrand(ip, port, UsernameBox.Text.Trim(), PasswordBox.Password, brandKey)
                : RtspUrlBuilder.BuildRtspUrl(ip, port, UsernameBox.Text.Trim(), PasswordBox.Password);

        // ONVIF detection result first, then existing value or brand dropdown
        var manufacturer = _detectedManufacturer
            ?? (BrandCombo.SelectedItem as BrandEntry)?.DisplayName
            ?? _existingCamera?.Manufacturer;
        var model = _detectedModel ?? _existingCamera?.Model;
        var serialNumber = _existingCamera?.SerialNumber;

        var defaultRecConfig = new CameraRecordingConfigData {
            IsContinuousEnabled = true,
            Quality = "Original",
            EnableAudio = true,
            PreRecordSeconds = 5,
            PostRecordSeconds = 10,
            RetentionDays = 30,
            SegmentDuration = 3600
        };

        Camera = new Camera {
            Id = _existingCamera?.Id ?? Guid.NewGuid().ToString(),
            CameraId = _existingCamera?.CameraId ?? Guid.NewGuid().ToString(),
            Name = NameBox.Text.Trim(),
            IpAddress = ip,
            Port = port,
            OnvifPort = onvifPort,
            Username = UsernameBox.Text.Trim(),
            Password = PasswordBox.Password,
            RtspUrl = rtspUrl,
            RtspUrlSub = string.IsNullOrWhiteSpace(RtspUrlSubBox.Text) ? _existingCamera?.RtspUrlSub : RtspUrlSubBox.Text.Trim(),
            OnvifResolvedUrl = _existingCamera?.OnvifResolvedUrl,
            OnvifResolvedUrlSub = _existingCamera?.OnvifResolvedUrlSub,
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = serialNumber,
            Group = string.IsNullOrWhiteSpace(GroupCombo.Text) ? null : GroupCombo.Text.Trim(),
            HasPTZ = _existingCamera?.HasPTZ ?? false,
            IsEnabled = _existingCamera?.IsEnabled ?? true,
            IsVisible = _existingCamera?.IsVisible ?? true,
            IsRecordingEnabled = true,
            RecordingConfigJson = defaultRecConfig.Serialize(),
            CreatedAt = _existingCamera?.CreatedAt ?? DateTime.Now,
            IsMotionDetectionEnabled = MotionEnabledCheck.IsChecked ?? false,
            MotionSensitivity = MotionSensitivitySlider.Value,
            AutoReconnectEnabled = AutoReconnectCheck.IsChecked ?? true,
            MaxReconnectAttempts = int.TryParse(MaxRetryBox.Text, out var retries) ? retries : 0,
            ReconnectIntervalSeconds = int.TryParse(ReconnectIntervalBox.Text, out var interval) ? Math.Max(5, interval) : 30,
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "測試中...";
        try {
            var ip = IpBox.Text.Trim();
            var port = int.TryParse(PortBox.Text, out var p) ? p : 554;
            var ok = await Task.Run(() => {
                try { using var tcp = new System.Net.Sockets.TcpClient(); var ar = tcp.BeginConnect(ip, port, null, null); return ar.AsyncWaitHandle.WaitOne(3000) && tcp.Connected; }
                catch { return false; }
            });
            var user = UsernameBox.Text.Trim();
            var pass = PasswordBox.Password;
            MessageBox.Show(ok ? $"{ip}:{port} 連線成功" : "連線失敗，請檢查 IP/Port", "測試結果",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        } catch (Exception ex) {
            MessageBox.Show($"連線測試異常：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            btn.IsEnabled = true;
            btn.Content = "測試連線";
        }
    }

    private async void OnvifDetectButton_Click(object sender, RoutedEventArgs e) {
        var ip = IpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) {
            MessageBox.Show("請先輸入 IP 位址再進行 ONVIF 偵測", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OnvifDetectButton.IsEnabled = false;
        OnvifDetectButton.Content = "偵測中...";

        try {
            var onvif = App.Services.GetRequiredService<IOnvifService>();
            var onvifPort = int.TryParse(OnvifPortBox.Text, out var op) ? op : 80;
            var user = UsernameBox.Text.Trim();
            var pass = PasswordBox.Password;

            // First get device info
            var (manufacturer, model, name) = await onvif.ProbeDeviceInfoAsync(ip, onvifPort, user, pass);

            if (!string.IsNullOrEmpty(manufacturer)) {
                // Cache ONVIF detection result, written to Camera on save
                _detectedManufacturer = manufacturer;
                _detectedModel = model;

                if (string.IsNullOrWhiteSpace(NameBox.Text)) {
                    NameBox.Text = name ?? manufacturer;
                }

                // Resolve dual stream URLs
                var (mainUrl, subUrl) = await onvif.TryResolveStreamUrlsAsync(
                    ip, onvifPort, user, pass, RtspUrlBox.Text, RtspUrlSubBox.Text);

                if (!string.IsNullOrWhiteSpace(mainUrl)) {
                    RtspUrlBox.Text = mainUrl;
                }
                if (!string.IsNullOrWhiteSpace(subUrl)) {
                    RtspUrlSubBox.Text = subUrl;
                }

                MessageBox.Show($"ONVIF 偵測成功\n廠商：{manufacturer}\n型號：{model}" +
                    $"\n主串流：{mainUrl}\n子串流：{subUrl}",
                    "ONVIF 偵測", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                MessageBox.Show("ONVIF 偵測失敗，請確認設備支援 ONVIF", "ONVIF 偵測",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        } catch {
            MessageBox.Show("ONVIF 偵測發生錯誤", "ONVIF 偵測",
                MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            OnvifDetectButton.IsEnabled = true;
            OnvifDetectButton.Content = "ONVIF 偵測";
        }
    }

    private bool ValidateInput() {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) {
            ShowWarning("請輸入攝影機名稱");
            NameBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(IpBox.Text)) {
            ShowWarning("請輸入 IP 位址");
            IpBox.Focus();
            return false;
        }
        if (!System.Net.IPAddress.TryParse(IpBox.Text.Trim(), out _)) {
            ShowWarning("請輸入有效的 IP 位址格式（如 192.168.1.100）");
            IpBox.Focus();
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var rp) || rp < 1 || rp > 65535) {
            ShowWarning("請輸入有效的 RTSP 連接埠 (1-65535)");
            PortBox.Focus();
            return false;
        }
        if (!int.TryParse(OnvifPortBox.Text.Trim(), out var op) || op < 1 || op > 65535) {
            ShowWarning("請輸入有效的 ONVIF 連接埠 (1-65535)");
            OnvifPortBox.Focus();
            return false;
        }
        return true;
    }

    private static void ShowWarning(string msg) {
        MessageBox.Show(msg, "驗證錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnMotionEnabledChanged(object sender, RoutedEventArgs e) {
        MotionSensitivitySlider.IsEnabled = MotionEnabledCheck.IsChecked ?? false;
    }

    private void OnMotionSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        MotionSensitivityLabel.Text = e.NewValue.ToString("F2");
    }

    private record BrandEntry(string DisplayName, string BrandKey, int RtspPort, int OnvifPort);
}
