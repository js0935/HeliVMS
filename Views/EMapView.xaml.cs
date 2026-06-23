using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace HeliVMS.Views;

public partial class EMapView : UserControl {
    private readonly EMapService _emap;
    private readonly ICameraService _cameras;
    private readonly INavigationService _nav;
    private readonly INotificationService _notify;
    private readonly IAudioTalkService _talkService;
    private bool _isDraggingMap;
    private Point _dragStart;
    private double _origOffsetX, _origOffsetY;
    private bool _isDraggingCamera;
    private FrameworkElement? _dragTarget;
    private Point _dragCameraStart;
    private double _origCameraLeft, _origCameraTop;
    private Window? _previewWindow;

    private static readonly SolidColorBrush OnlineBrush = new(Color.FromRgb(76, 175, 80));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromRgb(244, 67, 54));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(255, 193, 7));
    private static readonly SolidColorBrush TabActiveBg = new(Color.FromRgb(60, 60, 60));
    private static readonly SolidColorBrush TabInactiveBg = new(Color.FromRgb(40, 40, 40));

    public EMapView() {
        InitializeComponent();
        _emap = App.Services.GetRequiredService<EMapService>();
        _cameras = App.Services.GetRequiredService<ICameraService>();
        _nav = App.Services.GetRequiredService<INavigationService>();
        _notify = App.Services.GetRequiredService<INotificationService>();
        _talkService = App.Services.GetRequiredService<IAudioTalkService>();
        _emap.Load();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        RebuildFloorTabs();
        RestoreViewState();
    }

    private void RebuildFloorTabs() {
        FloorTabBar.Children.Clear();
        for (var i = 0; i < _emap.Data.Floors.Count; i++) {
            var floor = _emap.Data.Floors[i];
            var isActive = i == _emap.Data.ActiveFloorIndex;
            var btn = new Button {
                Content = floor.Name,
                Tag = i,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 12,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                Cursor = Cursors.Hand,
                Background = isActive ? TabActiveBg : TabInactiveBg,
                Foreground = isActive ? Brushes.White : Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = isActive ? (SolidColorBrush)TryFindResource("PrimaryBrush") ?? Brushes.DodgerBlue : Brushes.Transparent,
                Margin = new Thickness(0, 0, 2, 0),
            };
            btn.Click += FloorTab_Click;
            FloorTabBar.Children.Add(btn);
        }
    }

    private void FloorTab_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: int idx }) {
            _emap.SwitchFloor(idx);
            RebuildFloorTabs();
            RestoreViewState();
        }
    }

    private void RestoreViewState() {
        var floor = _emap.CurrentFloor;
        if (floor is null) return;
        if (!string.IsNullOrEmpty(floor.BackgroundImagePath) && File.Exists(floor.BackgroundImagePath)) {
            LoadBackgroundImage(floor.BackgroundImagePath);
        } else {
            MapImage.Source = null;
        }
        ApplyTransform();
        RebuildCameraIcons();
    }

    private void LoadBackgroundImage(string path) {
        try {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            MapImage.Source = bitmap;
        } catch (Exception ex) {
            _notify.Show($"載入地圖失敗：{ex.Message}", "ERROR");
        }
    }

    private void RebuildCameraIcons() {
        MapContainer.Children.Clear();
        MapContainer.Children.Add(MapImage);

        var floor = _emap.CurrentFloor;
        if (floor is null) return;

        foreach (var cam in _cameras.GetAllCameras()) {
            var pos = floor.Cameras.Find(c => c.CameraId == cam.Id);
            if (pos is null) continue;

            var icon = CreateCameraIcon(cam);
            Canvas.SetLeft(icon, pos.X);
            Canvas.SetTop(icon, pos.Y);
            MapContainer.Children.Add(icon);
        }
    }

    private Border CreateCameraIcon(Camera cam) {
        var isOnline = cam.IsConnected;
        var hasRecording = App.Services.GetRequiredService<IRecordingService>().IsRecording(cam.Id);

        var indicator = new Ellipse {
            Width = 12, Height = 12,
            Fill = isOnline ? (hasRecording ? OnlineBrush : WarningBrush) : OfflineBrush,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameBlock = new TextBlock {
            Text = cam.Name,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };

        var innerStack = new StackPanel { Orientation = Orientation.Horizontal, Children = { indicator, nameBlock } };

        var border = new Border {
            Child = innerStack,
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            BorderBrush = isOnline ? OnlineBrush : OfflineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 8, 3),
            Cursor = Cursors.Hand,
            Tag = cam.Id,
            ToolTip = $"{cam.Name}\n{(isOnline ? "在線" : "離線")}\n錄影: {(hasRecording ? "進行中" : "未啟動")}\nIP: {cam.IpAddress}"
        };

        border.MouseDown += CameraIcon_MouseDown;
        border.MouseLeftButtonUp += CameraIcon_MouseUp;
        border.MouseMove += CameraIcon_MouseMove;
        border.ContextMenu = CreateCameraContextMenu(cam.Id);

        return border;
    }

    private ContextMenu CreateCameraContextMenu(string cameraId) {
        var menu = new ContextMenu();
        var viewItem = new MenuItem { Header = "即時監看" };
        viewItem.Click += (_, _) => _nav.NavigateTo(NavPage.LiveView);
        menu.Items.Add(viewItem);

        var previewItem = new MenuItem { Header = "預覽畫面" };
        previewItem.Click += (_, _) => ShowCameraPreview(cameraId);
        menu.Items.Add(previewItem);

        var removeItem = new MenuItem { Header = "從地圖移除" };
        removeItem.Click += (_, _) => {
            _emap.RemoveCamera(cameraId);
            RebuildCameraIcons();
        };
        menu.Items.Add(removeItem);
        return menu;
    }

    private void ShowCameraPreview(string cameraId) {
        var cam = _cameras.GetCameraById(cameraId);
        if (cam is null) return;

        var player = new Controls.VideoPlayer {
            Width = 400,
            Height = 280,
        };
        player.LoadCamera(cam);

        _previewWindow = new Window {
            Title = $"預覽 — {cam.Name}",
            Content = player,
            Width = 420,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Owner = Window.GetWindow(this),
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResizeWithGrip,
        };
        _previewWindow.Closed += (_, _) => {
            player.DetachCamera();
            _previewWindow = null;
        };
        _previewWindow.Show();
    }

    private void CameraIcon_MouseDown(object sender, MouseButtonEventArgs e) {
        if (sender is not FrameworkElement el) return;

        if (e.ClickCount == 2) {
            if (el.Tag is string camId) {
                ShowCameraPreview(camId);
            }
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        _isDraggingCamera = true;
        _dragTarget = el;
        _dragCameraStart = e.GetPosition(MapContainer);
        _origCameraLeft = Canvas.GetLeft(el);
        _origCameraTop = Canvas.GetTop(el);
        el.CaptureMouse();
    }

    private void CameraIcon_MouseUp(object sender, MouseButtonEventArgs e) {
        if (_isDraggingCamera && _dragTarget is not null) {
            _isDraggingCamera = false;
            _dragTarget.ReleaseMouseCapture();
            var camId = _dragTarget.Tag as string;
            if (camId is not null) {
                _emap.SetCameraPosition(camId, Canvas.GetLeft(_dragTarget), Canvas.GetTop(_dragTarget));
            }
            _dragTarget = null;
        }
    }

    private void CameraIcon_MouseMove(object sender, MouseEventArgs e) {
        if (_isDraggingCamera && _dragTarget is not null && e.LeftButton == MouseButtonState.Pressed) {
            var pos = e.GetPosition(MapContainer);
            var dx = pos.X - _dragCameraStart.X;
            var dy = pos.Y - _dragCameraStart.Y;
            Canvas.SetLeft(_dragTarget, _origCameraLeft + dx);
            Canvas.SetTop(_dragTarget, _origCameraTop + dy);
        }
    }

    private void LoadMap_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFileDialog {
            Filter = "圖片檔案 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有檔案 (*.*)|*.*",
            Title = "選擇地圖圖片"
        };
        if (dialog.ShowDialog() == true) {
            _emap.SetBackground(dialog.FileName);
            LoadBackgroundImage(dialog.FileName);
        }
    }

    private void ClearMap_Click(object sender, RoutedEventArgs e) {
        var floor = _emap.CurrentFloor;
        if (floor is null) return;
        floor.BackgroundImagePath = null;
        floor.Cameras.Clear();
        _emap.Save();
        MapImage.Source = null;
        RebuildCameraIcons();
    }

    private void AddFloor_Click(object sender, RoutedEventArgs e) {
        var dlg = new Dialog.InputDialog("新增樓層", "請輸入樓層名稱：", $"{(char)('A' + _emap.Data.Floors.Count)}F");
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            _emap.AddFloor(dlg.Value.Trim());
            RebuildFloorTabs();
        }
    }

    private void RenameFloor_Click(object sender, RoutedEventArgs e) {
        var floor = _emap.CurrentFloor;
        if (floor is null) return;
        var dlg = new Dialog.InputDialog("重新命名樓層", "請輸入新名稱：", floor.Name);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            _emap.RenameFloor(_emap.Data.ActiveFloorIndex, dlg.Value.Trim());
            RebuildFloorTabs();
        }
    }

    private void RemoveFloor_Click(object sender, RoutedEventArgs e) {
        if (_emap.Data.Floors.Count <= 1) {
            MessageBox.Show("至少需要保留一個樓層", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var floor = _emap.CurrentFloor;
        if (floor is null) return;
        if (MessageBox.Show($"確定刪除樓層「{floor.Name}」？\n該樓層的所有攝影機位置將一併移除。",
                "確認刪除", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
            _emap.RemoveFloor(_emap.Data.ActiveFloorIndex);
            RebuildFloorTabs();
            RestoreViewState();
        }
    }

    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.Source == MapCanvas || e.Source == MapImage) {
            _isDraggingMap = true;
            _dragStart = e.GetPosition(MapCanvas);
            _origOffsetX = _emap.Data.OffsetX;
            _origOffsetY = _emap.Data.OffsetY;
            MapCanvas.CaptureMouse();
        }
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isDraggingMap) {
            _isDraggingMap = false;
            MapCanvas.ReleaseMouseCapture();
            _emap.SetViewState(_emap.Data.ZoomLevel, _emap.Data.OffsetX, _emap.Data.OffsetY);
        }
        if (_isDraggingCamera && _dragTarget is not null) {
            _isDraggingCamera = false;
            _dragTarget.ReleaseMouseCapture();
            _dragTarget = null;
        }
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e) {
        if (_isDraggingMap && e.LeftButton == MouseButtonState.Pressed) {
            var pos = e.GetPosition(MapCanvas);
            var dx = pos.X - _dragStart.X;
            var dy = pos.Y - _dragStart.Y;
            _emap.Data.OffsetX = _origOffsetX + dx;
            _emap.Data.OffsetY = _origOffsetY + dy;
            ApplyTransform();
        }
    }

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e) {
        var zoomFactor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        var newZoom = Math.Clamp(_emap.Data.ZoomLevel * zoomFactor, 0.25, 5.0);

        var mousePos = e.GetPosition(MapCanvas);

        var worldX = (mousePos.X - _emap.Data.OffsetX) / _emap.Data.ZoomLevel;
        var worldY = (mousePos.Y - _emap.Data.OffsetY) / _emap.Data.ZoomLevel;

        _emap.Data.ZoomLevel = newZoom;
        _emap.Data.OffsetX = mousePos.X - worldX * newZoom;
        _emap.Data.OffsetY = mousePos.Y - worldY * newZoom;

        ApplyTransform();
        _emap.Save();
    }

    private void ApplyTransform() {
        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(_emap.Data.OffsetX, _emap.Data.OffsetY));
        group.Children.Add(new ScaleTransform(_emap.Data.ZoomLevel, _emap.Data.ZoomLevel));
        MapContainer.RenderTransform = group;
        MapContainer.RenderTransformOrigin = new Point(0, 0);
    }
}
