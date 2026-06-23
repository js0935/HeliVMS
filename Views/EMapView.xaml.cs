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
    private bool _isDraggingMap;
    private Point _dragStart;
    private double _origOffsetX, _origOffsetY;
    private bool _isDraggingCamera;
    private FrameworkElement? _dragTarget;
    private Point _dragCameraStart;
    private double _origCameraLeft, _origCameraTop;

    private static readonly SolidColorBrush OnlineBrush = new(Color.FromRgb(76, 175, 80));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromRgb(244, 67, 54));

    public EMapView() {
        InitializeComponent();
        _emap = App.Services.GetRequiredService<EMapService>();
        _cameras = App.Services.GetRequiredService<ICameraService>();
        _nav = App.Services.GetRequiredService<INavigationService>();
        _notify = App.Services.GetRequiredService<INotificationService>();
        _emap.Load();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        RestoreViewState();
    }

    private void RestoreViewState() {
        if (!string.IsNullOrEmpty(_emap.Data.BackgroundImagePath) && File.Exists(_emap.Data.BackgroundImagePath)) {
            LoadBackgroundImage(_emap.Data.BackgroundImagePath);
        }
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

        foreach (var cam in _cameras.GetAllCameras()) {
            var pos = _emap.Data.Cameras.Find(c => c.CameraId == cam.Id);
            if (pos is null) continue;

            var icon = CreateCameraIcon(cam);
            Canvas.SetLeft(icon, pos.X);
            Canvas.SetTop(icon, pos.Y);
            MapContainer.Children.Add(icon);
        }
    }

    private Border CreateCameraIcon(Camera cam) {
        var isOnline = cam.IsConnected;

        var indicator = new Ellipse {
            Width = 12, Height = 12,
            Fill = isOnline ? OnlineBrush : OfflineBrush,
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
            ToolTip = $"{cam.Name}\n{(isOnline ? "在線" : "離線")}\nIP: {cam.IpAddress}"
        };

        border.MouseDown += CameraIcon_MouseDown;
        border.MouseLeftButtonUp += CameraIcon_MouseUp;
        border.MouseMove += CameraIcon_MouseMove;
        border.ContextMenu = CreateCameraContextMenu(cam.Id);

        return border;
    }

    private ContextMenu CreateCameraContextMenu(string cameraId) {
        var menu = new ContextMenu();
        var removeItem = new MenuItem { Header = "從地圖移除" };
        removeItem.Click += (_, _) => {
            _emap.RemoveCamera(cameraId);
            RebuildCameraIcons();
        };
        menu.Items.Add(removeItem);
        return menu;
    }

    private void CameraIcon_MouseDown(object sender, MouseButtonEventArgs e) {
        if (sender is not FrameworkElement el) return;

        if (e.ClickCount == 2) {
            _nav.NavigateTo(NavPage.LiveView);
            return;
        }

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
        _emap.Data.BackgroundImagePath = null;
        _emap.Data.Cameras.Clear();
        _emap.Save();
        MapImage.Source = null;
        RebuildCameraIcons();
    }

    // --- Map Pan & Zoom ---
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
