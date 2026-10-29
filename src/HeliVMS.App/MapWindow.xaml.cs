using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.App.Services;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 電子地圖（M41，§16.1）：樓層切換、平面圖＋圖釘（camera 扇形視角／io 狀態點）、
/// 事件閃爍、警報列表雙向定位。
/// </summary>
public partial class MapWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelManager? _manager;
    private readonly MapRepository _maps;
    private readonly IoRepository _io;
    private readonly AlarmEventRepository _events;
    private readonly Dictionary<int, string> _cameraNames = new();
    private readonly Dictionary<int, (int? CameraId, string Name)> _ioNames = new();

    /// <summary>
    /// 地圖上的一個圖釘。座標存的是「佔圖片寬／高的比例」（0–1），不是像素：縮放或改變視窗
    /// 大小時只要重算像素位置，不必重讀資料庫、也不必重建控制項（否則拖動視窗邊框時會一直
    /// 重新查事件表，畫面會頓）。
    /// </summary>
    /// <param name="Root">畫布上的圖釘本體（含扇形與圓點）。</param>
    /// <param name="Shape">圓點或方塊本身；事件上色與閃爍都改它的填色。</param>
    /// <param name="Type"><c>camera</c> 或 <c>io</c>。</param>
    /// <param name="LookupChannel">這顆圖釘對應的事件頻道（IO 為其綁定的攝影機）。</param>
    /// <param name="X">水平位置，圖片寬度的比例（0–1）。</param>
    /// <param name="Y">垂直位置，圖片高度的比例（0–1）。</param>
    /// <param name="Sector">攝影機的水平涵蓋扇形；IO 圖釘沒有。</param>
    /// <param name="Angle">攝影機方位角（度）。</param>
    /// <param name="FovDeg">水平視野角（度）。</param>
    /// <param name="RadiusPx">扇形半徑（原始圖片像素）。顯示時要再乘上縮放比例。</param>
    private sealed record PinView(
        FrameworkElement Root,
        Shape Shape,
        string Type,
        int? LookupChannel,
        double X,
        double Y,
        Path? Sector = null,
        double Angle = 0,
        double FovDeg = 0,
        double RadiusPx = 0);

    private readonly List<PinView> _pins = new();

    /// <summary>熱點徽章與脈動圓環。同樣存比例座標，縮放時跟著重排。</summary>
    private sealed record HotspotView(Ellipse Ring, FrameworkElement Badge, double X, double Y);

    private readonly List<HotspotView> _hotspots = new();
    private readonly DispatcherTimer _pulseTimer;
    private bool _pulseOn;
    private int _currentMapId;

    /// <summary>目前這張平面圖檔的原始像素尺寸（不是畫面顯示的大小）。</summary>
    private double _imageW;
    private double _imageH;

    /// <summary>
    /// 是否維持「整張圖剛好塞進視窗」。預設開啟；操作員自己滾輪放大後就交還控制權，
    /// 免得他一調整視窗大小，辛苦拉近的那一角又被縮回去。
    /// </summary>
    private bool _fitToWindow = true;

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

    /// <param name="store">資料庫連線（地圖、頻道與事件都由此讀取）。</param>
    /// <param name="locateChannelId">開啟時要定位並閃爍的頻道；事件中心雙擊時帶入。</param>
    /// <param name="locateDeviceType">定位目標型別（<c>camera</c> 或 <c>io</c>）。</param>
    /// <param name="manager">
    /// 主視窗的頻道管理器（可為 null）。圖釘單擊要開即時畫面時會優先重用它的監看連線，
    /// 沒有管理器（例如由事件中心開啟地圖）才由 <see cref="LiveWindow"/> 自建連線。
    /// </param>
    public MapWindow(
        SqliteStore store,
        int? locateChannelId = null,
        string locateDeviceType = "camera",
        ChannelManager? manager = null)
    {
        _store = store;
        _manager = manager;
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
        ApplyI18n();

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _flashTimer.Tick += OnFlashTick;

        _eventTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _eventTimer.Tick += (_, _) =>
        {
            RefreshEventColors();
            if (_currentMapId > 0)
            {
                RefreshHotspots(_currentMapId);
            }
        };
        _eventTimer.Start();

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _pulseTimer.Tick += OnPulseTick;

        MapScroll.PreviewMouseLeftButtonDown += OnPanStart;
        MapScroll.PreviewMouseMove += OnPanMove;
        MapScroll.PreviewMouseLeftButtonUp += (_, _) => _panning = false;

        // 視窗大小一變就要重算「整張圖」的比例：這是需求的重點——平面圖不該需要操作員
        // 自己拉捲軸才看得到全貌。視窗剛開、版面還沒量好時 ViewportWidth 是 0，
        // 靠這個事件補上第一次的縮放。
        MapScroll.SizeChanged += (_, _) => ApplyFitIfNeeded();

        ReloadMaps();
    }

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Map.Title");
        MapFloorLabel.Text = Localizer.T("Map.Floor");
        MapRefreshButton.Content = Localizer.T("Map.Refresh");
        MapRefreshButton.ToolTip = Localizer.T("Map.RefreshTip");
        MapHintText.Text = Localizer.T("Map.Hint");
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
                ? string.Format(CultureInfo.InvariantCulture, Localizer.T("Map.IoNotPlaced"), channelId)
                : string.Format(CultureInfo.InvariantCulture, Localizer.T("Map.ChannelNotPlaced"), channelId);
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
            MapStatusText.Text = Localizer.T("Map.NoMaps");
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
            MapStatusText.Text = Localizer.T("Map.Gone");
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
            MapStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Map.LoadFailed"), map.ImagePath, ex.Message);
            return;
        }

        MapImage.Source = bitmap;
        _imageW = bitmap.PixelWidth;
        _imageH = bitmap.PixelHeight;

        // 圖片以「顯示尺寸」放進畫布，而不是以原始像素放進去再套 RenderTransform：
        // RenderTransform 不會改變 ScrollViewer 認知的內容大小，縮小後會留下一大片可捲動的
        // 空白、放大後又捲不到右邊。這裡直接改版面尺寸，捲軸範圍才會正確。
        MapImage.Stretch = Stretch.Fill;
        MapImage.HorizontalAlignment = HorizontalAlignment.Left;
        MapImage.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(MapImage, 0);
        Canvas.SetTop(MapImage, 0);

        // 大幅縮小的平面圖用最近鄰取樣會滿是鋸齒，門牌與通道編號會看不清楚。
        RenderOptions.SetBitmapScalingMode(MapImage, BitmapScalingMode.HighQuality);

        _currentMapId = mapId;
        _fitToWindow = true;
        _zoom = FitScale();
        ApplyZoom();

        MapStatusText.Text = $"{map.Name} · {bitmap.PixelWidth}×{bitmap.PixelHeight}";

        _scaleMPerPx = map.ScaleMPerPx;
        UpdateScaleText(mapId, map);

        LoadPins(mapId);

        if (_locateChannelId is int ci)
        {
            var id = ci;
            var type = _locateType;
            _locateChannelId = null;
            FlashPinFor(id, type);
        }
    }

    /// <summary>
    /// 讓整張平面圖剛好塞進視窗（等比、不變形）。視窗還沒量好時先沿用目前的縮放，
    /// 等 <see cref="FrameworkElement.SizeChanged"/> 再算一次。
    /// </summary>
    private void ApplyFitIfNeeded()
    {
        if (!_fitToWindow || _imageW <= 0 || _imageH <= 0)
        {
            return;
        }

        var fit = FitScale();
        if (Math.Abs(fit - _zoom) < 0.0005)
        {
            return;
        }

        _zoom = fit;
        ApplyZoom();
    }

    /// <summary>
    /// 「整張圖都看得到」的縮放比例。
    /// </summary>
    /// <remarks>
    /// 上限刻意是 1.0：比原始尺寸還大的圖只會變模糊，而且圖釘會離它標示的點愈遠。
    /// 圖片比視窗小時就維持原尺寸，不放大。
    /// </remarks>
    private double FitScale()
    {
        if (_imageW <= 0 || _imageH <= 0)
        {
            return 1.0;
        }

        var vw = MapScroll.ViewportWidth;
        var vh = MapScroll.ViewportHeight;
        if (vw < 32 || vh < 32)
        {
            return Math.Min(1.0, _zoom);
        }

        // 留 2px：算得「剛剛好」時捲軸會因為次像素捨入而冒出來，一冒出來又把可用寬度吃掉，
        // 於是縮放比例在兩個值之間來回跳。
        var scale = Math.Min((vw - 2) / _imageW, (vh - 2) / _imageH);
        return Math.Clamp(scale, 0.02, 1.0);
    }

    private void UpdateScaleText(int mapId, MapRecord map)
    {
        var cam = _maps.ListDevices(mapId)
            .FirstOrDefault(x => x.DeviceType == "camera" && x.Enabled);
        var radius = cam is null
            ? MapGeometry.DefaultSectorRadiusPixels
            : MapGeometry.SectorRadiusPixels(cam.FovDepth, map.ScaleMPerPx);
        MapScaleText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("Map.Scale"),
            MapGeometry.ScaleLabel(map.ScaleMPerPx, Localizer.Lang),
            radius.ToString("0.#", CultureInfo.InvariantCulture));
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

            PinView pin;
            string name;
            var dt = d.DeviceType?.ToLowerInvariant() ?? "camera";
            if (dt == "camera")
            {
                _cameraNames.TryGetValue(d.ChannelId, out var cn);
                name = cn ?? string.Format(CultureInfo.InvariantCulture, Localizer.T("Map.ChannelFallback"), d.ChannelId);
                pin = CreateCameraPin(d, name, recent.Contains(d.ChannelId));
            }
            else
            {
                _ioNames.TryGetValue(d.ChannelId, out var io);
                var camId = io.CameraId;
                name = string.IsNullOrEmpty(io.Name)
                    ? string.Format(CultureInfo.InvariantCulture, Localizer.T("Map.IoFallback"), d.ChannelId)
                    : io.Name;
                pin = CreateIoPin(d, name, camId is int cc && recent.Contains(cc), camId);
            }

            _pins.Add(pin);
            try { MapCanvas.Children.Add(pin.Root); } catch { }
        }

        // 圖釘一律由 LayoutPins 定位：建立與縮放共用同一段座標換算，才不會兩邊各算一次而漂移。
        LayoutPins();

        RefreshHotspots(mapId);
    }

    private PinView CreateCameraPin(MapDeviceRecord d, string name, bool hasEvent)
    {
        var radius = MapGeometry.SectorRadiusPixels(d.FovDepth, _scaleMPerPx);
        var sector = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(70, 245, 158, 11)),
            Stroke = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11)),
            StrokeThickness = 1,
        };

        var dot = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = new SolidColorBrush(hasEvent ? ColorOrange : ColorGreen),
            Stroke = Brushes.White,
            StrokeThickness = 2,
        };

        var root = new Grid { Width = 18, Height = 18 };
        root.ToolTip = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("Map.CameraTip"),
            name,
            MapGeometry.Bearing(d.Angle, Localizer.Lang),
            d.Angle,
            d.FovDeg,
            d.FovDepth);
        root.Tag = $"camera:{d.ChannelId}";
        root.MouseLeftButtonUp += OnPinLiveClick;
        root.MouseRightButtonUp += OnPinPlaybackClick;
        root.Children.Add(sector);
        root.Children.Add(dot);

        return new PinView(root, dot, "camera", d.ChannelId, d.X, d.Y, sector, d.Angle, d.FovDeg, radius);
    }

    private PinView CreateIoPin(MapDeviceRecord d, string name, bool hasEvent, int? cameraId)
    {
        var box = new Rectangle
        {
            Width = 18,
            Height = 18,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new SolidColorBrush(hasEvent ? ColorOrange : ColorGray),
            Stroke = Brushes.White,
            StrokeThickness = 2,
        };

        var root = new Grid { Width = 18, Height = 18 };
        root.ToolTip = string.Format(CultureInfo.InvariantCulture, Localizer.T("Map.IoTip"), name);
        root.Tag = $"io:{d.ChannelId}";
        root.MouseLeftButtonUp += OnPinLiveClick;
        root.MouseRightButtonUp += OnPinPlaybackClick;
        root.Children.Add(box);

        return new PinView(root, box, "io", cameraId, d.X, d.Y);
    }

    /// <summary>
    /// 依目前畫布大小重新擺放圖釘（與攝影機的涵蓋扇形）。
    /// </summary>
    /// <remarks>
    /// 座標存的是比例，乘上畫布尺寸就是像素位置。圖釘本身維持 18×18 不隨縮放變小：
    /// 一張 4000px 的平面圖塞進視窗大約是 0.3 倍，若圖釘也跟著縮，全部只剩 5px 的點，
    /// 反而比原本要拉捲軸更難點。
    /// </remarks>
    private void LayoutPins()
    {
        var w = MapCanvas.Width;
        var h = MapCanvas.Height;

        foreach (var p in _pins)
        {
            var cx = p.X * w;
            var cy = p.Y * h;
            Canvas.SetLeft(p.Root, cx - 9);
            Canvas.SetTop(p.Root, cy - 9);

            if (p.Sector is { } sector)
            {
                // 扇形代表「這台攝影機實際拍得到多大範圍」，必須跟著圖一起縮放。
                sector.Data = CreateSector(cx, cy, p.Angle, p.FovDeg, p.RadiusPx * _zoom);
            }
        }
    }

    /// <summary>
    /// 圖釘單擊：攝影機開該點位的即時畫面，IO 則在狀態列顯示名稱（IO 本身沒有影像）。
    /// </summary>
    /// <remarks>
    /// 原本單一 <c>OnPinClick</c> 掛在 <c>MouseLeftButtonUp</c> 上再判斷 <c>e.RightButton</c>，
    /// 但左鍵事件永遠不會帶右鍵狀態，於是「右鍵回放」實際上是死碼——操作員按右鍵只會看到
    /// 一行文字。左右鍵因此拆成兩個獨立處理常式。
    ///
    /// <para>
    /// 開窗一律走 <see cref="LiveWindow.ShowFor"/>：同一顆圖釘被連點時只會把已開著的視窗提到
    /// 最前面。直接 <c>new LiveWindow(...)</c> 會讓同一個頻道疊出好幾個一模一樣的小視窗，
    /// 每個都各自拉一條 RTSP 連線——操作員看到的卻是「怎麼點都只多一個蓋在上面」。
    /// </para>
    /// </remarks>
    private void OnPinLiveClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag)
        {
            return;
        }

        if (tag.StartsWith("camera:", StringComparison.Ordinal))
        {
            var channelId = int.Parse(tag.AsSpan("camera:".Length));
            try
            {
                LiveWindow.ShowFor(_store, channelId, _manager, this);
            }
            catch
            {
                // 即時畫面開不起來（頻道被刪、解碼器缺失）時退回回放，至少看得到該點位的錄影。
                OpenPlayback(channelId);
            }

            e.Handled = true;
            return;
        }

        MapStatusText.Text = fe.ToolTip as string ?? tag;
        e.Handled = true;
    }

    /// <summary>
    /// 圖釘右鍵：開此點位的錄影回放。IO 圖釘會回放它綁定的攝影機（未綁定時僅顯示名稱）。
    /// </summary>
    private void OnPinPlaybackClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        var channelId = fe.Tag is string tag && tag.StartsWith("camera:", StringComparison.Ordinal)
            ? int.Parse(tag.AsSpan("camera:".Length))
            : _pins.FirstOrDefault(p => ReferenceEquals(p.Root, fe))?.LookupChannel;

        if (channelId is int id)
        {
            OpenPlayback(id);
        }
        else
        {
            MapStatusText.Text = fe.ToolTip as string ?? string.Empty;
        }

        e.Handled = true;
    }

    private void OpenPlayback(int channelId)
    {
        try
        {
            var playback = new PlaybackWindow(_store, channelId) { Owner = this };
            playback.Show();
        }
        catch (Exception ex)
        {
            MapStatusText.Text = ex.Message;
        }
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

    /// <summary>重繪事件熱點（M82，§16 強化②）：依 MapEventAggregator 在圖釘上加計數徽章＋脈動圓環。</summary>
    private void RefreshHotspots(int mapId)
    {
        foreach (var h in _hotspots)
        {
            MapCanvas.Children.Remove(h.Badge);
            MapCanvas.Children.Remove(h.Ring);
        }

        _hotspots.Clear();
        _pulseTimer.Stop();
        _pulseOn = false;

        var nowUtc = DateTime.UtcNow;
        var pins = _maps.ListDevices(mapId);
        if (pins.Count == 0)
        {
            _pulseTimer.Stop();
            return;
        }

        var samples = new List<MapEventSample>();
        foreach (var ev in _events.ListByRange(null, nowUtc.AddMinutes(-30), nowUtc))
        {
            samples.Add(new MapEventSample(ev.ChannelId, ev.EventType, new DateTimeOffset(ev.StartUtc, TimeSpan.Zero)));
        }

        foreach (var hs in MapEventAggregator.Compute(pins, samples, new DateTimeOffset(nowUtc, TimeSpan.Zero)))
        {
            if (hs.Count <= 0)
            {
                continue;
            }

            var cx = hs.X * MapCanvas.Width;
            var cy = hs.Y * MapCanvas.Height;

            var ring = new Ellipse
            {
                Width = 28,
                Height = 28,
                Stroke = new SolidColorBrush(hs.Hot ? Color.FromRgb(0xE5, 0x39, 0x35) : ColorOrange),
                StrokeThickness = 2,
                Opacity = 0.35,
            };
            MapCanvas.Children.Add(ring);

            var kinds = string.Join("/", hs.DistinctKinds);
            var countText = new TextBlock
            {
                Text = hs.Count.ToString(),
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            };
            AutomationProperties.SetAutomationId(countText, $"MapHotspotBadge_{hs.ChannelId}");
            AutomationProperties.SetName(countText, $"HOT:{hs.ChannelId}:{hs.Count}:{kinds}:{hs.Hot}");

            var label = hs.Hot ? Localizer.T("Map.Hotspot") : Localizer.T("Map.Coldspot");
            var badge = new Border
            {
                Background = new SolidColorBrush(hs.Hot
                    ? Color.FromRgb(0xE5, 0x39, 0x35)
                    : Color.FromArgb(210, 0x8A, 0x2B, 0xE4)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(5, 1, 5, 2),
                ToolTip = string.Format(
                    CultureInfo.InvariantCulture, Localizer.T("Map.HotspotTip"), hs.Count, kinds, label),
                Child = countText,
            };
            Canvas.SetLeft(badge, cx + 10);
            Canvas.SetTop(badge, cy - 18);
            MapCanvas.Children.Add(badge);
            _hotspots.Add(new HotspotView(ring, badge, hs.X, hs.Y));
        }

        LayoutHotspots();

        if (_hotspots.Count > 0)
        {
            _pulseTimer.Start();
        }
    }

    /// <summary>依目前畫布大小重新擺放熱點圓環與計數徽章（尺寸固定，只有位置跟著縮放）。</summary>
    private void LayoutHotspots()
    {
        var w = MapCanvas.Width;
        var h = MapCanvas.Height;

        foreach (var spot in _hotspots)
        {
            var cx = spot.X * w;
            var cy = spot.Y * h;
            Canvas.SetLeft(spot.Ring, cx - 14);
            Canvas.SetTop(spot.Ring, cy - 14);
            Canvas.SetLeft(spot.Badge, cx + 10);
            Canvas.SetTop(spot.Badge, cy - 18);
        }
    }

    private void OnPulseTick(object? sender, EventArgs e)
    {
        _pulseOn = !_pulseOn;
        var opacity = _pulseOn ? 0.85 : 0.2;
        foreach (var spot in _hotspots)
        {
            spot.Ring.Opacity = opacity;
        }
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
            MapStatusText.Text = Localizer.T("Map.NotOnMap");
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

        // 縮小的下限就是「整張圖」：再縮下去只是讓圖變得更小、旁邊空一大片。
        // 縮到剛好全圖時就自動回到「跟隨視窗」模式，視窗一拉大又會重新填滿。
        var minZoom = FitScale();
        var nz = Math.Clamp(_zoom * d, minZoom, 8.0);
        if (Math.Abs(nz - _zoom) < 0.001)
        {
            return;
        }

        _zoom = nz;
        _fitToWindow = nz <= minZoom + 0.001;
        ApplyZoom();
        e.Handled = true;
    }

    /// <summary>
    /// 套用目前的縮放：改畫布的版面尺寸（不是 RenderTransform），並重排圖釘與熱點。
    /// </summary>
    /// <remarks>
    /// 用 RenderTransform 縮放整張畫布很省事，但 ScrollViewer 認定的內容大小不會跟著變：
    /// 縮小時會留下一大片可捲動的空白，放大時又捲不到右邊那半張圖。改版面尺寸則兩者都正確，
    /// 代價是圖釘位置必須自己重算（<see cref="LayoutPins"/>）。
    /// </remarks>
    private void ApplyZoom()
    {
        var vw = MapScroll.ViewportWidth;
        var vh = MapScroll.ViewportHeight;

        // 以「目前視窗中心」為錨點：記住中心落在圖上的比例，縮放後把捲軸移回去。
        // 少了這段，每次滾輪都會把畫面甩回左上角，放大等於重新找路。
        var fx = MapCanvas.Width > 1 ? (MapScroll.HorizontalOffset + vw / 2) / MapCanvas.Width : 0.5;
        var fy = MapCanvas.Height > 1 ? (MapScroll.VerticalOffset + vh / 2) / MapCanvas.Height : 0.5;

        var w = Math.Max(1, _imageW * _zoom);
        var h = Math.Max(1, _imageH * _zoom);
        MapCanvas.Width = w;
        MapCanvas.Height = h;
        MapImage.Width = w;
        MapImage.Height = h;

        LayoutPins();
        LayoutHotspots();

        if (vw < 32 || vh < 32)
        {
            return;
        }

        // 捲軸範圍要等版面跑完才更新，先 UpdateLayout 再捲，否則會被夾在舊範圍內。
        MapScroll.UpdateLayout();
        MapScroll.ScrollToHorizontalOffset(fx * w - vw / 2);
        MapScroll.ScrollToVerticalOffset(fy * h - vh / 2);
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (Image or Border or Canvas))
        {
            return;
        }

        // 雙擊空白處＝回到「整張圖」。刻意接在這裡而不是 MouseDoubleClick：預覽事件一旦
        // 標記 Handled，控制項的 MouseDoubleClick 不一定還會被叫到，而拖曳起點是必經之路。
        if (e.ClickCount == 2)
        {
            ResetToFit();
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(this);
        _panH = MapScroll.HorizontalOffset;
        _panV = MapScroll.VerticalOffset;
        e.Handled = true;
    }

    /// <summary>
    /// 回到「整張圖都看得到」並繼續跟隨視窗大小。
    /// </summary>
    /// <remarks>
    /// 滾輪放大後很容易迷路（尤其在只有幾條走廊的長條平面圖上），而「拉回全圖」如果只能靠
    /// 一路滾輪縮小，操作員得先想起來往哪個方向轉。雙擊是最不需要解釋的回原點動作。
    /// </remarks>
    private void ResetToFit()
    {
        _fitToWindow = true;
        _zoom = FitScale();
        ApplyZoom();
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}