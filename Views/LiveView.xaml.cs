using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Views;

public partial class LiveView : UserControl
{
    private readonly ICameraService _cameraService;
    private readonly IEventService _eventLog;
    private readonly ISystemStatusService _systemStatus;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _healthTimer;
    private string? _selectedGroup;
    private bool _isFullScreen;
    private bool _ptzWasVisible;
    private bool _gridRefreshPending;
    private bool _isShuttingDown;
    private bool _isLoaded;

    private static readonly string[] DayNames = { "日", "一", "二", "三", "四", "五", "六" };

    public LiveView()
    {
        InitializeComponent();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        _systemStatus = App.Services.GetRequiredService<ISystemStatusService>();
        CameraGridControl.PtzSelected += OnPtzSelected;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += (_, _) => SyncCameraHealth();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) { return; }
        _isLoaded = true;
        _isShuttingDown = false;
        _gridRefreshPending = false;
        _cameraService.CamerasChanged += OnCamerasChanged;
        RefreshGroupFilter();
        // Restore layout split from settings before LoadCameras
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            if (settings.Settings.LiveViewSplitLayout > 0)
            {
                CameraGridControl.SetSplitLayoutOnly(settings.Settings.LiveViewSplitLayout);
            }
        }
        catch { }
        RefreshCameraGrid();
        _clockTimer.Start();
        _healthTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) { return; }
        _isLoaded = false;
        _isShuttingDown = true;
        _cameraService.CamerasChanged -= OnCamerasChanged;
        _clockTimer.Stop();
        _healthTimer.Stop();
    }

    private void SyncCameraHealth()
    {
        var allCams = _cameraService.GetAllCameras();
        var cameras = new List<Camera>();
        for (int i = 0; i < allCams.Count; i++)
        {
            if (allCams[i].IsEnabled)
            {
                cameras.Add(allCams[i]);
            }
        }
        _systemStatus.CameraTotalCount = cameras.Count;
        int onlineCount = 0;
        for (int i = 0; i < cameras.Count; i++)
        {
            if (cameras[i].IsConnected) { onlineCount++; }
        }
        _systemStatus.CameraOnlineCount = onlineCount;
        _systemStatus.RecordingActiveCount = App.Services.GetRequiredService<IRecordingService>().GetActiveRecordings().Count;
    }

    private int _healthTick;

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LiveClockDateText.Text = $"{now:yyyy-MM-dd} {DayNames[(int)now.DayOfWeek]}";
        LiveClockTimeText.Text = now.ToString("HH:mm:ss");
        _healthTick++;
        if (_healthTick >= 30)
        {
            _healthTick = 0;
            LogHealthSummary();
        }
    }

    private void LogHealthSummary()
    {
        if (_isShuttingDown) { return; }
        try
        {
            var allCams = _cameraService.GetAllCameras();
            int total = allCams.Count;
            int enabled = 0;
            for (int i = 0; i < allCams.Count; i++)
            {
                if (allCams[i].IsEnabled) { enabled++; }
            }
            int visible = 0;
            for (int i = 0; i < allCams.Count; i++)
            {
                if (allCams[i].IsVisible && allCams[i].IsEnabled) { visible++; }
            }
            int ptz = 0;
            for (int i = 0; i < allCams.Count; i++)
            {
                if (allCams[i].HasPTZ) { ptz++; }
            }
            DebugLogger.Info(DebugLogger.CatCamera, "HealthSummary",
                $"total={total} enabled={enabled} visible={visible} ptz={ptz}");
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.CatCamera, "HealthSummary", $"LogHealthSummary failed: {ex.Message}");
        }
    }

    private void OnCamerasChanged()
    {
        if (_isShuttingDown) { return; }
        if (_gridRefreshPending) { return; }
        _gridRefreshPending = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            _gridRefreshPending = false;
            if (_isShuttingDown) { return; }
            RefreshGroupFilter();
            RefreshCameraGrid();
        });
    }

    private void RefreshCameraGrid()
    {
        // Guard: skip during shutdown to prevent deadlock
        if (_isShuttingDown) { return; }
        var allCameras = _cameraService.GetAllCameras();

        List<Camera> filtered;
        if (string.IsNullOrEmpty(_selectedGroup))
        {
            filtered = allCameras;
        }
        else
        {
            filtered = new List<Camera>();
            for (int i = 0; i < allCameras.Count; i++)
            {
                if (string.Equals(allCameras[i].Group, _selectedGroup, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(allCameras[i]);
                }
            }
        }

        var channelSlots = new Camera?[64];
        var filteredForGrid = new List<Camera>();
        for (int i = 0; i < filtered.Count; i++)
        {
            var c = filtered[i];
            if (c.IsEnabled && c.IsVisible && c.ChannelNumber.HasValue
                && c.ChannelNumber >= 1 && c.ChannelNumber <= 64)
            {
                filteredForGrid.Add(c);
            }
        }
        var ordered = new List<Camera>(filteredForGrid);
        ordered.Sort((a, b) =>
        {
            var aVal = a.GridOrder > 0 ? a.GridOrder : a.ChannelNumber!.Value;
            var bVal = b.GridOrder > 0 ? b.GridOrder : b.ChannelNumber!.Value;
            return aVal.CompareTo(bVal);
        });
        for (int i = 0; i < Math.Min(ordered.Count, 64); i++)
        {
            channelSlots[i] = ordered[i];
        }

        // Debug log first 7 cameras with GridOrder
        for (int i = 0; i < Math.Min(7, ordered.Count); i++)
        {
            var c = ordered[i];
            if (c is null) { continue; }
            System.Diagnostics.Debug.WriteLine(
                $"[HeliVMS]   slot[{i}] = {c.Name} CH{c.ChannelNumber} order={c.GridOrder}");
        }

        int activeCount = 0;
        for (int i = 0; i < allCameras.Count; i++)
        {
            if (allCameras[i].IsEnabled && allCameras[i].IsVisible) { activeCount++; }
        }
        int filteredCount = 0;
        for (int i = 0; i < filtered.Count; i++)
        {
            if (filtered[i].IsEnabled && filtered[i].IsVisible) { filteredCount++; }
        }
        CameraCountText.Text = string.IsNullOrEmpty(_selectedGroup)
            ? $"{activeCount} 台攝影機"
            : $"{filteredCount}/{activeCount} 台攝影機";
        try
        {
            CameraGridControl.LoadCameras(channelSlots);
        }
        catch when (_isShuttingDown)
        {
            // Suppress shutdown exceptions
        }

        // Connect first PTZ camera to PTZ panel, show sidebar if any PTZ exists
        Camera? ptzCamera = null;
        for (int ci = 0; ci < allCameras.Count; ci++)
        {
            if (allCameras[ci].HasPTZ)
            { ptzCamera = allCameras[ci]; break; }
        }
        if (ptzCamera is not null)
        {
            ShowPtzForCamera(ptzCamera);
        }
        else
        {
            PtzSidebar.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private void RefreshGroupFilter()
    {
        var selectedText = GroupFilterCombo.SelectedItem is ComboBoxItem sel ? sel.Content?.ToString() : null;
        GroupFilterCombo.SelectionChanged -= GroupFilterCombo_SelectionChanged;
        GroupFilterCombo.Items.Clear();
        GroupFilterCombo.Items.Add(new ComboBoxItem { Content = "全部" });

        var allCamsForGroups = _cameraService.GetAllCameras();
        var groups = new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < allCamsForGroups.Count; gi++)
        {
            var g = allCamsForGroups[gi].Group;
            if (!string.IsNullOrWhiteSpace(g) && seenGroups.Add(g))
            {
                groups.Add(g);
            }
        }
        groups.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            GroupFilterCombo.Items.Add(new ComboBoxItem { Content = g });
        }

        // Restore previous selection
        if (selectedText is not null)
        {
            foreach (ComboBoxItem item in GroupFilterCombo.Items)
            {
                if (string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                { item.IsSelected = true; break; }
            }
        }
        else if (GroupFilterCombo.Items.Count > 0)
        {
            ((ComboBoxItem)GroupFilterCombo.Items[0]).IsSelected = true;
        }
        GroupFilterCombo.SelectionChanged += GroupFilterCombo_SelectionChanged;
    }

    private void GroupFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cameraService is null) { return; }
        if (GroupFilterCombo.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString();
            _selectedGroup = text == "全部" ? null : text;
            RefreshCameraGrid();
        }
    }

    private void OnPtzSelected(Camera? camera)
    {
        if (camera is not null && camera.HasPTZ)
        {
            ShowPtzForCamera(camera);
        }
    }

    private void ShowPtzForCamera(Camera camera)
    {
        PtzCameraName.Text = $"PTZ 控制 - {camera.Name}";
        PtzPanel.LoadCamera(camera);
        PtzSidebar.Visibility = System.Windows.Visibility.Visible;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen) { return; }
        _isFullScreen = true;

        if (Window.GetWindow(this) is MainWindow win)
        {
            _ptzWasVisible = PtzSidebar.Visibility == Visibility.Visible;
            win.Sidebar.Visibility = Visibility.Collapsed;
            win.PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        ToolbarPanel.Visibility = Visibility.Collapsed;
        PtzSidebar.Visibility = Visibility.Collapsed;
        CameraGridControl.Margin = new Thickness(0);
    }

    private void RestoreFromFullScreen()
    {
        if (!_isFullScreen) { return; }
        _isFullScreen = false;

        if (Window.GetWindow(this) is MainWindow win)
        {
            win.Sidebar.Visibility = Visibility.Visible;
            win.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }

        ToolbarPanel.Visibility = Visibility.Visible;
        if (_ptzWasVisible)
        {
            PtzSidebar.Visibility = Visibility.Visible;
        }
        CameraGridControl.Margin = new Thickness(4);
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            RestoreFromFullScreen();
            e.Handled = true;
        }
    }

    public void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            RestoreFromFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void CtxFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            RefreshCameraGrid();
            e.Handled = true;
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var size))
        {
            CameraGridControl.SetLayout(size);
            _eventLog.LogInfo(EventCategories.Operation, "LiveView", $"佈局切換至 {size}x{size}");
            SaveLayoutSetting(size);

            if (btn.Parent is WrapPanel panel)
            {
                var highlight = btn.TryFindResource("PrimaryBrush") as System.Windows.Media.Brush;
                foreach (var child in panel.Children)
                {
                    if (child is Button b)
                    {
                        b.Background = b == btn
                            ? (highlight ?? System.Windows.Media.Brushes.DodgerBlue)
                            : System.Windows.Media.Brushes.Transparent;
                    }
                }
            }
        }
    }

    private void RestoreLayoutFromSettings()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            int split = settings.Settings.LiveViewSplitLayout;
            if (split > 0 && CameraGridControl.CurrentLayout == 0)
            {
                CameraGridControl.SetLayout(split);
                // Highlight active layout button
                HighlightLayoutButton(split);
            }
        }
        catch { /* Settings save error */ }
    }

    private void HighlightLayoutButton(int splitLayout)
    {
        var btnMap = new Dictionary<int, Button>
        {
            { 1, Layout1 }, { 4, Layout4 }, { 9, Layout9 }, { 16, Layout16 },
            { 25, Layout25 }, { 36, Layout36 }, { 49, Layout49 }, { 64, Layout64 }
        };
        if (btnMap.TryGetValue(splitLayout, out var btn))
        {
            var highlight = btn.TryFindResource("PrimaryBrush") as System.Windows.Media.Brush;
            foreach (var kvp in btnMap)
            {
                kvp.Value.Background = kvp.Key == splitLayout
                    ? (highlight ?? System.Windows.Media.Brushes.DodgerBlue)
                    : System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    private void SaveLayoutSetting(int splitLayout)
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            settings.Settings.LiveViewSplitLayout = splitLayout;
            settings.Settings.LiveViewCurrentPage = CameraGridControl.CurrentPage;
            settings.Save();
        }
        catch { /* Settings save error */ }
    }
}
