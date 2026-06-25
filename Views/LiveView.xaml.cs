using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HeliVMS.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Views;

public partial class LiveView : UserControl {
    private ICameraService? _lazyCamera;
    private ICameraService CameraService => _lazyCamera ??= App.Services.GetRequiredService<ICameraService>();
    private ILayoutService? _lazyLayout;
    private ILayoutService LayoutService => _lazyLayout ??= App.Services.GetRequiredService<ILayoutService>();
    private Camera? _ptzCamera;
    private bool _isFullScreen;
    public bool IsFullScreen => _isFullScreen;
    private bool _initialized;

    private int _currentGridSize = 4;
    private const int DefaultGridSize = 4;
    public string? SelectedTabId => TabBar.CurrentTab?.Id;
    private const int MaxSlots = 64;
    private readonly ISettingsService _settings = App.Services.GetRequiredService<ISettingsService>();

    public LiveView() {
        InitializeComponent();
        Loaded += LiveView_Loaded;
        Action<string, bool> onRecording = (camId, isRec) =>
            _ = Dispatcher.InvokeAsync(() => VideoGrid.SetRecordingIndicator(camId, isRec));
        RecordingService.RecordingStatusChanged += onRecording;
        Unloaded += (_, _) => {
            RecordingService.RecordingStatusChanged -= onRecording;
            Cleanup();
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Loaded / Ignition
    // ═══════════════════════════════════════════════════════════

    private void LiveView_Loaded(object sender, RoutedEventArgs e) {
        if (_initialized) return;
        _initialized = true;

        VideoGrid.DropCameraRequested += OnDropCameraToGrid;
        VideoGrid.SlotChanged += OnSlotChanged;
        VideoGrid.SlotContextMenuRequested += OnGridContextMenu;
        TabBar.TabSelected += OnTabSelected;
        TabBar.TabAdded += OnTabAdded;
        TabBar.TabRenamed += (_, id) => SaveCurrentTabs();
        TabBar.TabsReordered += (_, _) => SaveCurrentTabs();

        VideoGrid.SetSlotCount(DefaultGridSize);
        LayoutLabel.Text = DefaultGridSize.ToString();
        Log.Debug("[LiveView] Grid layout set to {Size}x{Size}",
            DefaultGridSize, DefaultGridSize);

        LoadLayoutTabs();
        ReloadAllCamerasIntoGrid();

        Log.Debug("[LiveView] Loaded — cameras in grid: {Count}",
            VideoGrid.GetSlotCameras().Count(c => c is not null));
    }

    private void Cleanup() {
    }

    // ═══════════════════════════════════════════════════════════
    //  Camera Grid — Load / Reload
    // ═══════════════════════════════════════════════════════════

    private void ReloadAllCamerasIntoGrid() {
        try {
            var all = CameraService.GetAllCameras();
            if (all.Count == 0) {
                Log.Warning("[LiveView] No cameras returned from service");
                App.Services.GetRequiredService<INotificationService>().Show("尚未新增攝影機 — 請至設備管理新增", "WARN");
                VideoGrid.SetSlotCount(DefaultGridSize);
                return;
            }

            var count = Math.Min(all.Count, MaxSlots);
            var arr = new Camera?[count];
            for (int i = 0; i < count; i++)
                arr[i] = all[i];

            var threshold = _settings.Settings.SubStreamThreshold;
            var useSub = count >= threshold;
            VideoGrid.LoadCameras(arr, useSub);
            foreach (var cam in arr)
                if (cam is not null)
                    VideoGrid.SetRecordingIndicator(cam.Id, RecordingService.IsRecording(cam.Id));
            Log.Debug("[LiveView] Loaded {Count}/{Total} cameras into grid (subStream={Sub}, threshold={Thresh})", count, all.Count, useSub, threshold);
        } catch (Exception ex) {
            Log.Error(ex, "[LiveView] Failed to load cameras");
        }
    }

    private void OnGridContextMenu(string cameraId, string action) {
        var cam = CameraService.GetCameraById(cameraId);
        if (cam is null) return;
        switch (action) {
            case "全畫面": {
                var slots = VideoGrid.GetSlotCameras().ToList();
                var idx = slots.FindIndex(c => c?.Id == cameraId);
                if (idx >= 0) VideoGrid.ToggleMaximize(idx);
                break;
            }
            case "拍照": HandleCameraAction(cameraId, "snapshot"); break;
            case "移除攝影機": {
                var slots = VideoGrid.GetSlotCameras().ToList();
                var idx = slots.FindIndex(c => c?.Id == cameraId);
                if (idx >= 0) VideoGrid.RemoveSlot(idx);
                break;
            }
            case "開始錄影": RecordingService.StartRecording(cam); break;
            case "停止錄影": RecordingService.StopRecording(cam.Id); break;
            case "PTZ 控制": HandleCameraAction(cameraId, "ptz"); break;
            case "加入書籤": HandleCameraAction(cameraId, "bookmark"); break;
        }
    }

    private void OnSlotChanged(Camera? camera, int slotIndex) {
        _ptzCamera = camera is { HasPTZ: true } ? camera : null;
        PtzBar.Visibility = _ptzCamera is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_ptzCamera is not null)
            PtzLabel.Text = _ptzCamera.Name;
        if (TabBar.CurrentTab is not null)
            TabBar.MarkDirty(TabBar.CurrentTab.Id);
    }

    private void OnDropCameraToGrid(string cameraId, int slotIndex) {
        try {
            Log.Debug("[LiveView] OnDropCameraToGrid: cameraId={Id}, slot={Slot}", cameraId, slotIndex);
            var camera = CameraService.GetCameraById(cameraId);
            if (camera is null) {
                Log.Warning("[LiveView] Drop: camera {Id} not found", cameraId);
                return;
            }
            Log.Debug("[LiveView] OnDropCameraToGrid: found camera {Name}({Id}), rtsp={Rtsp}", camera.Name, camera.Id, camera.RtspUrl);

            var existing = VideoGrid.GetCameraAt(slotIndex);
            Log.Debug("[LiveView] OnDropCameraToGrid: existing at slot={Slot} = {Name}", slotIndex, existing?.Name ?? "none");
            if (existing is not null) {
                for (int i = 0; i < MaxSlots; i++) {
                    if (VideoGrid.GetCameraAt(i)?.Id == cameraId && i != slotIndex) {
                        Log.Debug("[LiveView] OnDropCameraToGrid: swapping existing to slot {Slot}", i);
                        VideoGrid.AssignSlot(i, existing);
                        break;
                    }
                }
            }

            VideoGrid.AssignSlot(slotIndex, camera);
            Log.Debug("[LiveView] Camera {Name} dropped into slot {Slot}", camera.Name, slotIndex);
        } catch (Exception ex) {
            Log.Error(ex, "[LiveView] DropCameraToGrid failed");
        }
    }

    private const int LayoutGridCells = 8;
    private bool _layoutPopupInitialized;
    private int _hoverCol = -1, _hoverRow = -1;

    private void LayoutToggle_Click(object sender, RoutedEventArgs e) {
        if (!_layoutPopupInitialized)
            InitLayoutPopup();
        LayoutPopup.IsOpen = !LayoutPopup.IsOpen;
    }

    private void InitLayoutPopup() {
        _layoutPopupInitialized = true;
        LayoutDragGrid.Children.Clear();
        LayoutDragGrid.RowDefinitions.Clear();
        LayoutDragGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < LayoutGridCells; i++) {
            LayoutDragGrid.RowDefinitions.Add(new RowDefinition());
            LayoutDragGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int r = 0; r < LayoutGridCells; r++) {
            for (int c = 0; c < LayoutGridCells; c++) {
                var cell = new Border {
                    Background = Brushes.Transparent,
                    BorderBrush = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Margin = new Thickness(1),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                LayoutDragGrid.Children.Add(cell);
            }
        }
    }

    private void LayoutDragGrid_MouseMove(object sender, MouseEventArgs e) {
        var pos = e.GetPosition(LayoutDragGrid);
        var cellW = LayoutDragGrid.ActualWidth / LayoutGridCells;
        var cellH = LayoutDragGrid.ActualHeight / LayoutGridCells;
        var col = (int)(pos.X / cellW);
        var row = (int)(pos.Y / cellH);
        col = Math.Clamp(col, 0, LayoutGridCells - 1);
        row = Math.Clamp(row, 0, LayoutGridCells - 1);
        if (col == _hoverCol && row == _hoverRow) return;
        _hoverCol = col;
        _hoverRow = row;
        UpdateDragSelection(col, row);
        var size = Math.Max(col + 1, row + 1);
        var layout = size * size;
        LayoutSizeLabel.Text = $"{layout} ({size}×{size})";
    }

    private void LayoutDragGrid_MouseLeave(object sender, MouseEventArgs e) {
        _hoverCol = -1;
        _hoverRow = -1;
        ClearDragSelection();
        LayoutSizeLabel.Text = _currentGridSize.ToString();
    }

    private void LayoutDragGrid_MouseDown(object sender, MouseButtonEventArgs e) {
        if (_hoverCol >= 0 && _hoverRow >= 0) {
            var size = Math.Max(_hoverCol + 1, _hoverRow + 1);
            ApplyLayout(size * size);
            LayoutPopup.IsOpen = false;
        }
    }

    private void UpdateDragSelection(int col, int row) {
        var accent = TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var accentDim = new SolidColorBrush(
            accent is SolidColorBrush scb
                ? Color.FromArgb(60, scb.Color.R, scb.Color.G, scb.Color.B)
                : Color.FromArgb(60, 30, 136, 229));
        foreach (var child in LayoutDragGrid.Children) {
            if (child is Border b) {
                var r = Grid.GetRow(b);
                var c = Grid.GetColumn(b);
                if (r <= row && c <= col) {
                    b.Background = accentDim;
                    b.BorderBrush = accent;
                } else {
                    b.Background = Brushes.Transparent;
                    b.BorderBrush = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;
                }
            }
        }
    }

    private void ClearDragSelection() {
        var dim = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;
        foreach (var child in LayoutDragGrid.Children) {
            if (child is Border b) {
                b.Background = Brushes.Transparent;
                b.BorderBrush = dim;
            }
        }
    }

    private void ApplyLayout(int size) {
        _currentGridSize = size;
        VideoGrid.SetSlotCount(size);
        if (TabBar.CurrentTab is not null)
            TabBar.MarkDirty(TabBar.CurrentTab.Id);
        LayoutLabel.Text = size.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    //  Layout Tabs
    // ═══════════════════════════════════════════════════════════

    private void LoadLayoutTabs() {
        var tabs = LayoutService.GetAllTabs();
        if (tabs.Count == 0) {
            var def = LayoutService.CreateTab("預設佈局");
            tabs = [def];
        }
        TabBar.LoadTabs(tabs);
        if (tabs.Count > 0)
            LoadTabLayout(tabs[0]);
    }

    private void OnTabSelected(object? sender, LayoutTab tab) {
        SaveCurrentTabToService();
        LoadTabLayout(tab);
    }

    private void OnTabAdded(object? sender, LayoutTab tab) {
        LayoutService.SaveTab(tab);
        LoadTabLayout(tab);
    }

    private void LoadTabLayout(LayoutTab tab) {
        _currentGridSize = tab.SlotCount;
        VideoGrid.SetSlotCount(tab.SlotCount);
        LayoutLabel.Text = tab.SlotCount.ToString();
        var cameras = CameraService.GetAllCameras();
        var camMap = cameras.ToDictionary(c => c.Id, c => c);
        var arr = new Camera?[tab.CameraIds.Count];
        for (int i = 0; i < tab.CameraIds.Count; i++) {
            var camId = tab.CameraIds[i];
            arr[i] = camId is not null && camMap.TryGetValue(camId, out var cam) ? cam : null;
        }
        if (arr.Length > 0)
            VideoGrid.LoadCameras(arr);
    }

    private void SaveCurrentTabToService() {
        var tab = TabBar.CurrentTab;
        if (tab is null) return;
        tab.SlotCount = _currentGridSize;
        tab.CameraIds = VideoGrid.GetSlotCameras().Take(_currentGridSize).Select(c => c?.Id).ToList();
        LayoutService.SaveTab(tab);
    }

    private void SaveCurrentTabs() {
        var tab = TabBar.CurrentTab;
        if (tab is not null) LayoutService.SaveTab(tab);
    }

    private void BtnSaveLayout_Click(object sender, RoutedEventArgs e) {
        var tab = TabBar.CurrentTab;
        if (tab is null) return;
        var dlg = new Dialog.InputDialog("重新命名佈局", "請輸入佈局名稱：", tab.Name) {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            tab.Name = dlg.Value.Trim();
            LayoutService.SaveTab(tab);
            TabBar.MarkClean(tab.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Recording
    // ═══════════════════════════════════════════════════════════

    private IRecordingService? _lazyRecording;
    private IRecordingService RecordingService => _lazyRecording ??= App.Services.GetRequiredService<IRecordingService>();

    public void ToggleRecordingSelectedCamera() {
        var selected = VideoGrid.GetActiveSlots().FirstOrDefault(p => p.IsSelected);
        var cam = selected?.Camera;
        if (cam is null) return;
        if (RecordingService.IsRecording(cam.Id)) {
            RecordingService.StopRecording(cam.Id);
        } else {
            RecordingService.StartRecording(cam);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Camera Actions (snapshot, bookmark, etc.)
    // ═══════════════════════════════════════════════════════════

    public void HandleCameraAction(string cameraId, string action) {
        var cam = App.Services.GetRequiredService<ICameraService>().GetAllCameras()
            .FirstOrDefault(c => c.Id == cameraId);
        if (cam is null) return;
        var bookmarks = App.Services.GetRequiredService<IBookmarkService>();
        switch (action) {
            case "play": {
                var grid = VideoGrid;
                var empty = grid.GetSlotCameras().ToList();
                var freeIndex = empty.FindIndex(c => c is null);
                if (freeIndex >= 0) grid.AssignSlot(freeIndex, cam);
                break;
            }
            case "open_new_tab": {
                var layout = new LayoutTab { Name = cam.Name };
                TabBar.AddTab(layout);
                TabBar.SelectTab(layout.Id);
                ReloadAllCamerasIntoGrid();
                var slots = VideoGrid.GetSlotCameras().ToList();
                var freeIndex = slots.FindIndex(c => c is null);
                if (freeIndex >= 0) VideoGrid.AssignSlot(freeIndex, cam);
                break;
            }
            case "snapshot": {
                var slots = VideoGrid.GetActiveSlots();
                foreach (var s in slots) {
                    if (s?.Camera?.Id == cameraId) {
                        var snapDir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HeliVMS");
                        System.IO.Directory.CreateDirectory(snapDir);
                        s.SaveSnapshot(snapDir);
                        break;
                    }
                }
                break;
            }
            case "bookmark": {
                var now = DateTime.Now;
                var bm = new PlaybackBookmark {
                    Seconds = now.TimeOfDay.TotalSeconds,
                    Note = $"{cam.Name} @ {now:HH:mm:ss}"
                };
                bookmarks.SaveBookmark(bm, DateTime.Today);
                break;
            }
            case "start_recording":
                RecordingService.StartRecording(cam);
                break;
            case "stop_recording":
                RecordingService.StopRecording(cam.Id);
                break;
            case "camera_settings": {
                var nav = App.Services.GetRequiredService<INavigationService>();
                nav.NavigateTo(NavPage.DeviceManagement);
                break;
            }
            case "ptz": {
                var panel = new PTZControlPanel { Width = 240 };
                panel.LoadCamera(cam);
                var win = new Window {
                    Title = $"PTZ — {cam.Name}", Content = panel,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Owner = Window.GetWindow(this),
                    Background = Brushes.Transparent,
                    WindowStyle = WindowStyle.ToolWindow, Topmost = true,
                };
                win.Closed += (_, _) => panel.UnloadCamera();
                win.Show();
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  PTZ — popup panel with presets + tours
    // ═══════════════════════════════════════════════════════════

    private void BtnPtz_Click(object sender, RoutedEventArgs e) {
        if (_ptzCamera is null) return;
        var panel = new PTZControlPanel { Width = 240 };
        panel.LoadCamera(_ptzCamera);
        var win = new Window {
            Title = $"PTZ — {_ptzCamera.Name}",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Owner = Window.GetWindow(this),
            Background = Brushes.Transparent,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
        };
        win.Closed += (_, _) => panel.UnloadCamera();
        win.Show();
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Screen
    // ═══════════════════════════════════════════════════════════

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    public void ToggleFullScreen() {
        if (_isFullScreen) ExitFullScreen(); else EnterFullScreen();
    }

    private void EnterFullScreen() {
        if (_isFullScreen) return;
        _isFullScreen = true;

        ControlBar.Visibility = Visibility.Collapsed;

        if (Window.GetWindow(this) is MainWindow main) {
            main.SetFullScreenUI(true);
            main.WindowStyle = WindowStyle.None;
            main.WindowState = WindowState.Maximized;
            main.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void ExitFullScreen() {
        if (!_isFullScreen) return;
        _isFullScreen = false;

        if (Window.GetWindow(this) is MainWindow main) {
            main.SetFullScreenUI(false);
            main.WindowState = WindowState.Normal;
            main.WindowStyle = WindowStyle.SingleBorderWindow;
            main.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }

        ControlBar.Visibility = Visibility.Visible;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) ExitFullScreen();
    }

    // ═══════════════════════════════════════════════════════════
    //  Misc
    // ═══════════════════════════════════════════════════════════

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F5) { ReloadAllCamerasIntoGrid(); e.Handled = true; }
    }
}
