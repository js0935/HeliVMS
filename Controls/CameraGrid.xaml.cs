using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Controls;

    public partial class CameraGrid : UserControl
    {
        private readonly List<VideoPlayer> _players = new();
        private readonly List<VideoPlayer> _playerPool = new();
        private int _columns = 2;
        private int _splitLayout = 0;
        private int _currentPage = 0;
        private List<Camera?> _channelSlots = new();
        private Camera? _maximizedCamera = null;
        private int? _previousSplitLayout = null;
        private int _previousPage = 0;

        private class PlayerLayoutInfo
        {
            public VideoPlayer Player { get; set; } = null!;
            public int Row { get; set; }
            public int Column { get; set; }
            public int RowSpan { get; set; } = 1;
            public int ColumnSpan { get; set; } = 1;
        }
        private List<PlayerLayoutInfo>? _savedLayout;

    private readonly ICameraService _cameraService;
    private readonly ISettingsService _settingsService;

    // Camera drag-sort implementation using WPF DragDrop
    private VideoPlayer? _dragSourcePlayer;
    private Point _dragStartPoint;
    private bool _isDragging;
    private readonly Border _dragOverlay;
    private DispatcherTimer? _dragTimer;

    public CameraGrid()
    {
        // Constructor: InitializeComponent must be called before event binding
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();

        InitializeComponent();

        // Use bubbling events on MainGrid so child events propagate to CameraGrid
        this.AddHandler(UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(MainGrid_MouseLeftButtonDown), true);
        this.AddHandler(UIElement.MouseMoveEvent,
            new MouseEventHandler(MainGrid_MouseMove), true);
        this.AddHandler(UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(MainGrid_MouseLeftButtonUp), true);
        this.LostMouseCapture += OnLostMouseCapture;
        Unloaded += (_, _) => { CancelDrag(); UnloadAllPlayers(); };

        _dragOverlay = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
        ((Grid)Content).Children.Add(_dragOverlay);

        // Apply debug settings from configuration
        ApplyDebugSettings();
    }

    /// <summary>Apply debug panel visibility from settings</summary>
    public void ApplyDebugSettings()
    {
        DragDebugPanel.Visibility = _settingsService.Settings.ShowDragDebugPanel
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public int CurrentLayout => _splitLayout;
    public int CurrentPage => _currentPage;

    /// <summary>Set split layout only (without LoadCameras) for layout-only changes</summary>
    public void SetSplitLayoutOnly(int splitCount)
    {
        _splitLayout = splitCount;
        _currentPage = 0;
    }

    public void SetLayout(int splitCount)
    {
        _maximizedCamera = null;
        _previousSplitLayout = null;
        if (_splitLayout == splitCount)
        {
            _currentPage++;
        }
        else
        {
            _splitLayout = splitCount;
            _currentPage = 0;
        }
        if (_channelSlots.Count > 0)
        {
            LoadCameras(_channelSlots);
        }
    }

    /// <summary>Save current layout state (split, page, maximized camera)</summary>
    public (int splitLayout, int currentPage, string? maximizedCameraId) SaveLayoutState()
    {
        return (_splitLayout, _currentPage, _maximizedCamera?.Id);
    }

    /// <summary>Restore layout state</summary>
    public void RestoreLayoutState(int splitLayout, int currentPage, string? maximizedCameraId)
    {
        _splitLayout = splitLayout;
        _currentPage = currentPage;
        if (maximizedCameraId is not null)
        {
            Camera? found = null;
            for (int si = 0; si < _channelSlots.Count; si++)
            {
                if (_channelSlots[si]?.Id == maximizedCameraId)
                { found = _channelSlots[si]; break; }
            }
            if (found is not null)
            {
                MaximizePlayer(found);
            }
        }
        else if (_channelSlots.Count > 0)
        {
            LoadCameras(_channelSlots);
        }
    }

    public event Action<Camera?>? PtzSelected;

    public void LoadCameras(IEnumerable<Camera?> channelSlots)
    {
        _savedLayout = null;
        var slots = channelSlots.ToList();
        CancelDrag();

        // Filter enabled and visible cameras for display
        
        var activeCameras = new List<Camera?>(slots.Count);
        for (int si = 0; si < slots.Count; si++)
        {
            var s = slots[si];
            if (s is not null && s.IsEnabled && s.IsVisible)
            {
                activeCameras.Add(s);
            }
        }
        EmptyState.Visibility = activeCameras.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        List<Camera?> displayList;
        int rows, cols;
        bool useSub;
        ComputeDisplayLayout(slots, activeCameras, out displayList, out rows, out cols, out useSub);

        // Compute grid quality and FPS for player decode settings
        var (gridHeight, gridFps) = ComputeGridQuality(rows, cols);

        _columns = cols;
        _channelSlots = slots;

        // Reuse existing players, reposition in Grid if needed
        var currentIds = new HashSet<string>(_players.Count);
        for (int pi = 0; pi < _players.Count; pi++)
        {
            var p = _players[pi];
            if (p.Camera is not null)
            {
                currentIds.Add(p.Camera.Id);
            }
        }
        var newIds = new HashSet<string>(displayList.Count);
        for (int di = 0; di < displayList.Count; di++)
        {
            var c = displayList[di];
            if (c is not null)
            {
                newIds.Add(c.Id);
            }
        }
        bool sameSet = currentIds.SetEquals(newIds);

        MainGrid.Visibility = Visibility.Hidden;
        try
        {
            if (sameSet && rows == MainGrid.RowDefinitions.Count && cols == MainGrid.ColumnDefinitions.Count)
            {
                // Reposition only - no full rebuild
                RebuildGridDefinitions(rows, cols);
                for (int i = 0; i < displayList.Count; i++)
                {
                    var camera = displayList[i];
                    if (camera is null) { continue; }
                    VideoPlayer? player = null;
                    for (int j = 0; j < _players.Count; j++)
                    {
                        if (_players[j].Camera?.Id == camera.Id)
                        {
                            player = _players[j];
                            break;
                        }
                    }
                    if (player is null) continue;
                    Grid.SetRow(player, i / cols);
                    Grid.SetColumn(player, i % cols);
                    player.SetOverlayName(BuildOverlayName(camera));
                }
                MainGrid.Visibility = Visibility.Visible;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowGridMetrics));
                return;
            }

            // Full rebuild - remove all players and recreate
            MainGrid.Children.Clear();

            var usedPlayers = new Dictionary<string, VideoPlayer>();
            var newDisplayIds = new HashSet<string>(newIds);

            // Reuse existing players already in display set
            for (int pi = 0; pi < _players.Count; pi++)
            {
                var p = _players[pi];
                if (p.Camera is not null && newDisplayIds.Contains(p.Camera.Id))
                {
                    usedPlayers[p.Camera.Id] = p;
                }
                else
                {
                    _playerPool.Add(p);
                }
            }

            // Check pool for reusable players
            for (int pi = 0; pi < _playerPool.Count; pi++)
            {
                var p = _playerPool[pi];
                if (p.Camera is not null && newDisplayIds.Contains(p.Camera.Id)
                    && !usedPlayers.TryGetValue(p.Camera.Id, out _))
                {
                    usedPlayers[p.Camera.Id] = p;
                    _playerPool.RemoveAt(pi);
                    pi--;
                }
            }

            RebuildGridDefinitions(rows, cols);

            for (int i = 0; i < displayList.Count; i++)
            {
                var camera = displayList[i];

                if (camera is null)
                {
                    var img = new Image
                    {
                        Source = new BitmapImage(new Uri("pack://application:,,,/images/Disable.png")),
                        Stretch = Stretch.Fill
                    };
                    Grid.SetRow(img, i / cols);
                    Grid.SetColumn(img, i % cols);
                    MainGrid.Children.Add(img);
                    continue;
                }

                var overlayName = BuildOverlayName(camera);
                usedPlayers.TryGetValue(camera.Id, out var player);

                if (player is not null)
                {
                    player.TargetDecodeHeight = gridHeight;
                    player.TargetFps = gridFps;
                    player.LoadCamera(camera, useSub);
                    usedPlayers.Remove(camera.Id);
                }
                else
                {
                    player = new VideoPlayer();
                    player.TargetDecodeHeight = gridHeight;
                    player.TargetFps = gridFps;
                    player.SetFullBleed();
                    player.LoadCamera(camera, useSub);
                    player.PtzSelected += OnPlayerPtzSelected;
                    player.MaximizeRequested += OnPlayerMaximizeRequested;
                }

                player.SetOverlayName(overlayName);

                if (camera.RecordingConfigJson is not null)
                {
                    var recConfig = CameraRecordingConfigData.Deserialize(camera.RecordingConfigJson);
                    if (recConfig is not null)
                    {
                        player.SetRecordingMode(recConfig.GetCurrentMode(DateTime.Now));
                    }
                }

                Grid.SetRow(player, i / cols);
                Grid.SetColumn(player, i % cols);
                MainGrid.Children.Add(player);
            }

            _players.Clear();
            _players.AddRange(MainGrid.Children.OfType<VideoPlayer>());

            // Return unused players to pool
            foreach (var p in usedPlayers.Values)
            {
                p.UnloadCamera();
                _playerPool.Add(p);
            }
        }
        finally
        {
            MainGrid.Visibility = Visibility.Visible;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowGridMetrics));
    }

    private void ComputeDisplayLayout(List<Camera?> slots, List<Camera?> activeCameras,
        out List<Camera?> displayList, out int rows, out int cols, out bool useSub)
    {
        if (activeCameras.Count == 0)
        {
            displayList = new List<Camera?>(slots.Count);
            rows = 1; cols = 1; useSub = false;
            return;
        }

        if (_splitLayout > 0)
        {
            int pageSize = _splitLayout;
            int totalSlots = slots.Count;
            int totalPages = (totalSlots + pageSize - 1) / pageSize;

            if (totalPages > 0 && _currentPage >= totalPages)
            {
                _currentPage = 0;
            }

            int skip = _currentPage * pageSize;
            int displayCount = Math.Min(pageSize, totalSlots - skip);

            (rows, cols) = CalculateGridForLayout(_splitLayout);
            useSub = displayCount > 1;
            var temp = new List<Camera?>(displayCount);
            int end = Math.Min(skip + displayCount, slots.Count);
            for (int i = skip; i < end; i++) { temp.Add(slots[i]); }
            displayList = temp;

            if (_maximizedCamera is not null)
            {
                Camera? target = null;
                for (int si = 0; si < slots.Count; si++)
                {
                    if (slots[si]?.Id == _maximizedCamera.Id)
                    { target = slots[si]; break; }
                }
                if (target is not null)
                {
                    displayList = new List<Camera?> { target };
                }
                else
                {
                    _maximizedCamera = null;
                }
            }
        }
        else
        {
            int split = CalculateAutoSplit(activeCameras.Count);
            (rows, cols) = CalculateGridForLayout(split);
            useSub = activeCameras.Count > 1;
            int takeCount = Math.Min(split, slots.Count);
            var tempList = new List<Camera?>(takeCount);
            for (int i = 0; i < takeCount; i++) { tempList.Add(slots[i]); }
            displayList = tempList;
        }
    }

    private void RebuildGridDefinitions(int rows, int cols)
    {
        MainGrid.RowDefinitions.Clear();
        MainGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < cols; c++)
        {
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int r = 0; r < rows; r++)
        {
            MainGrid.RowDefinitions.Add(new RowDefinition());
        }
    }

    private string BuildOverlayName(Camera camera)
    {
        int channelNumber = _channelSlots.IndexOf(camera) + 1;
        var channels = Controls.ChannelManagementPage.CurrentChannels;
        if (camera.ChannelNumber.HasValue && channels is not null)
        {
            int chNum = camera.ChannelNumber.Value;
            for (int ci = 0; ci < channels.Count; ci++)
            {
                if (channels[ci] is ChannelItem ciItem && ciItem.ChannelNumber == chNum)
                {
                    return ciItem.DisplayName ?? camera.Name ?? $"CH{chNum}";
                }
            }
        }
        return camera.Name ?? (channelNumber > 0 ? $"CH{channelNumber}" : "");
    }

    private void OnPlayerMaximizeRequested(Camera? camera)
    {
        if (camera is null) { return; }

        if (_maximizedCamera is not null && _maximizedCamera.Id == camera.Id)
        {
            RestoreGridLayout();
        }
        else
        {
            _previousSplitLayout = _splitLayout;
            _previousPage = _currentPage;
            MaximizePlayer(camera);
        }
    }

    private void MaximizePlayer(Camera? camera)
    {
        if (camera is null) { return; }

        VideoPlayer? target = null;
        for (int pi = 0; pi < _players.Count; pi++)
        {
            if (_players[pi].Camera?.Id == camera.Id)
            { target = _players[pi]; break; }
        }
        if (target is null) { return; }

        // Save player positions in Grid for restore
        _savedLayout = new List<PlayerLayoutInfo>(_players.Count);
        for (int pi2 = 0; pi2 < _players.Count; pi2++)
        {
            var p = _players[pi2];
            _savedLayout.Add(new PlayerLayoutInfo
            {
                Player = p,
                Row = Grid.GetRow(p),
                Column = Grid.GetColumn(p),
                RowSpan = Grid.GetRowSpan(p),
                ColumnSpan = Grid.GetColumnSpan(p),
            });
        }

        _maximizedCamera = camera;

        // Phase 1: Update UI synchronously to avoid WPF layout issues
        foreach (var p in _players)
        {
            if (p != target)
            {
                p.Visibility = Visibility.Collapsed;
            }
        }
        Grid.SetRow(target, 0);
        Grid.SetColumn(target, 0);
        Grid.SetRowSpan(target, MainGrid.RowDefinitions.Count);
        Grid.SetColumnSpan(target, MainGrid.ColumnDefinitions.Count);
        target.IsMaximized = true;

        // Phase 2: Suspend non-target players to reduce I/O
        foreach (var p in _players)
        {
            if (p != target)
            {
                p.SuspendVideo();
            }
        }

        // Phase 3: Switch target to HD main stream
        target.TargetDecodeHeight = 720;
        target.TargetFps = 25;
        target.SwitchToMainStream();
    }

    private void RestoreGridLayout()
    {
        if (_savedLayout is null) { return; }

        var (gridHeight, gridFps) = ComputeGridQuality(MainGrid.RowDefinitions.Count, MainGrid.ColumnDefinitions.Count);

        foreach (var info in _savedLayout)
        {
            Grid.SetRow(info.Player, info.Row);
            Grid.SetColumn(info.Player, info.Column);
            Grid.SetRowSpan(info.Player, info.RowSpan);
            Grid.SetColumnSpan(info.Player, info.ColumnSpan);
            info.Player.TargetDecodeHeight = gridHeight;
            info.Player.TargetFps = gridFps;
            info.Player.ResumeVideo();
            info.Player.Visibility = Visibility.Visible;
            info.Player.IsMaximized = false;
        }

        _maximizedCamera = null;
        _savedLayout = null;

        if (_previousSplitLayout.HasValue)
        {
            _splitLayout = _previousSplitLayout.Value;
            _currentPage = _previousPage;
            _previousSplitLayout = null;
        }
    }

    private static int CalculateAutoSplit(int activeCount)
    {
        if (activeCount <= 1) { return 1; }
        if (activeCount <= 4) { return 4; }
        if (activeCount <= 9) { return 9; }
        if (activeCount <= 16) { return 16; }
        if (activeCount <= 25) { return 25; }
        if (activeCount <= 36) { return 36; }
        if (activeCount <= 49) { return 49; }
        return 64;
    }

    private static (int rows, int cols) CalculateGridForLayout(int splitCount)
    {
        return splitCount switch
        {
            1 => (1, 1),
            4 => (2, 2),
            9 => (3, 3),
            16 => (4, 4),
            25 => (5, 5),
            36 => (6, 6),
            49 => (7, 7),
            64 => (8, 8),
            _ => (8, 8)
        };
    }

    /// <summary>Called from VideoPlayer.OverlayGrid_MouseDown via HWND mouse capture</summary>
    public void TryStartDragFromPlayer(VideoPlayer player)
    {
        if (player.Camera is null) { return; }
        _dragSourcePlayer = player;
        _dragStartPoint = Mouse.GetPosition(MainGrid);
        _isDragging = false;
        Mouse.Capture(this);
    }

    private void OnPlayerPtzSelected(Camera? camera)
    {
        PtzSelected?.Invoke(camera);
    }

// ================================================================
//  Camera drag-sort implementation using WPF DragDrop
//  Uses Preview events on MainGrid to handle mouse capture on child elements
// ================================================================
    private VideoPlayer? FindVideoPlayerAt(Point position)
    {
        var hit = MainGrid.InputHitTest(position) as DependencyObject;
        while (hit is not null && hit is not VideoPlayer)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }
        return hit as VideoPlayer;
    }

    /// <summary>Walk up from e.OriginalSource to find parent VideoPlayer</summary>
    private static VideoPlayer? FindParentPlayer(DependencyObject? source)
    {
        while (source is not null && source is not VideoPlayer)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source as VideoPlayer;
    }

    private void MainGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) { return; }

        var player = FindParentPlayer(e.OriginalSource as DependencyObject);
        if (player?.Camera is null) { return; }

        _dragSourcePlayer = player;
        _dragStartPoint = e.GetPosition(MainGrid);
        _isDragging = false;
        Mouse.Capture(this);
    }

    private void MainGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourcePlayer is null) { return; }

        var movePos = e.GetPosition(MainGrid);

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (!_isDragging)
            {
                CancelDrag();
            }
            else
            {
                PerformSwapAt(movePos);
                CancelDrag();
            }
            return;
        }

        if (!_isDragging)
        {
            double dx = movePos.X - _dragStartPoint.X;
            double dy = movePos.Y - _dragStartPoint.Y;
            double thresh = SystemParameters.MinimumHorizontalDragDistance;

            if (Math.Abs(dx) < thresh && Math.Abs(dy) < thresh) { return; }

            _isDragging = true;
            Mouse.OverrideCursor = Cursors.SizeAll;

        var channels = Controls.ChannelManagementPage.CurrentChannels;
        var dragName = _dragSourcePlayer.Camera?.Name ?? "";
        if (_dragSourcePlayer.Camera?.ChannelNumber.HasValue == true && channels is not null)
        {
            int chNum = _dragSourcePlayer.Camera.ChannelNumber.Value;
            string? displayName = null;
            for (int ci = 0; ci < channels.Count; ci++)
            {
                if (channels[ci] is ChannelItem ciItem && ciItem.ChannelNumber == chNum)
                { displayName = ciItem.DisplayName; break; }
            }
            dragName = displayName ?? _dragSourcePlayer.Camera.Name ?? $"CH{chNum}";
        }
        _dragOverlay.Visibility = Visibility.Visible;
        ((TextBlock)_dragOverlay.Child).Text = dragName;
            _dragOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            _dragTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Normal,
                (_, _) =>
                {
                    var cp = Mouse.GetPosition((Grid)Content);
                    double h = _dragOverlay.DesiredSize.Height;
                    _dragOverlay.Margin = new Thickness(cp.X + 12, cp.Y - h - 8, 0, 0);
                },
                Dispatcher);
            _dragTimer.Start();
        }
    }

    private void MainGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging && _dragSourcePlayer is not null)
        {
            PerformSwapAt(e.GetPosition(MainGrid));
            e.Handled = true;
        }
        CancelDrag();
    }

    private void PerformSwapAt(Point dropPosition)
    {
        var targetPlayer = FindVideoPlayerAt(dropPosition);
        if (targetPlayer is null || targetPlayer == _dragSourcePlayer || targetPlayer.Camera is null) { return; }

        var sourceCameraId = _dragSourcePlayer!.Camera?.Id;
        if (sourceCameraId is null) { return; }

        var srcIdx = IndexOfId(_channelSlots, sourceCameraId);
        var tgtIdx = IndexOfId(_channelSlots, targetPlayer.Camera.Id);
        if (srcIdx < 0 || tgtIdx < 0) { return; }

        var ordered = new List<Camera?>(_channelSlots.Count);
        for (int i = 0; i < _channelSlots.Count; i++)
        {
            var c = _channelSlots[i];
            if (c is not null) { ordered.Add(c); }
        }

        var actualSrcIdx = IndexOfId(ordered, sourceCameraId);
        var actualTgtIdx = IndexOfId(ordered, targetPlayer.Camera.Id);
        if (actualSrcIdx < 0 || actualTgtIdx < 0) { return; }

        var moving = ordered[actualSrcIdx];
        ordered.RemoveAt(actualSrcIdx);
        ordered.Insert(actualTgtIdx, moving);

        var orders = new List<(string, int)>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            orders.Add((ordered[i]!.Id!, (i + 1) * 100));
        }

        // Swap players in-place in the grid
        int srcRow = Grid.GetRow(_dragSourcePlayer);
        int srcCol = Grid.GetColumn(_dragSourcePlayer);
        int tgtRow = Grid.GetRow(targetPlayer);
        int tgtCol = Grid.GetColumn(targetPlayer);
        Grid.SetRow(_dragSourcePlayer, tgtRow);
        Grid.SetColumn(_dragSourcePlayer, tgtCol);
        Grid.SetRow(targetPlayer, srcRow);
        Grid.SetColumn(targetPlayer, srcCol);

        (_channelSlots[srcIdx], _channelSlots[tgtIdx]) = (_channelSlots[tgtIdx], _channelSlots[srcIdx]);
        _cameraService.ReassignGridOrder(orders, notify: false);
    }

    private static int IndexOfId(List<Camera?> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i]?.Id == id) { return i; }
        return -1;
    }

    private void ShowDragEvent(string msg)
    {
        DragEventText.Text = $"event: {msg}";
    }

    private void ShowDragState(string msg)
    {
        DragStateText.Text = $"state: {msg}";
    }

    private void ShowDragDetail(string msg)
    {
        DragDetailText.Text = $"detail: {msg}";
    }

    private void ShowGridMetrics()
    {
        double w = MainGrid.ActualWidth;
        double h = MainGrid.ActualHeight;
        int rc = MainGrid.RowDefinitions.Count;
        int cc = MainGrid.ColumnDefinitions.Count;
        ShowDragState($"MG:{w:F0}x{h:F0} {cc}x{rc}  players:{_players.Count}");
    }

    /// <summary>Compute decode quality based on grid dimensions</summary>
    private static (int height, int fps) ComputeGridQuality(int rows, int cols)
    {
        var maxDim = Math.Max(rows, cols);
        return maxDim switch
        {
            <= 1 => (720, 25),
            <= 2 => (480, 15),
            <= 3 => (360, 12),
            <= 4 => (270, 8),
            _ => (180, 5)
        };
    }

    /// <summary>Apply decode quality settings to a player</summary>
    private void ApplyPlayerQuality(VideoPlayer player, int rows, int cols)
    {
        var (height, fps) = ComputeGridQuality(rows, cols);
        player.TargetDecodeHeight = height;
        player.TargetFps = fps;
    }

    private void CancelDrag()
    {
        _dragSourcePlayer = null;
        _isDragging = false;
        _dragTimer?.Stop();
        _dragTimer = null;
        _dragOverlay.Visibility = Visibility.Collapsed;
        if (Mouse.OverrideCursor == Cursors.SizeAll)
        {
            Mouse.OverrideCursor = null;
        }
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }
        ShowGridMetrics();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragSourcePlayer is not null || _isDragging)
        {
            CancelDrag();
        }
    }

    /// <summary>Detach all VideoPlayers from decoder sessions and clean up</summary>
    private void UnloadAllPlayers()
    {
        foreach (var p in _players)
        {
            p.DetachCamera();
        }
        foreach (var p in _playerPool)
        {
            p.DetachCamera();
        }
    }
}
