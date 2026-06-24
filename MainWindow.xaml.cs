using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HeliVMS.Models;
using HeliVMS.Services;
using HeliVMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS;

public partial class MainWindow : Window {
    private readonly IAuthenticationService _auth;
    private readonly IAuthorizationService _authz;
    private readonly ISystemStatusService _status;
    private readonly NotificationHistoryService _notifHistory;
    private readonly DispatcherTimer _statusTimer;
    private bool _isShuttingDown;
    private bool _drawerOpen = true;
    private bool _notifPanelOpen;
    private string? _activeView;
    private string? _restoreView;
    private string? _restoreLayoutTab;

    private const double DrawerOpenWidth = 220;
    private const double DrawerClosedWidth = 0;

    public MainWindow() {
        InitializeComponent();

        _auth = App.Services.GetRequiredService<IAuthenticationService>();
        _authz = App.Services.GetRequiredService<IAuthorizationService>();
        _status = App.Services.GetRequiredService<ISystemStatusService>();
        _notifHistory = App.Services.GetRequiredService<NotificationHistoryService>();

        _auth.LoggedOut += () => NavigateToLogin();
        _auth.SessionExpired += () => Dispatcher.InvokeAsync(() => {
            _auth.Logout();
            ShowToast("閒置超過 30 分鐘，已自動登出", "WARN");
        });

        var notif = App.Services.GetRequiredService<INotificationService>();
        notif.NotificationReceived += args =>
            _ = Dispatcher.InvokeAsync(() => ShowToast(args.Message, args.Severity));

        _notifHistory.Updated += () => Dispatcher.InvokeAsync(UpdateNotifBadge);

        EventManager.RegisterClassHandler(typeof(UIElement),
            UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _auth.ResetSessionTimer()));
        EventManager.RegisterClassHandler(typeof(UIElement),
            UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => _auth.ResetSessionTimer()));

        Closing += MainWindow_Closing;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatusBar();
        _statusTimer.Start();
        _status.StartMonitoring();

        NotifSidePanel.CloseRequested += (_, _) => ToggleNotifPanel(false);

        LiveDrawer.CameraAction += (cameraId, action) => {
            if (MainWorkArea.Content is Views.LiveView live) {
                _ = Dispatcher.InvokeAsync(() => live.HandleCameraAction(cameraId, action));
            }
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        LoadWindowState();
        UpdateNotifBadge();
        if (_auth.IsLoggedIn) {
            RestoreSession();
            SwitchToLive();
        } else {
            _auth.LoginSucceeded += OnLoginSucceeded;
            NavigateToLogin();
        }
    }

    private void OnLoginSucceeded(User user) {
        _auth.LoginSucceeded -= OnLoginSucceeded;
        try {
            var tray = App.Services.GetRequiredService<TrayIconService>();
            tray.Initialize(this);
            Log.Information("[Tray] Tray icon initialized after login");
        } catch (Exception ex) {
            Log.Warning(ex, "[Tray] Failed to initialize tray icon");
        }
        SwitchToLive();
    }

    // ═══════════════════════════════════════════════════════════
    //  Notification popup
    // ═══════════════════════════════════════════════════════════

    private void ToggleNotifPanel_Click(object sender, RoutedEventArgs e) {
        ToggleNotifPanel();
    }

    private void UpdateNotifBadge() {
        var unread = _notifHistory.UnreadCount;
        NotifBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
    }

    private void MarkNotifRead_Click(object sender, RoutedEventArgs e) {
        if (sender is MenuItem { Tag: NotificationEntry entry }) {
            _notifHistory.MarkAsRead(entry.Id);
            NotificationList.ItemsSource = null;
            NotificationList.ItemsSource = _notifHistory.Entries;
        }
    }

    private void MarkAllNotifRead_Click(object sender, RoutedEventArgs e) {
        _notifHistory.MarkAllAsRead();
        NotificationList.Items.Refresh();
    }

    private void CloseNotifPopup_Click(object sender, RoutedEventArgs e) {
        NotificationPopup.IsOpen = false;
    }

    private void ClearAllNotif_Click(object sender, RoutedEventArgs e) {
        _notifHistory.Clear();
        NotificationList.ItemsSource = null;
    }

    // ═══════════════════════════════════════════════════════════
    //  Keyboard shortcuts
    // ═══════════════════════════════════════════════════════════

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F || (e.Key == Key.F11)) {
            var live = MainWorkArea.Content as Views.LiveView;
            if (live is not null) {
                live.ToggleFullScreen();
                e.Handled = true;
            }
        } else if (e.Key == Key.Escape) {
            var live = MainWorkArea.Content as Views.LiveView;
            if (live is not null && live.IsFullScreen) {
                live.ToggleFullScreen();
                e.Handled = true;
            }
        } else if (e.Key == Key.F1 || e.Key == Key.H || e.Key == Key.OemQuestion) {
            ShowShortcutHelp();
            e.Handled = true;
        } else if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.R) {
            var live = MainWorkArea.Content as Views.LiveView;
            if (live is not null) {
                live.ToggleRecordingSelectedCamera();
                e.Handled = true;
            }
        } else if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.F) {
            FocusTreeSearch();
            e.Handled = true;
        } else if (e.Key == Key.System && e.KeyboardDevice.Modifiers == ModifierKeys.Alt) {
            switch (e.SystemKey) {
                case Key.D1: ToggleDrawer(); e.Handled = true; break;
                case Key.D2: ToggleNotifPanel(); e.Handled = true; break;
                case Key.D3:
                    if (MainWorkArea.Content is LiveView lv) {
                        lv.TimelineVisible = !lv.TimelineVisible;
                        ShowToast(lv.TimelineVisible ? "時間軸已顯示" : "時間軸已隱藏", "INFO");
                    }
                    e.Handled = true; break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Navigation
    // ═══════════════════════════════════════════════════════════

    private void BtnLive_Click(object sender, RoutedEventArgs e) => SwitchToLive();
    private void BtnDevice_Click(object sender, RoutedEventArgs e) => SwitchToDevice();
    private void BtnLicense_Click(object sender, RoutedEventArgs e) => SwitchToLicense();
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => SwitchToSettings();
    private void BtnDashboard_Click(object sender, RoutedEventArgs e) => SwitchToDashboard();
    private void BtnEMap_Click(object sender, RoutedEventArgs e) => SwitchToEMap();
    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e) {
        try {
            App.Services.GetRequiredService<ThemeService>().Toggle();
            BtnTheme.ToolTip = App.Services.GetRequiredService<ThemeService>().CurrentTheme == "Dark" ? "切換至淺色主題" : "切換至深色主題";
        } catch { }
    }

    private void SwitchToLive() {
        SelectNavButton(BtnLive);
        ShowDrawer(true);
        LiveDrawer.Visibility = Visibility.Visible;
        SubMenuDrawer.Visibility = Visibility.Collapsed;
        _activeView = "live";
        MainWorkArea.Content = new LiveView();
        Log.Debug("[HeliVMS] Navigated to Live");
    }

    public void SwitchToLive(DateTime date) {
        SelectNavButton(BtnLive);
        ShowDrawer(true);
        LiveDrawer.Visibility = Visibility.Visible;
        SubMenuDrawer.Visibility = Visibility.Collapsed;
        var lv = new LiveView();
        lv.Loaded += (_, _) => lv.NavigateToDate(date);
        MainWorkArea.Content = lv;
        Log.Debug("[HeliVMS] Navigated to Live (date: {Date})", date.ToString("yyyy-MM-dd"));
    }

    private void SwitchToDevice() {
        SelectNavButton(BtnDevice);
        ShowDrawer(false);
        _activeView = "device";
        MainWorkArea.Content = new DeviceManagementView();
        Log.Debug("[HeliVMS] Navigated to DeviceManagement");
    }

    private void SwitchToLicense() {
        SelectNavButton(BtnLicense);
        ShowDrawer(false);
        _activeView = "license";
        MainWorkArea.Content = new LicenseView();
        Log.Debug("[HeliVMS] Navigated to License");
    }

    private void SwitchToSettings() {
        SelectNavButton(BtnSettings);
        ShowDrawer(true);
        LiveDrawer.Visibility = Visibility.Collapsed;
        SubMenuDrawer.Visibility = Visibility.Visible;
        _activeView = "settings";
        MainWorkArea.Content = new SettingsView();
        Log.Debug("[HeliVMS] Navigated to Settings");
    }

    private void SwitchToDashboard() {
        SelectNavButton(BtnDashboard);
        ShowDrawer(false);
        LiveDrawer.Visibility = Visibility.Collapsed;
        SubMenuDrawer.Visibility = Visibility.Collapsed;
        _activeView = "dashboard";
        MainWorkArea.Content = new DashboardView();
        Log.Debug("[HeliVMS] Navigated to Dashboard");
    }

    private void SwitchToEMap() {
        SelectNavButton(BtnEMap);
        ShowDrawer(false);
        LiveDrawer.Visibility = Visibility.Collapsed;
        SubMenuDrawer.Visibility = Visibility.Collapsed;
        _activeView = "emap";
        MainWorkArea.Content = new EMapView();
        Log.Debug("[HeliVMS] Navigated to EMap");
    }

    private void FocusTreeSearch() {
        if (LiveDrawer.Visibility == Visibility.Visible) {
            LiveDrawer.FocusSearch();
        }
    }

    private void RefreshStatusBar() {
        var total = _status.CameraTotalCount;
        var online = _status.CameraOnlineCount;
        var recording = _status.RecordingActiveCount;
        StatusCameras.Text = $"{online}/{total} 在線 · {recording} 錄影";
        StatusBandwidth.Text = _status.StatusSummary;
        StatusStorage.Text = $"儲存 {_status.DiskUsagePercent:F1}%";
        StatusCpu.Text = $"⚡ CPU {_status.CpuUsagePercent:F1}%";
        StatusMemory.Text = $"記憶體 {_status.MemoryUsagePercent:F1}%";
        StatusTime.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    // ─── Window State Persistence ───

    private static string StatePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "window_state.json");

    private void SaveWindowState() {
        try {
            var dir = Path.GetDirectoryName(StatePath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var activeTab = MainWorkArea.Content is LiveView lv ? lv.SelectedTabId : null;
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new {
                DrawerOpen = _drawerOpen,
                NotifPanelOpen = _notifPanelOpen,
                ActiveView = _activeView,
                ActiveLayoutTab = activeTab
            }));
        } catch { }
    }

    private void LoadWindowState() {
        try {
            if (!File.Exists(StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("DrawerOpen", out var d))
                ShowDrawer(d.GetBoolean());
            if (root.TryGetProperty("NotifPanelOpen", out var n) && n.GetBoolean())
                ToggleNotifPanel(true);
            if (root.TryGetProperty("ActiveView", out var v))
                _restoreView = v.GetString();
            if (root.TryGetProperty("ActiveLayoutTab", out var lt))
                _restoreLayoutTab = lt.GetString();
        } catch { }
    }

    private void RestoreSession() {
        if (_restoreLayoutTab is not null && MainWorkArea.Content is LiveView lv) {
            lv.TabBar.SelectTab(_restoreLayoutTab);
        }
    }

    private void NavigateToLogin() {
        MainWorkArea.Content = new LoginView();
        ShowDrawer(false);
        Log.Debug("[HeliVMS] Navigated to Login");
    }

    private void SelectNavButton(Button selected) {
        foreach (var btn in new[] { BtnLive, BtnDevice, BtnLicense, BtnDashboard, BtnEMap, BtnSettings })
            btn.Opacity = btn == selected ? 1.0 : 0.35;
    }

    // ═══════════════════════════════════════════════════════════
    //  Drawer Animation
    // ═══════════════════════════════════════════════════════════

    public void ToggleDrawer() => ShowDrawer(!_drawerOpen);

    public void ToggleNotifPanel() => ToggleNotifPanel(!_notifPanelOpen);

    private void ToggleNotifPanel(bool open) {
        _notifPanelOpen = open;
        NotifColumn.Width = open ? new GridLength(260) : new GridLength(0);
        NotifSidePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SaveWindowState();
    }

    public void ShowDrawer(bool open) {
        if (_drawerOpen == open) return;
        _drawerOpen = open;
        SaveWindowState();
        var target = open ? DrawerOpenWidth : DrawerClosedWidth;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DrawerContainer.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    // ═══════════════════════════════════════════════════════════
    //  Shortcut help overlay
    // ═══════════════════════════════════════════════════════════

    private void ShowShortcutHelp() {
        var overlay = new Window {
            Title = "鍵盤快捷鍵",
            Content = new Views.ShortcutHelpView(),
            Width = 440,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
        };
        overlay.ShowDialog();
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Screen — hide nav bar + drawer
    // ═══════════════════════════════════════════════════════════

    public void SetFullScreenUI(bool isFullScreen) {
        NavColumn.Width = isFullScreen ? new GridLength(0) : new GridLength(45);
        DrawerColumn.Width = isFullScreen ? new GridLength(0) : GridLength.Auto;
    }

    // ═══════════════════════════════════════════════════════════
    //  Toast
    // ═══════════════════════════════════════════════════════════

    private DispatcherTimer? _toastTimer;

    private void ShowToast(string message, string severity) {
        ToastText.Text = $"[{severity}] {message}";
        ToastOverlay.Visibility = Visibility.Visible;
        if (_toastTimer is null) {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (_, _) => {
                _toastTimer?.Stop();
                ToastOverlay.Visibility = Visibility.Collapsed;
            };
        } else {
            _toastTimer.Stop();
        }
        _toastTimer.Start();
    }

    // ═══════════════════════════════════════════════════════════
    //  Shutdown
    // ═══════════════════════════════════════════════════════════

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (_isShuttingDown) return;
        e.Cancel = true;
        await ShutdownApplicationAsync();
    }

    private async Task ShutdownApplicationAsync() {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        var shutdownWindow = new ShutdownWindow();
        shutdownWindow.Show();

        try {
            var scheduler = App.Services.GetRequiredService<RecordingSchedulerService>();
            var stopTask = Task.Run(() =>
                scheduler.Stop(msg => shutdownWindow.AppendStatus(msg)));
            if (await Task.WhenAny(stopTask, Task.Delay(6000)) != stopTask)
                shutdownWindow.AppendStatus("停止錄影逾時，強制終止殘留 ffmpeg\u2026");
        } catch (Exception ex) {
            Log.Debug(ex, "[HeliVMS] ShutdownApplicationAsync error");
        } finally {
            RecordingService.KillAllFfmpegProcesses();
        }

        shutdownWindow.Close();
        Application.Current.Shutdown();
    }
}