using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 電子地圖（M41，§16.1）：樓層切換、平面圖＋圖釘（camera 扇形視角／io 狀態點）、
/// 事件閃爍、警報列表雙向定位。
/// </summary>
public partial class MapWindow : Window
{
    private readonly SqliteStore _store;
    private readonly MapRepository _maps;
    private readonly IoRepository _io;
    private readonly AlarmEventRepository _events;
    private readonly Dictionary<int, string> _cameraNames = new();
    private readonly Dictionary<int, (int? CameraId, string Name)> _ioNames = new();

    private sealed record PinView(FrameworkElement Root, Shape Shape, string Type, int? LookupChannel);
    private readonly List<PinView> _pins = new();

    private readonly DispatcherTimer _flashTimer;
    private int _flashTicks;
    private PinView? _flashPin;
    private bool _flashOn;

    private readonly DispatcherTimer _eventTimer;
    private bool _panning;
    private Point _panStart;
    private double _panH;
    private double _panV;
    private double _zoom = 1.0;
    private double _scaleMPerPx;

    /// <summary>待定位（開窗時由事件中心呼叫）。</summary>
    private int? _locateChannelId;
    private string _locateType = "camera";

    private static readonly Color ColorGreen = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color ColorOrange = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color ColorGray = Color.FromRgb(0x6B, 0x7A, 0x90);

    public MapWindow(SqliteStore store, int? locateChannelId = null, string locateDeviceType = "camera")
    {
        _store = store;
        _maps = new MapRepository(store);
        _io = new IoRepository(store);
        _events = new AlarmEventRepository(store);
        _locateChannelId = locateChannelId;
        _locateType = locateDeviceType;

        foreach (var c in new ChannelRepository(store).List())
        {
            _cameraNames[c.Id] = c.Name;
        }

        foreach (var c in _io.ListChannels())
        {
            _ioNames[c.Id] = (c.CameraId, c.Name);
        }

        InitializeComponent();

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _flashTimer.Tick += OnFlashTick;

        _eventTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _eventTimer.Tick += (_, _) => RefreshEventColors();
        _eventTimer.Start();

        MapScroll.PreviewMouseLeftButtonDown += OnPanStart;
        MapScroll.PreviewMouseMove += OnPanMove;
        MapScroll.PreviewMouseLeftButtonUp += (_, _) => _panning = false;

        ReloadMaps();
    }

    /// <summary>事件中心定位：切換至該通道所在樓層並閃爍圖釘。</summary>
    public void LocateChannel(int channelId, string deviceType)
    {
        _locateChannelId = channelId;
        _locateType = deviceType;
        var mapId = deviceType == "io"
            ? _maps.FindMapByChannel(channelId, "io")
            : _maps.FindMapByChannel(channelId, "camera");
        if (mapId is null)
        {
            _locateChannelId = null;
            MapStatusText.Text = deviceType == "io"
                ? $"IO 通道 #{channelId} 未放置於任何地圖。"
                : $"頻道 #{channelId} 未放置於任何地圖。";
            return;
        }

        foreach (var item in MapCombo.Items)
        {
            if (item is ComboBoxItem ci && ci.Tag is int id && id == mapId.Value)
            {
                MapCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void ReloadMaps()
    {
        MapCombo.Items.Clear();
        foreach (var m in _maps.ListMaps())
        {
            if (!m.Enabled)
            {
                continue;
            }

            MapCombo.Items.Add(new ComboBoxItem { Content = $"{m.Name}（#{m.Id}）", Tag = m.Id });
        }

        if (_locateChannelId is int ci)
        {
            var mapId = _locateType == "io"
                ? _maps.FindMapByChannel(ci, "io")
                : _maps.FindMapByChannel(ci, "camera");
            if (mapId is int mid)
            {
                foreach (var item in MapCombo.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Tag is int id && id == mid)
                    {
                        MapCombo.SelectedItem = item;
                        return;
                    }
                }
            }
        }

        if (MapCombo.Items.Count > 0)
        {
            MapCombo.SelectedIndex = 0;
        }
        else
        {
            MapStatusText.Text = "尚未建立任何地圖（設定中心 → 地圖）。";
        }
    }

    private void OnMapComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not int mapId)
        {
            return;
        }

        ShowMap(mapId);
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        ReloadMaps();
    }

    private void ShowMap(int mapId)
    {
        var map = _maps.GetMap(mapId);
        if (map is null)
        {
            MapStatusText.Text = "地圖不存在（可能已被刪除）。";
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(map.ImagePath, UriKind.RelativeOrAbsolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
        }
        catch (Exception ex)
        {
            MapStatusText.Text = $"無法載入圖檔：{map.ImagePath}（{ex.Message}）";
            return;
        }

        MapImage.Source = bitmap;
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        MapCanvas.Width = w;
        MapCanvas.Height = h;
        MapImage.Width = w;
        MapImage.Height = h;

        _zoom = 1.0;
        ApplyZoom();

        MapStatusText.Text = $"{map.Name} · {w}×{h}";

        _scaleMPerPx = map.ScaleMPerPx;
        UpdateScaleText(mapId, map);

        LoadPins(mapId);

        Dispatcher.BeginInvoke(() =>
        {
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, (w - MapScroll.ViewportWidth) / 2));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, (h - MapScroll.ViewportHeight) / 2));
        });

        if (_locateChannelId is int ci)
        {
            var id = ci;
            var type = _locateType;
            _locateChannelId = null;
            FlashPinFor(id, type);
        }
    }

    private void UpdateScaleText(int mapId, MapRecord map)
    {
        var cam = _maps.ListDevices(mapId)
            .FirstOrDefault(x => x.DeviceType == "camera" && x.Enabled);
        var radius = cam is null
            ? MapGeometry.DefaultSectorRadiusPixels
            : MapGeometry.SectorRadiusPixels(cam.FovDepth, map.ScaleMPerPx);
        MapScaleText.Text = $"比例：{MapGeometry.ScaleLabel(map.ScaleMPerPx)}｜扇形半徑：{radius:0.#} px";
    }

    private void LoadPins(int mapId)
    {
        foreach (var p in _pins)
        {
            MapCanvas.Children.Remove(p.Root);
        }

        _pins.Clear();
        _flashTimer.Stop();
        _flashPin = null;

        var recent = _events.ListByRange(null, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow)
            .Select(x => x.ChannelId)
            .ToHashSet();

        foreach (var d in _maps.ListDevices(mapId))
        {
            if (!d.Enabled)
            {
                continue;
            }

            PinView pin;
            string name;
            if (d.DeviceType == "camera")
            {
                _cameraNames.TryGetValue(d.ChannelId, out var cn);
                name = cn ?? $"頻道 #{d.ChannelId}";
                pin = CreateCameraPin(d, name, recent.Contains(d.ChannelId));
            }
            else
            {
                _ioNames.TryGetValue(d.ChannelId, out var io);
                var camId = io.CameraId;
                name = string.IsNullOrEmpty(io.Name) ? $"IO #{d.ChannelId}" : io.Name;
                pin = CreateIoPin(d, name, camId is int cc && recent.Contains(cc), camId);
            }

            _pins.Add(pin);
            MapCanvas.Children.Add(pin.Root);
        }
    }

    private PinView CreateCameraPin(MapDeviceRecord d, string name, bool hasEvent)
    {
        var cx = d.X * MapCanvas.Width;
        var cy = d.Y * MapCanvas.Height;

        var radius = MapGeometry.SectorRadiusPixels(d.FovDepth, _scaleMPerPx);
        var sector = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(70, 245, 158, 11)),
            Stroke = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11)),
            StrokeThickness = 1,
            Data = CreateSector(cx, cy, d.Angle, d.FovDeg, radius),
        };
        Canvas.SetLeft(sector, 0);
        Canvas.SetTop(sector, 0);

        var dot = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(hasEvent ? ColorOrange : ColorGreen),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(dot, cx - 7);
        Canvas.SetTop(dot, cy - 7);

        var root = new Grid { Width = 14, Height = 14 };
        Canvas.SetLeft(root, cx - 7);
        Canvas.SetTop(root, cy - 7);
        root.ToolTip = $"{name} · {MapGeometry.Bearing(d.Angle)} {d.Angle:0.#}°／FOV {d.FovDeg:0.#}°／深度 {d.FovDepth:0.#} m";
        root.Tag = $"camera:{d.ChannelId}";
        root.MouseLeftButtonUp += OnPinClick;
        root.Children.Add(sector);
        root.Children.Add(dot);

        return new PinView(root, dot, "camera", d.ChannelId);
    }

    private PinView CreateIoPin(MapDeviceRecord d, string name, bool hasEvent, int? cameraId)
    {
        var cx = d.X * MapCanvas.Width;
        var cy = d.Y * MapCanvas.Height;

        var box = new Rectangle
        {
            Width = 14,
            Height = 14,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(hasEvent ? ColorOrange : ColorGray),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(box, cx - 7);
        Canvas.SetTop(box, cy - 7);

        var root = new Grid { Width = 14, Height = 14 };
        Canvas.SetLeft(root, cx - 7);
        Canvas.SetTop(root, cy - 7);
        root.ToolTip = $"[IO] {name}";
        root.Tag = $"io:{d.ChannelId}";
        root.MouseLeftButtonUp += OnPinClick;
        root.Children.Add(box);

        return new PinView(root, box, "io", cameraId);
    }

    private void OnPinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag)
        {
            return;
        }

        if (e.ClickCount >= 2 && tag.StartsWith("camera:", StringComparison.Ordinal))
        {
            var channelId = int.Parse(tag.AsSpan("camera:".Length));
            var playback = new PlaybackWindow(_store, channelId) { Owner = this };
            playback.Show();
            return;
        }

        MapStatusText.Text = fe.ToolTip as string ?? tag;
        e.Handled = true;
    }

    private static Geometry CreateSector(double cx, double cy, double angleDeg, double fovDeg, double radius)
    {
        double ToRad(double d) => d * Math.PI / 180.0;
        var p0 = new Point(cx, cy);
        var a1 = ToRad(angleDeg - fovDeg / 2);
        var a2 = ToRad(angleDeg + fovDeg / 2);
        var p1 = new Point(cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1));
        var p2 = new Point(cx + radius * Math.Cos(a2), cy + radius * Math.Sin(a2));

        var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(p1, true));
        fig.Segments.Add(new ArcSegment(
            p2,
            new Size(radius, radius),
            0,
            fovDeg > 180,
            SweepDirection.Clockwise,
            true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>重新依最近事件上色（不重建圖釘）。</summary>
    private void RefreshEventColors()
    {
        if (_pins.Count == 0)
        {
            return;
        }

        var recent = _events.ListByRange(null, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow)
            .Select(x => x.ChannelId)
            .ToHashSet();
        foreach (var pin in _pins)
        {
            if (ReferenceEquals(pin, _flashPin))
            {
                continue;
            }

            var active = pin.LookupChannel is int lk && recent.Contains(lk);
            var color = pin.Type == "camera"
                ? (active ? ColorOrange : ColorGreen)
                : (active ? ColorOrange : ColorGray);
            pin.Shape.Fill = new SolidColorBrush(color);
        }
    }

    private void FlashPinFor(int channelId, string type)
    {
        if (type == "io")
        {
            _flashPin = _pins.FirstOrDefault(p => p.Root.Tag is string t && t == $"io:{channelId}");
        }
        else
        {
            _flashPin = _pins.FirstOrDefault(p => p.Root.Tag is string t && t == $"camera:{channelId}")
                        ?? _pins.FirstOrDefault(p => p.Type == "io" && p.LookupChannel == channelId);
        }

        if (_flashPin is null)
        {
            MapStatusText.Text = "此裝置未放置於目前地圖或樓層。";
            return;
        }

        _flashTicks = 0;
        _flashOn = false;
        _flashTimer.Start();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashTicks++;
        if (_flashTicks > 12)
        {
            _flashTimer.Stop();
            _flashPin = null;
            RefreshEventColors();
            return;
        }

        if (_flashPin is null)
        {
            return;
        }

        _flashOn = !_flashOn;
        _flashPin.Shape.Fill = new SolidColorBrush(_flashOn ? Colors.White : ColorOrange);
    }

    private void OnScrollWheel(object sender, MouseWheelEventArgs e)
    {
        const double step = 1.15;
        var d = e.Delta > 0 ? step : 1.0 / step;
        var nz = Math.Clamp(_zoom * d, 0.2, 8.0);
        if (Math.Abs(nz - _zoom) < 0.01)
        {
            return;
        }

        _zoom = nz;
        ApplyZoom();
        e.Handled = true;
    }

    private void ApplyZoom()
    {
        var c = new Point(MapCanvas.Width / 2, MapCanvas.Height / 2);
        MapCanvas.RenderTransform = new ScaleTransform(_zoom, _zoom, c.X, c.Y);
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Image or Border or Canvas)
        {
            _panning = true;
            _panStart = e.GetPosition(this);
            _panH = MapScroll.HorizontalOffset;
            _panV = MapScroll.VerticalOffset;
            e.Handled = true;
        }
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var p = e.GetPosition(this);
        MapScroll.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
        MapScroll.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
        e.Handled = true;
    }
}