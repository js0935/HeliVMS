using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace HeliVMS.Controls;

/// <summary>
/// Timeline control for video playback seeking and visualization
/// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
/// </summary>
public partial class TimelineControl : UserControl
{
    // Fields: playback state
    private DateTime _selectedDate = DateTime.Today;
    private double _positionFraction;
    private double _viewDurationFraction = 1.0 / 24.0; // Default: 60 minutes

    private const double MinViewHours = 1.0;
    private const double MaxViewHours = 24.0;

    // Fields: export bracket selection
    private double _selectionStartSeconds;
    private double _selectionEndSeconds;
    private bool _isSelecting;
    private bool _isExportEnabled;

    // Hover tracking fields
    private double _hoverSeconds = -1;
    private SegmentRenderData? _hoverSegment;
    private Point _lastHoverPoint;

    // Playback position throttle to limit UI refresh rate
    private DateTime _lastPlaybackThrottle = DateTime.MinValue;
    private const double PlaybackThrottleIntervalMs = 100; // max 10fps

    /// <summary>Playback position changed (seconds 0 ~ 86400)</summary>
    public event Action<double>? PositionChanged;

    /// <summary>Seek requested via timeline click</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Export bracket selection changed (startSec, endSec), 0=cleared</summary>
    public event Action<double, double>? SelectionChanged;

    /// <summary>A-B loop: point A positioned</summary>
    public event Action? LoopAPositioned;
    /// <summary>A-B loop: point B positioned</summary>
    public event Action? LoopBPositioned;
    /// <summary>A-B loop: toggle enabled/disabled</summary>
    public event Action? LoopToggled;

    /// <summary>Bookmark jump requested to timestamp</summary>
    public event Action<double>? BookmarkJumpRequested;

    /// <summary>Bookmark repositioned (bookmarkId, newSeconds)</summary>
    public event Action<string, double>? BookmarkMoved;
    public event Action<string>? BookmarkDeleted;
    public event Action<string, string>? BookmarkRenamed;

    public double SelectionStartSeconds => _selectionStartSeconds;
    public double SelectionEndSeconds => _selectionEndSeconds;
    public bool HasSelection => _selectionStartSeconds > 0 && _selectionEndSeconds > 0
        && Math.Abs(_selectionEndSeconds - _selectionStartSeconds) > 1;
    public bool IsExportEnabled
    {
        get => _isExportEnabled;
        set { _isExportEnabled = value; if (!value) ClearSelection(); else SyncCanvas(); }
    }

    public void ClearSelection()
    {
        _selectionStartSeconds = 0;
        _selectionEndSeconds = 0;
        TimelineCanvas.SelectionStartSeconds = 0;
        TimelineCanvas.SelectionEndSeconds = 0;
        SelectionChanged?.Invoke(0, 0);
        SyncCanvas();
    }

    // A-B Loop fields
    public double LoopA
    {
        get => TimelineCanvas.LoopA;
        set { TimelineCanvas.LoopA = value; LoopAPositioned?.Invoke(); SyncCanvas(); }
    }
    public double LoopB
    {
        get => TimelineCanvas.LoopB;
        set { TimelineCanvas.LoopB = value; LoopBPositioned?.Invoke(); SyncCanvas(); }
    }
    public bool LoopEnabled
    {
        get => TimelineCanvas.LoopEnabled;
        set { TimelineCanvas.LoopEnabled = value; LoopToggled?.Invoke(); SyncCanvas(); }
    }
    public bool HasLoop => LoopA >= 0 && LoopB >= 0 && Math.Abs(LoopB - LoopA) > 1;

    public void SetLoopAtPosition(double seconds)
    {
        if (!HasLoop || HasLoop && LoopToggled is null && LoopBPositioned is null)
        {
            LoopA = seconds;
        }
        else if (HasLoop && !LoopEnabled)
        {
            LoopB = seconds;
            LoopEnabled = true;
        }
        else
        {
            ClearLoop();
            LoopA = seconds;
        }
    }

    public void ClearLoop()
    {
        TimelineCanvas.LoopA = -1;
        TimelineCanvas.LoopB = -1;
        TimelineCanvas.LoopEnabled = false;
        SyncCanvas();
    }
    // end A-B Loop

    // ScrollViewer offset/viewport accessors
    private double ScrollOffset => TimelineCanvas.ScrollOffset;
    private double ViewportWidthForCanvas => TimelineCanvas.ViewportWidthForCanvas;
    // end ScrollViewer

// ================================================================
    //  Bookmarks
// ================================================================

    public List<PlaybackBookmark> Bookmarks => TimelineCanvas.Bookmarks;

    public void AddBookmark(double seconds, string note = "")
    {
        var bm = new PlaybackBookmark
        {
            Seconds = seconds,
            Note = note ?? ""
        };
        TimelineCanvas.Bookmarks.Add(bm);
        SyncCanvas();
    }

    public bool RemoveBookmark(string id)
    {
        var removed = TimelineCanvas.Bookmarks.RemoveAll(b => b.Id == id);
        if (removed > 0) { SyncCanvas(); return true; }
        return false;
    }

    public void ClearBookmarks()
    {
        TimelineCanvas.Bookmarks.Clear();
        SyncCanvas();
    }

    public void ReplaceBookmarks(List<PlaybackBookmark> bookmarks)
    {
        TimelineCanvas.Bookmarks.Clear();
        TimelineCanvas.Bookmarks.AddRange(bookmarks);
        SyncCanvas();
    }

    public PlaybackBookmark? FindBookmarkAtSeconds(double seconds, double tolerance = 4)
    {
        double totalSecs = _viewDurationFraction * 86400;
        double secsPerPixel = TimelineCanvas.ActualWidth > 0
            ? totalSecs / TimelineCanvas.ActualWidth : 1;
        double toleranceSecs = tolerance * secsPerPixel;
        var bms = TimelineCanvas.Bookmarks;
        for (int bi = 0; bi < bms.Count; bi++)
        {
            if (Math.Abs(bms[bi].Seconds - seconds) < toleranceSecs)
            {
                return bms[bi];
            }
        }
        return null;
    }

    public bool TryJumpToBookmarkAt(double seconds, out PlaybackBookmark? bookmark)
    {
        bookmark = FindBookmarkAtSeconds(seconds);
        if (bookmark is not null)
        {
            BookmarkJumpRequested?.Invoke(bookmark.Seconds);
            return true;
        }
        return false;
    }

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set { _selectedDate = value; _positionFraction = 0; UpdateZoomIndicator(); SyncCanvas(); }
    }

    public double PositionFraction
    {
        get => _positionFraction;
        set
        {
            _positionFraction = Math.Clamp(value, 0, 1);
            EnsurePlayheadVisible();
            SyncCanvas();
        }
    }

    /// <summary>Update playhead position and sync overlay via Canvas</summary>
    public void UpdatePlaybackPosition(double fraction)
    {
        _positionFraction = Math.Clamp(fraction, 0, 1);
        TimelineCanvas.PositionFraction = _positionFraction;

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastPlaybackThrottle).TotalMilliseconds;

        if (elapsed >= PlaybackThrottleIntervalMs)
        {
            _lastPlaybackThrottle = now;
            EnsurePlayheadVisible(); // every 100ms throttled scroll
            TimelineCanvas.RenderOverlay();
        }
    }

    public double ViewDurationHours
    {
        get => _viewDurationFraction * 24.0;
        set
        {
            var hours = Math.Clamp(value, MinViewHours, MaxViewHours);
            _viewDurationFraction = hours / 24.0;
            UpdateCanvasWidth();
            EnsurePlayheadVisible();
            UpdateZoomIndicator();
            SyncCanvas();
        }
    }

    public TimelineControl()
    {
        Log.Information("[DBG] TimelineControl ctor start");
        InitializeComponent();
        Log.Information("[DBG] TimelineControl InitializeComponent done");
        Loaded += (_, _) =>
        {
            Log.Information("[DBG] TimelineControl Loaded firing");
            TimelineCanvas.RenderAll();
            UpdateZoomIndicator();
            Log.Information("[DBG] TimelineControl Loaded done");
        };
    }

    public string? HighlightedCameraId
    {
        get => TimelineCanvas.HighlightedCameraId;
        set { TimelineCanvas.HighlightedCameraId = value; TimelineCanvas.RenderAll(); }
    }

    /// <summary>Adjust timeline height based on channel count, clamped to reasonable range</summary>
    public void AdjustHeightForChannelCount(int channelCount)
    {
        const double rulerH = 22;
        const double actBarH = 10;
        const double actGap = 2;
        const double trackH = 14;
        const double trackGap = 2;
        double desiredH = rulerH + 2 + actBarH + actGap + channelCount * (trackH + trackGap) + 8;
        Height = Math.Clamp(desiredH, 80, 300);
    }

    public void SetChannels(List<TimelineChannelData> channels)
    {
        TimelineCanvas.Channels = channels;
        AdjustHeightForChannelCount(channels.Count);
        TimelineCanvas.RenderAll();
    }

    public void SetSegments(List<SegmentRenderData> segments)
    {
        TimelineCanvas.Segments = segments;
        TimelineCanvas.RenderAll();
    }

    private void UpdateCanvasWidth()
    {
        double vpW = TimelineScroller.ViewportWidth;
        if (vpW > 0 && _viewDurationFraction > 0)
        {
            double newW = vpW / _viewDurationFraction;
            if (Math.Abs(TimelineCanvas.Width - newW) > 0.5)
            {
                TimelineCanvas.Width = newW;
            }
        }
    }

    private void SyncCanvas()
    {
        TimelineCanvas.PositionFraction = _positionFraction;
        TimelineCanvas.ViewDurationFraction = _viewDurationFraction;
        TimelineCanvas.ScrollOffset = TimelineScroller.HorizontalOffset;
        TimelineCanvas.ViewportWidthForCanvas = TimelineScroller.ViewportWidth;
        TimelineCanvas.SelectedDate = _selectedDate;
        UpdateCanvasWidth();
        TimelineCanvas.RenderAll();
    }

    private double GetSecondsFromX(double canvasX)
    {
        double w = TimelineCanvas.ActualWidth;
        if (w <= 0) { return 0; }
        return (canvasX / w) * 86400.0;
    }

    private double GetXFromSeconds(double seconds)
    {
        double w = TimelineCanvas.ActualWidth;
        if (w <= 0) { return 0; }
        return (seconds / 86400.0) * w;
    }

    private void EnsurePlayheadVisible()
    {
        double canvasW = TimelineCanvas.ActualWidth;
        if (canvasW <= 0) { return; }

        double phPixel = _positionFraction * canvasW;
        double scrollOff = TimelineScroller.HorizontalOffset;
        double vpW = TimelineScroller.ViewportWidth;
        const double padding = 30;

        if (phPixel < scrollOff + padding)
        {
            TimelineScroller.ScrollToHorizontalOffset(Math.Max(0, phPixel - padding));
        }
        else if (phPixel > scrollOff + vpW - padding)
            TimelineScroller.ScrollToHorizontalOffset(
                Math.Min(TimelineScroller.ScrollableWidth, phPixel - vpW + padding));
    }

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out int hours))
        {
            ViewDurationHours = hours;
        }
    }

    private void UpdateZoomIndicator()
    {
        double hours = ViewDurationHours;
        string label = hours switch
        {
            <= 1 => "1H",
            <= 2 => "2H",
            <= 6 => "6H",
            <= 12 => "12H",
            _ => "24H"
        };
        ZoomLevelText.Text = label;

        var activeBrush = TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var inactiveBrush = Brushes.Transparent;
        Zoom1H.Background = label == "1H" ? activeBrush : inactiveBrush;
        Zoom6H.Background = label == "6H" ? activeBrush : inactiveBrush;
        Zoom12H.Background = label == "12H" ? activeBrush : inactiveBrush;
        Zoom24H.Background = label == "24H" ? activeBrush : inactiveBrush;
    }

// ================================================================
    //  Mouse event handlers
// ================================================================

    private bool _isDraggingPlayhead;
    private bool _isPanning;
    private Point _lastMousePos;
    private bool _isScrubbing;
    private bool _isDraggingBookmark;
    private PlaybackBookmark? _dragBookmark;

    private void TimelineCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double currentHours = ViewDurationHours;
        double zoomFactor = e.Delta > 0 ? 0.8 : 1.25;
        double newHours = Math.Clamp(currentHours * zoomFactor, MinViewHours, MaxViewHours);
        ViewDurationHours = newHours;

        // Keep mouse position stable during zoom
            double mouseSecs = GetSecondsFromX(e.GetPosition(TimelineCanvas).X);
        double newCanvasW = TimelineCanvas.Width > 0 ? TimelineCanvas.Width : 1;
        double targetScroll = (mouseSecs / 86400.0) * newCanvasW - e.GetPosition(TimelineCanvas).X;
        targetScroll = Math.Clamp(targetScroll, 0, TimelineScroller.ScrollableWidth);
        TimelineScroller.ScrollToHorizontalOffset(targetScroll);

        e.Handled = true;
    }

    private void TimelineCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(TimelineCanvas);
        var seconds = GetSecondsFromX(pos.X);

        if (e.ChangedButton == MouseButton.Left)
        {
        // Bookmark hit test — drag if directly on a diamond, jump otherwise
            if (Bookmarks.Count > 0 && FindBookmarkAtSeconds(seconds) is { } hitBm)
            {
                _isDraggingBookmark = true;
                _dragBookmark = hitBm;
                TimelineCanvas.CaptureMouse();
                TimelineCanvas.RenderOverlay();
                return;
            }

            // Activity overview bar click → jump to that hour
            if (pos.Y >= 24 && pos.Y <= 36)
            {
                double hourStart = Math.Floor(seconds / 3600) * 3600;
                _positionFraction = Math.Clamp(hourStart / 86400.0, 0, 1);
                SeekRequested?.Invoke(hourStart);
                TimelineCanvas.RenderOverlay();
                return;
            }

            // SHIFT + click for export bracket range
            if (Keyboard.Modifiers == ModifierKeys.Shift && _isExportEnabled)
            {
                _isSelecting = true;
                _selectionStartSeconds = seconds;
                _selectionEndSeconds = seconds;
                TimelineCanvas.SelectionStartSeconds = seconds;
                TimelineCanvas.SelectionEndSeconds = seconds;
                TimelineCanvas.CaptureMouse();
                TimelineCanvas.RenderOverlay();
                return;
            }

        // Double-click inside selection range clears it
            if (HasSelection && e.ClickCount >= 2 &&
                seconds >= _selectionStartSeconds && seconds <= _selectionEndSeconds)
            {
                ClearSelection();
                TimelineCanvas.RenderOverlay();
                return;
            }

            double phSecs = _positionFraction * 86400;
            double phX = GetXFromSeconds(phSecs);

            if (Math.Abs(pos.X - phX) <= 8)
            {
        // Start dragging playhead
                _isDraggingPlayhead = true;
                TimelineCanvas.CaptureMouse();
            }
            else
            {
        // Click elsewhere: seek and prepare for drag-scrub
                _positionFraction = Math.Clamp(seconds / 86400.0, 0, 1);
                _isScrubbing = true;
                TimelineCanvas.CaptureMouse();
                PositionChanged?.Invoke(seconds);
                TimelineCanvas.RenderOverlay();
            }
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            // Right-click on bookmark → context menu for rename/delete
            var hitBm = Bookmarks.Count > 0 ? FindBookmarkAtSeconds(seconds) : null;
            if (hitBm is not null)
            {
                var ctx = new ContextMenu();
                var renameItem = new MenuItem { Header = "重新命名書籤" };
                renameItem.Click += (_, _) =>
                {
                    var dlg = new System.Windows.Window
                    {
                        Title = "重新命名書籤",
                        Width = 320, Height = 140,
                        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                        WindowStyle = System.Windows.WindowStyle.None,
                        AllowsTransparency = true,
                        Background = (Brush)(TryFindResource("SurfaceBrush") ?? Brushes.Black),
                        ResizeMode = System.Windows.ResizeMode.NoResize
                    };
                    var sp = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(12) };
                    sp.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "書籤備註：",
                        Foreground = (Brush)(TryFindResource("TextBrush") ?? Brushes.White),
                        Margin = new System.Windows.Thickness(0, 0, 0, 6)
                    });
                    var tb = new System.Windows.Controls.TextBox
                    {
                        Text = hitBm.Note ?? "",
                        Foreground = (Brush)(TryFindResource("TextBrush") ?? Brushes.White),
                        Background = (Brush)(TryFindResource("InputBackgroundBrush") ?? Brushes.Gray)
                    };
                    sp.Children.Add(tb);
                    var btnPanel = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Margin = new System.Windows.Thickness(0, 8, 0, 0)
                    };
                    var okBtn = new System.Windows.Controls.Button
                    {
                        Content = "確定",
                        Width = 60, Height = 24,
                        Margin = new System.Windows.Thickness(0, 0, 6, 0),
                        Style = (System.Windows.Style)(TryFindResource("PrimaryButton") ?? new System.Windows.Style())
                    };
                    okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
                    var cancelBtn = new System.Windows.Controls.Button
                    {
                        Content = "取消",
                        Width = 60, Height = 24,
                        Style = (System.Windows.Style)(TryFindResource("SecondaryButton") ?? new System.Windows.Style())
                    };
                    cancelBtn.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
                    btnPanel.Children.Add(okBtn);
                    btnPanel.Children.Add(cancelBtn);
                    sp.Children.Add(btnPanel);
                    dlg.Content = sp;
                    if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text)
                        && tb.Text != hitBm.Note)
                    {
                        hitBm.Note = tb.Text;
                        BookmarkRenamed?.Invoke(hitBm.Id, tb.Text);
                        TimelineCanvas.RenderOverlay();
                    }
                };
                var deleteItem = new MenuItem { Header = "刪除書籤" };
                deleteItem.Click += (_, _) =>
                {
                    BookmarkDeleted?.Invoke(hitBm.Id);
                    TimelineCanvas.RenderOverlay();
                };
                ctx.Items.Add(renameItem);
                ctx.Items.Add(deleteItem);
                ctx.IsOpen = true;
                return;
            }

            _isPanning = true;
            _lastMousePos = pos;
            TimelineCanvas.CaptureMouse();
            Cursor = Cursors.ScrollWE;
        }
    }

    private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(TimelineCanvas);
        var seconds = GetSecondsFromX(pos.X);

        // Update hover state on every move
        _hoverSeconds = seconds;
        _lastHoverPoint = pos;
        TimelineCanvas.HoverSeconds = seconds;
        TimelineCanvas.HoverPosition = pos;

        UpdateHoverSegment(seconds);
        TimelineCanvas.ShowTooltip = _hoverSegment is not null && !_isDraggingPlayhead && !_isPanning && !_isSelecting;

        if (_isDraggingBookmark && _dragBookmark is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            double dragSeconds = GetSecondsFromX(pos.X);
            _dragBookmark.Seconds = Math.Clamp(dragSeconds, 0, 86400);
            TimelineCanvas.RenderOverlay();
        }
        else if ((_isDraggingPlayhead || _isScrubbing) && e.LeftButton == MouseButtonState.Pressed)
        {
            double dragSeconds = GetSecondsFromX(pos.X);
            _positionFraction = Math.Clamp(dragSeconds / 86400.0, 0, 1);
            PositionChanged?.Invoke(dragSeconds);
            TimelineCanvas.RenderOverlay();
        }
        else if (_isPanning && e.RightButton == MouseButtonState.Pressed)
        {
            double dx = _lastMousePos.X - pos.X;
            double newOff = TimelineScroller.HorizontalOffset + dx;
            newOff = Math.Clamp(newOff, 0, TimelineScroller.ScrollableWidth);
            TimelineScroller.ScrollToHorizontalOffset(newOff);
            _lastMousePos = pos;
        }
        else if (_isSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            _selectionEndSeconds = seconds;
            TimelineCanvas.SelectionEndSeconds = seconds;
            TimelineCanvas.RenderOverlay();
        }
        else
        {
            TimelineCanvas.RenderOverlay();
        }
    }

    private void UpdateHoverSegment(double seconds)
    {
        _hoverSegment = TimelineCanvas.FindSegmentAtSeconds(seconds);
        TimelineCanvas.HoverSegment = _hoverSegment;
    }

    private void TimelineCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBookmark && _dragBookmark is not null)
        {
            _isDraggingBookmark = false;
            TimelineCanvas.ReleaseMouseCapture();
            var pos = e.GetPosition(TimelineCanvas);
            double seconds = Math.Clamp(GetSecondsFromX(pos.X), 0, 86400);
            _dragBookmark.Seconds = seconds;
            BookmarkMoved?.Invoke(_dragBookmark.Id, seconds);
            _dragBookmark = null;
            SeekRequested?.Invoke(seconds);
            TimelineCanvas.RenderOverlay();
        }

        if (_isDraggingPlayhead || _isScrubbing)
        {
            bool wasScrubbing = _isScrubbing;
            _isDraggingPlayhead = false;
            _isScrubbing = false;
            TimelineCanvas.ReleaseMouseCapture();
            var pos = e.GetPosition(TimelineCanvas);
            double seconds = GetSecondsFromX(pos.X);
            if (wasScrubbing) SeekRequested?.Invoke(seconds);
        }

        if (_isPanning)
        {
            _isPanning = false;
            TimelineCanvas.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            TimelineCanvas.ReleaseMouseCapture();
            if (HasSelection)
            {
                SelectionChanged?.Invoke(_selectionStartSeconds, _selectionEndSeconds);
            }
            else
            {
                ClearSelection();
            }
            TimelineCanvas.RenderOverlay();
        }
    }

    private void TimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        TimelineCanvas.RenderOverlay();
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Log.Information("[DBG] TimelineCanvas SizeChanged {W}x{H}", e.NewSize.Width, e.NewSize.Height);
        TimelineCanvas.RenderAll();
    }

    private void TimelineScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateCanvasWidth();
        TimelineCanvas.ScrollOffset = TimelineScroller.HorizontalOffset;
        TimelineCanvas.ViewportWidthForCanvas = TimelineScroller.ViewportWidth;
        TimelineCanvas.RenderAll();
    }
}

/// <summary>
/// Custom Canvas that renders timeline segments via OnRender
/// Uses DrawingGroup backing store for performance; avoids WPF UserControl.OnRender ContentPresenter issues
class TimelineCanvas : Canvas
{
    // Rendering data properties
    public double PositionFraction { get; set; }
    public double ScrollOffset { get; set; }
    public double ViewportWidthForCanvas { get; set; } = 100;
    public double ViewDurationFraction { get; set; } = 1.0;
    public DateTime SelectedDate { get; set; } = DateTime.Today;

    // Cached rendering values
    private double _cachedDpi = 96.0;
    private double _cachedTotalTrackH = 20;
    private readonly double[] _cachedActivityAgg = new double[24];
    private readonly double[] _cachedMotionAgg = new double[24];
    private readonly Dictionary<string, FormattedText> _rulerTextCache = new();
    private string _lastRulerFormat = "";
    private Brush? _lastRulerBrush;
    private Geometry? _playheadTriangleCache;
    private FormattedText? _loopLabelA, _loopLabelB;

    // DrawingGroup backing store to avoid InvalidateVisual relayout
    private readonly DrawingGroup _staticLayer = new();
    private readonly DrawingGroup _overlayLayer = new();

    // Selection bracket state
    public double SelectionStartSeconds { get; set; }
    public double SelectionEndSeconds { get; set; }

    // A-B Loop state
    public double LoopA { get; set; } = -1;
    public double LoopB { get; set; } = -1;
    public bool LoopEnabled { get; set; }

    // Hover state
    public double HoverSeconds { get; set; } = -1;
    public Point HoverPosition { get; set; }
    public SegmentRenderData? HoverSegment { get; set; }
    public bool ShowTooltip { get; set; }

    /// <summary>CameraId to highlight in the timeline tracks (video grid selection)</summary>
    public string? HighlightedCameraId { get; set; }

    public List<PlaybackBookmark> Bookmarks { get; set; } = new();

    // Activity overview bar brushes
    private static readonly Brush[] _activityBrushes;
    private static readonly Brush _activityBarBgBrush;

    // Rendering geometry constants
    private const double TopMargin = 4;
    private const double RulerHeight = 22;
    private const double ActivityBarHeight = 10;
    private const double ActivityBarGap = 2;
    private const double ChannelLabelWidth = 32;
    private const double TrackHeight = 14;
    private const double TrackGap = 2;
    private const double PlayheadWidth = 2;

    private static readonly Color ColorContinuous = Color.FromRgb(0x21, 0x96, 0xF3);
    private static readonly Color ColorMotion    = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color ColorAlarm     = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color ColorEvent     = Color.FromRgb(0xE9, 0x1E, 0x63);

    // Pre-allocated rendering resources to avoid allocations during OnRender
    private static readonly Brush[] SegmentFillBrushes;
    private static readonly Pen[] SegmentBorderPens;
    private static readonly Brush NoRecBrush;
    private static readonly Pen? PlayheadPen;
    private static Pen? _rulerMajorPen;
    private static Pen? _rulerMinorPen;

    // Selection bracket rendering resources
    private static readonly Brush _selectionOverlayBrush;
    private static readonly Pen _selectionLinePen;
    private static readonly Pen _selectionFillPen;
    // A-B Loop rendering resources
    private static readonly Brush _loopOverlayBrush;
    private static readonly Pen _loopMarkerPen;
    private static readonly Pen _loopMarkerDisabledPen;
    // Hover cursor rendering resources
    private static readonly Pen _hoverLinePen;
    private static readonly Brush _hoverLabelBgBrush;
    // Segment tooltip rendering resources
    private static readonly Brush _tooltipBgBrush;
    private static readonly Pen _tooltipBorderPen;
    // Segment boundary markers
    private static readonly Pen[] _markerPens;
    // Motion score intensity brushes
    private static readonly Brush[] _motionScoreBrushes;

    // Channel highlight (from video grid selection)
    private static readonly Brush _highlightTrackBrush;
    private static readonly Pen _highlightBorderPen;

    // Bookmark markers
    private static readonly Brush _bookmarkFillBrush;
    private static readonly Brush _bookmarkNoteBgBrush;
    private static readonly Pen _bookmarkNoteBorderPen;

    static TimelineCanvas()
    {
        var colors = new[] { ColorContinuous, ColorMotion, ColorAlarm, ColorEvent };
        SegmentFillBrushes = new Brush[4];
        SegmentBorderPens = new Pen[4];
        for (int i = 0; i < 4; i++)
        {
            var fill = new SolidColorBrush(Color.FromArgb(180, colors[i].R, colors[i].G, colors[i].B));
            fill.Freeze();
            SegmentFillBrushes[i] = fill;

            var border = new SolidColorBrush(colors[i]);
            border.Freeze();
            var bp = new Pen(border, 0.5);
            bp.Freeze();
            SegmentBorderPens[i] = bp;
        }

        var noRec = new SolidColorBrush(Color.FromArgb(30, 0x80, 0x80, 0x80));
        noRec.Freeze();
        NoRecBrush = noRec;

        var phPen = new Pen(Brushes.OrangeRed, PlayheadWidth);
        phPen.Freeze();
        PlayheadPen = phPen;

        _selectionOverlayBrush = new SolidColorBrush(Color.FromArgb(64, 0x44, 0x88, 0xFF));
        _selectionOverlayBrush.Freeze();

        var selLine = new Pen(new SolidColorBrush(Color.FromArgb(180, 0x44, 0x88, 0xFF)), 1.5);
        selLine.Freeze();
        _selectionLinePen = selLine;

        var selFill = new Pen(new SolidColorBrush(Color.FromArgb(64, 0x44, 0x88, 0xFF)), 0);
        selFill.Freeze();
        _selectionFillPen = selFill;

        var loopOv = new SolidColorBrush(Color.FromArgb(40, 0x4C, 0xAF, 0x50));
        loopOv.Freeze();
        _loopOverlayBrush = loopOv;

        var loopMkr = new Pen(new SolidColorBrush(Color.FromArgb(200, 0x4C, 0xAF, 0x50)), 2);
        loopMkr.Freeze();
        _loopMarkerPen = loopMkr;

        var loopMkrDis = new Pen(Brushes.Gray, 1);
        loopMkrDis.Freeze();
        _loopMarkerDisabledPen = loopMkrDis;

        var hoverLine = new Pen(new SolidColorBrush(Color.FromArgb(100, 0xFF, 0xFF, 0xFF)), 0.5);
        hoverLine.Freeze();
        _hoverLinePen = hoverLine;

        var hoverBg = new SolidColorBrush(Color.FromArgb(160, 0x22, 0x22, 0x22));
        hoverBg.Freeze();
        _hoverLabelBgBrush = hoverBg;

        var tipBg = new SolidColorBrush(Color.FromArgb(220, 0x22, 0x22, 0x22));
        tipBg.Freeze();
        _tooltipBgBrush = tipBg;

        var tipBorder = new Pen(new SolidColorBrush(Color.FromArgb(100, 0xFF, 0xFF, 0xFF)), 0.5);
        tipBorder.Freeze();
        _tooltipBorderPen = tipBorder;

        // Bookmark: gold diamond fill
        var bmFill = new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xD7, 0x00));
        bmFill.Freeze();
        _bookmarkFillBrush = bmFill;

        // Highlight: green-tinted background + border for selected channel track
        var hlBg = new SolidColorBrush(Color.FromArgb(40, 0x4C, 0xAF, 0x50));
        hlBg.Freeze();
        _highlightTrackBrush = hlBg;
        var hlBorder = new Pen(new SolidColorBrush(Color.FromArgb(180, 0x4C, 0xAF, 0x50)), 1.5);
        hlBorder.Freeze();
        _highlightBorderPen = hlBorder;

        var bmNoteBg = new SolidColorBrush(Color.FromArgb(210, 0x22, 0x22, 0x22));
        bmNoteBg.Freeze();
        _bookmarkNoteBgBrush = bmNoteBg;

        var bmNoteBorder = new Pen(new SolidColorBrush(Color.FromArgb(180, 0xFF, 0xD7, 0x00)), 0.5);
        bmNoteBorder.Freeze();
        _bookmarkNoteBorderPen = bmNoteBorder;

        // Marker pens for segment boundaries (dashed, color-coded)
        var markerColors = new[] { ColorContinuous, ColorMotion, ColorAlarm, ColorEvent };
        _markerPens = new Pen[4];
        for (int i = 0; i < 4; i++)
        {
            var c = Color.FromArgb(160, markerColors[i].R, markerColors[i].G, markerColors[i].B);
            var b = new SolidColorBrush(c); b.Freeze();
            var dashes = new DoubleCollection { 3, 3 }; dashes.Freeze();
            var p = new Pen(b, 1) { DashStyle = new DashStyle(dashes, 0) };
            p.Freeze();
            _markerPens[i] = p;
        }

        // Motion score intensity overlay -> 10-step green gradient (higher score = brighter)
        _motionScoreBrushes = new Brush[10];
        for (int i = 0; i < 10; i++)
        {
            double t = i / 9.0;
            var c = Color.FromArgb((byte)(120 + t * 80),
                (byte)(60 * t),
                (byte)(100 + t * 100),
                (byte)(40 + t * 60));
            var b = new SolidColorBrush(c);
            b.Freeze();
            _motionScoreBrushes[i] = b;
        }

        // Activity overview bar -> 10-step blue heat gradient
        _activityBrushes = new Brush[10];
        for (int i = 0; i < 10; i++)
        {
            double t = i / 9.0;
            var c = Color.FromArgb(200,
                (byte)(20 + t * 55),
                (byte)(80 + t * 120),
                (byte)(200 - t * 40));
            var b = new SolidColorBrush(c);
            b.Freeze();
            _activityBrushes[i] = b;
        }

        var actBg = new SolidColorBrush(Color.FromArgb(30, 0x80, 0x80, 0x80));
        actBg.Freeze();
        _activityBarBgBrush = actBg;

        var nowPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0x99, 0xCC, 0xFF)), 1);
        nowPen.Freeze();
        NowMarkerPen = nowPen;

        var legendLabels = new[] { "連續", "動態", "警報", "事件" };
        RecTypeLegend = new (Brush Brush, string Label)[4];
        for (int i = 0; i < 4; i++)
        {
            var b = new SolidColorBrush(Color.FromArgb(200, colors[i].R, colors[i].G, colors[i].B));
            b.Freeze();
            RecTypeLegend[i] = (b, legendLabels[i]);
        }
    }

    // Cached rendering resources
    private readonly Dictionary<string, FormattedText> _labelCache = new();
    private Dictionary<string, List<SegmentRenderData>>? _segmentsByCamera;

    /// <summary>Find segment at given seconds for hover tooltip</summary>
    public SegmentRenderData? FindSegmentAtSeconds(double seconds)
    {
        if (_segmentsByCamera is null || _segmentsByCamera.Count == 0) { return null; }
        var selectedDateStart = SelectedDate.Date;
        foreach (var kvp in _segmentsByCamera)
        {
            foreach (var seg in kvp.Value)
            {
                double segStart = (seg.StartTime - selectedDateStart).TotalSeconds;
                double segEnd = seg.EndTime.HasValue
                    ? (seg.EndTime.Value - selectedDateStart).TotalSeconds
                    : segStart + 60;
                if (seconds >= segStart && seconds <= segEnd)
                {
                    return seg;
                }
            }
        }
        return null;
    }

    // Channel data storage and lookup
    private List<TimelineChannelData> _channels = new();
    private Dictionary<string, TimelineChannelData> _channelsByCameraId = new();
    public List<TimelineChannelData> Channels
    {
        get => _channels;
        set
        {
            _channels = value ?? new();
            var dict = new Dictionary<string, TimelineChannelData>(_channels.Count);
            for (int ci = 0; ci < _channels.Count; ci++)
            {
                var c = _channels[ci];
                if (!string.IsNullOrEmpty(c.CameraId))
                {
                    dict.TryAdd(c.CameraId, c);
                }
            }
            _channelsByCameraId = dict;
            _labelCache.Clear();
            InvalidateTrackHeight();
        }
    }

    private List<SegmentRenderData> _segments = new();
    public List<SegmentRenderData> Segments
    {
        get => _segments;
        set
        {
            _segments = value ?? new();
            _segmentsByCamera = null;
            if (_segments.Count > 0)
            {
                var dict = new Dictionary<string, List<SegmentRenderData>>(_segments.Count);
                for (int si = 0; si < _segments.Count; si++)
                {
                    var seg = _segments[si];
                    if (!dict.TryGetValue(seg.CameraId, out var list))
                    {
                        dict[seg.CameraId] = list = new List<SegmentRenderData>();
                    }
                    list.Add(seg);
                }
                _segmentsByCamera = dict;
            }
        }
    }

    private double GetTotalTrackHeight()
    {
        return _cachedTotalTrackH;
    }

    private void InvalidateTrackHeight()
    {
        _cachedTotalTrackH = Channels.Count > 0
            ? Channels.Count * (TrackHeight + TrackGap)
            : 20;
    }

// ================================================================
    //  Rendering
// ================================================================

    protected override void OnRender(DrawingContext dc)
    {
    // Uses DrawingGroup backing store instead of OnRender to avoid
    // triggering InvalidateVisual() which causes layout/measure/arrange
        base.OnRender(dc);
        dc.DrawDrawing(_staticLayer);
        dc.DrawDrawing(_overlayLayer);
    }

    /// <summary>Full re-render (static + overlay). Call after data changes.</summary>
    public void RenderAll()
    {
        double w = ActualWidth;
        if (w <= 0) { return; }
        _cachedDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using (var ctx = _staticLayer.Open())
        {
            RenderStaticContent(ctx, w);
        }
        using (var ctx = _overlayLayer.Open())
        {
            RenderOverlayContent(ctx, w);
        }
    }

    /// <summary>Overlay-only re-render (playhead, selection, hover). ~10x cheaper.</summary>
    public void RenderOverlay()
    {
        double w = ActualWidth;
        if (w <= 0) { return; }
        _cachedDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using (var ctx = _overlayLayer.Open())
        {
            RenderOverlayContent(ctx, w);
        }
    }

    private void RenderStaticContent(DrawingContext dc, double w)
    {
        double h = ActualHeight;
        if (h <= 0) { h = GetTotalTrackHeight() + RulerHeight + 8; }

        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double bgLeft = Math.Max(0, scrollOff);
        double bgRight = Math.Min(w, scrollOff + vpW);
        dc.DrawRectangle(GetBrush("SurfaceBrush"), null,
            new Rect(bgLeft, 0, Math.Max(1, bgRight - bgLeft), h));

        DrawRuler(dc, w);
        DrawActivityOverview(dc, w);
        DrawTracks(dc, w);
        DrawSegmentMarkers(dc, w);
        DrawBookmarks(dc, w);
    }

    private static readonly Pen NowMarkerPen;
    // Legend brush/labels for recording types
    private static readonly (Brush Brush, string Label)[] RecTypeLegend;

    private void RenderOverlayContent(DrawingContext dc, double w)
    {
        DrawNowMarker(dc, w);
        DrawLoopRegion(dc, w);
        DrawPlayhead(dc, w);
        DrawSelectionBracket(dc, w);
        DrawHoverCursor(dc, w);
        if (ShowTooltip && HoverSegment is not null)
        {
            DrawSegmentTooltip(dc, w);
        }
    }

    private void DrawNowMarker(DrawingContext dc, double canvasW)
    {
        if (SelectedDate.Date != DateTime.Today) return;
        double nowSec = (DateTime.Now - DateTime.Today).TotalSeconds;
        if (nowSec < 0 || nowSec > 86400) return;
        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double cx = nowSec * pixelsPerSec;
        if (cx < scrollOff || cx > scrollOff + vpW) return;
        double trackH = GetTotalTrackHeight();
        dc.DrawLine(NowMarkerPen, new Point(cx, TopMargin + 1), new Point(cx, RulerHeight + trackH));
        var ft = new FormattedText("現在", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 7,
            Brushes.Gray, _cachedDpi);
        double vx = cx - scrollOff;
        double tx = vx - ft.Width / 2 + scrollOff;
        dc.DrawText(ft, new Point(tx, TopMargin + RulerHeight + trackH + 4));
    }

    private void DrawBookmarks(DrawingContext dc, double canvasW)
    {
        if (Bookmarks.Count == 0) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double viewLeftSec = (scrollOff / canvasW) * 86400;
        double viewRightSec = ((scrollOff + vpW) / canvasW) * 86400;

        double trackH = GetTotalTrackHeight();
        double markerY = RulerHeight + 1 + trackH + 4;
        const double diamondSize = 5;

        foreach (var bm in Bookmarks)
        {
            if (bm.Seconds < viewLeftSec || bm.Seconds > viewRightSec) { continue; }

            double cx = bm.Seconds * pixelsPerSec; // canvas-absolute
            if (cx + diamondSize < scrollOff || cx - diamondSize > scrollOff + vpW) { continue; }

            double vx = cx - scrollOff; // viewport-relative for label positioning

            // Diamond marker below track area
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(cx, markerY - diamondSize), true, true);
                ctx.LineTo(new Point(cx + diamondSize, markerY), true, false);
                ctx.LineTo(new Point(cx, markerY + diamondSize), true, false);
                ctx.LineTo(new Point(cx - diamondSize, markerY), true, false);
            }
            dc.DrawGeometry(_bookmarkFillBrush, null, geo);

            // Draw note if present
            if (!string.IsNullOrEmpty(bm.Note))
            {
                var noteText = new FormattedText(bm.Note,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 8, Brushes.White, _cachedDpi);

                double nx = vx - noteText.Width / 2 - 2;
                double ny = markerY + diamondSize + 2;
                if (nx < 2) { nx = 2; }
                if (nx + noteText.Width + 4 > vpW) { nx = vpW - noteText.Width - 6; }

                dc.DrawRectangle(_bookmarkNoteBgBrush, _bookmarkNoteBorderPen,
                    new Rect(nx + scrollOff, ny, noteText.Width + 4, noteText.Height + 2));
                dc.DrawText(noteText, new Point(nx + 2 + scrollOff, ny + 1));
            }
        }
    }

    private void DrawRuler(DrawingContext dc, double canvasW)
    {
        double pixelsPerSec = canvasW / 86400.0;
        double secsPerPixel = 1.0 / pixelsPerSec;

        if (double.IsNaN(pixelsPerSec) || double.IsInfinity(pixelsPerSec) ||
            double.IsNaN(secsPerPixel) || double.IsInfinity(secsPerPixel) ||
            canvasW <= 0 || pixelsPerSec <= 0)
            return;

        var textBrush = GetBrush("SecondaryTextBrush");

        if (_rulerMajorPen is null)
        {
            var rulerBrush = GetBrush("BorderBrush");
            var mp = new Pen(rulerBrush, 1); mp.Freeze();
            _rulerMajorPen = mp;
            var mn = new Pen(rulerBrush, 0.5); mn.Freeze();
            _rulerMinorPen = mn;
        }

        double majorInterval, minorInterval;
        string format;
        if (secsPerPixel <= 1)
        { majorInterval = 60; minorInterval = 10; format = "mm':'ss"; }
        else if (secsPerPixel <= 5)
        { majorInterval = 300; minorInterval = 60; format = "hh':'mm"; }
        else if (secsPerPixel <= 30)
        { majorInterval = 900; minorInterval = 300; format = "hh':'mm"; }
        else if (secsPerPixel <= 120)
        { majorInterval = 3600; minorInterval = 900; format = "hh':'mm"; }
        else
        { majorInterval = 7200; minorInterval = 3600; format = "hh':'mm"; }

        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double viewLeftSec = (scrollOff / canvasW) * 86400;
        double viewRightSec = ((scrollOff + vpW) / canvasW) * 86400;

        double firstMajor = Math.Ceiling(viewLeftSec / majorInterval) * majorInterval;
        double firstMinor = Math.Ceiling(viewLeftSec / minorInterval) * minorInterval;

        dc.DrawLine(_rulerMajorPen, new Point(0, RulerHeight), new Point(canvasW, RulerHeight));

        for (double s = firstMajor; s <= viewRightSec; s += majorInterval)
        {
            double cx = s * pixelsPerSec;
            if (cx + 10 < scrollOff || cx - 10 > scrollOff + vpW) { continue; }

            dc.DrawLine(_rulerMajorPen, new Point(cx, RulerHeight - 6), new Point(cx, RulerHeight));

            // Cache ruler label FormattedText by format string
            if (format != _lastRulerFormat || !ReferenceEquals(textBrush, _lastRulerBrush))
            {
                _rulerTextCache.Clear();
                _lastRulerFormat = format;
                _lastRulerBrush = textBrush;
            }

            string text;
            try
            {
                text = TimeSpan.FromSeconds(s).ToString(format,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FormatException) { continue; }
            catch (OverflowException) { continue; }
            if (!_rulerTextCache.TryGetValue(text, out var formatted))
            {
                formatted = new FormattedText(text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9, textBrush, _cachedDpi);
                _rulerTextCache[text] = formatted;
            }

            double vx = cx - scrollOff;
            double lx = vx - formatted.Width / 2;
            if (lx < 2) { lx = 2; }
            if (lx + formatted.Width > vpW - 2) { lx = vpW - formatted.Width - 2; }
            dc.DrawText(formatted, new Point(lx + scrollOff, TopMargin + 1));
        }

        for (double s = firstMinor; s <= viewRightSec; s += minorInterval)
        {
            double cx = s * pixelsPerSec;
            if (cx < scrollOff || cx > scrollOff + vpW) { continue; }

            if (Math.Abs(s % majorInterval) < 0.01) { continue; }

            dc.DrawLine(_rulerMinorPen, new Point(cx, RulerHeight - 3), new Point(cx, RulerHeight));
        }
    }

    private void DrawActivityOverview(DrawingContext dc, double canvasW)
    {
        if (_channels.Count == 0) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;

        double y = RulerHeight + 2;
        double barH = ActivityBarHeight;

            // Aggregate per-hour activity max across all channels
        Array.Clear(_cachedActivityAgg, 0, 24);
        Array.Clear(_cachedMotionAgg, 0, 24);
        var selectedDateStart = SelectedDate.Date;

        foreach (var ch in _channels)
        {
            if (ch.HourlyActivity is null) { continue; }
            for (int h = 0; h < 24 && h < ch.HourlyActivity.Length; h++)
                if (ch.HourlyActivity[h] > _cachedActivityAgg[h])
                {
                    _cachedActivityAgg[h] = ch.HourlyActivity[h];
                }
        }

            // Motion score aggregation across all segments
        if (_segmentsByCamera is not null)
        {
            foreach (var kvp in _segmentsByCamera)
            {
                foreach (var seg in kvp.Value)
                {
                    if (seg.MotionScore < 0.1) { continue; }
                    double segStartSec = (seg.StartTime - selectedDateStart).TotalSeconds;
                    double segEndSec = seg.EndTime.HasValue
                        ? (seg.EndTime.Value - selectedDateStart).TotalSeconds
                        : segStartSec + 60;
                    int startH = Math.Clamp((int)(segStartSec / 3600), 0, 23);
                    int endH = Math.Clamp((int)(segEndSec / 3600), 0, 23);
                    for (int h = startH; h <= endH; h++)
                    {
                        double hourStart = h * 3600;
                        double hourEnd = (h + 1) * 3600;
                        double overlap = Math.Min(segEndSec, hourEnd) - Math.Max(segStartSec, hourStart);
                        if (overlap > 0)
                        {
                            double motionContrib = (overlap / 3600.0) * seg.MotionScore;
                            if (motionContrib > _cachedMotionAgg[h])
                            {
                                _cachedMotionAgg[h] = motionContrib;
                            }
                        }
                    }
                }
            }
        }

        double maxAct = _cachedActivityAgg.Max();
        double maxMotion = _cachedMotionAgg.Max();

            // Draw activity bar background within viewport
            double abgSx = Math.Max(0, scrollOff);
        double abgEx = Math.Min(canvasW, scrollOff + vpW);
        dc.DrawRectangle(_activityBarBgBrush, null,
            new Rect(abgSx, y, Math.Max(1, abgEx - abgSx), barH));

        if (maxAct <= 0 && maxMotion <= 0) { return; }

            // Draw per-hour bars: recording activity + motion overlay
            for (int h = 0; h < 24; h++)
        {
            double hourStartSecs = h * 3600;
            double hourEndSecs = (h + 1) * 3600;

            double cxStart = hourStartSecs * pixelsPerSec;
            double cxEnd = hourEndSecs * pixelsPerSec;
            if (cxEnd < scrollOff || cxStart > scrollOff + vpW) { continue; }

            double drawSx = Math.Max(cxStart, scrollOff);
            double drawEx = Math.Min(cxEnd, scrollOff + vpW);
            double sw = Math.Max(drawEx - drawSx, 1);

                // Recording activity bar
            if (maxAct > 0)
            {
                int idx = (int)(_cachedActivityAgg[h] / maxAct * 9);
                idx = Math.Clamp(idx, 0, 9);
                dc.DrawRectangle(_activityBrushes[idx], null, new Rect(drawSx, y + 1, sw, barH - 2));
            }

                // Motion overlay bar
            if (maxMotion > 0 && _cachedMotionAgg[h] > 0)
            {
                int mIdx = (int)(_cachedMotionAgg[h] / maxMotion * 9);
                mIdx = Math.Clamp(mIdx, 0, 9);
                dc.DrawRectangle(_motionScoreBrushes[mIdx], null, new Rect(drawSx, y + 1, sw, barH - 2));
            }
        }

        // Recording type legend at right edge of viewport
        const double legendItemW = 20;
        const double legendItemH = 8;
        const double legendGap = 14;
        double legendY = y;
        double legendX = scrollOff + vpW - (legendItemW + legendGap) * 4 + legendGap - 14;
        if (legendX > scrollOff + ChannelLabelWidth)
        {
            for (int li = 0; li < RecTypeLegend.Length; li++)
            {
                double lx = legendX + li * (legendItemW + legendGap);
                dc.DrawRectangle(RecTypeLegend[li].Brush, null,
                    new Rect(lx, legendY + 1, legendItemW, legendItemH));
                var lf = new FormattedText(RecTypeLegend[li].Label,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 6, Brushes.Gray, _cachedDpi);
                double llx = lx + (legendItemW - lf.Width) / 2;
                dc.DrawText(lf, new Point(llx, legendY + 1 + legendItemH + 1));
            }
        }
    }

    private void DrawTracks(DrawingContext dc, double canvasW)
    {
        double y = RulerHeight + 2 + ActivityBarHeight + ActivityBarGap;
        var textBrush = GetBrush("SecondaryTextBrush");

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double viewLeftSec = (scrollOff / canvasW) * 86400;
        double viewRightSec = ((scrollOff + vpW) / canvasW) * 86400;
        var selectedDateStart = SelectedDate.Date;

        // Pre-render channel labels with correct DPI and textBrush
        if (_labelCache.Count == 0 && _channels.Count > 0)
        {
            foreach (var ch in _channels)
            {
                var ft = new FormattedText(
                    $"CH{ch.ChannelNumber:D2}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 8, textBrush, _cachedDpi);
                _labelCache[ch.CameraId] = ft;
            }
        }

        foreach (var channel in _channels)
        {
            if (_labelCache.TryGetValue(channel.CameraId, out var label))
            {
                dc.DrawText(label, new Point(2, y + (TrackHeight - 10) / 2));
            }

            double barX = ChannelLabelWidth;
            // Highlight track if this channel is selected in video grid
            bool isHighlighted = HighlightedCameraId == channel.CameraId;
            // Draw NoRecBrush background bar within viewport bounds
            double barSx = Math.Max(barX, scrollOff);
            double barEx = Math.Min(canvasW, scrollOff + vpW);
            double barW = Math.Max(0, barEx - barSx);
            if (barW > 0)
            {
                var trackBg = isHighlighted ? _highlightTrackBrush : NoRecBrush;
                dc.DrawRectangle(trackBg, null, new Rect(barSx, y, barW, TrackHeight));
                if (!isHighlighted)
                {
                    dc.DrawRectangle(GapHatchBrush, null, new Rect(barSx, y, barW, TrackHeight));
                }
            }
            if (isHighlighted)
            {
                dc.DrawRectangle(null, _highlightBorderPen,
                    new Rect(barSx, y, barW, TrackHeight));
            }

            if (_segmentsByCamera is not null &&
                _segmentsByCamera.TryGetValue(channel.CameraId, out var channelSegs))
            {
                foreach (var seg in channelSegs)
                {
                    double segStartSecs = (seg.StartTime - selectedDateStart).TotalSeconds;
                    double segEndSecs = seg.EndTime.HasValue
                        ? (seg.EndTime.Value - selectedDateStart).TotalSeconds
                        : segStartSecs + 60;

                // Convert to canvas-absolute coordinates
                    double cxStart = segStartSecs * pixelsPerSec;
                    double cxEnd = segEndSecs * pixelsPerSec;
                    if (cxEnd < scrollOff || cxStart > scrollOff + vpW) { continue; }

                // Clamp to viewport bounds (still canvas-absolute)
                    double drawSx = Math.Max(cxStart, scrollOff);
                    double drawEx = Math.Min(cxEnd, scrollOff + vpW);

                // Ensure draw starts after label area (barX), in canvas-absolute
                    double sx = Math.Max(drawSx, barX);
                    double sw = Math.Max(drawEx - sx, 1);

                    int idx = seg.RecordType switch
                    {
                        0 => 0,
                        1 => 1,
                        2 => 2,
                        _ => 3
                    };

                    dc.DrawRectangle(SegmentFillBrushes[idx], SegmentBorderPens[idx],
                        new Rect(sx, y + 1, sw, TrackHeight - 2));

                    // Draw motion score overlay if MotionScore >= 0.1
                    if (seg.MotionScore >= 0.1)
                    {
                        double mIntensity = Math.Clamp(seg.MotionScore, 0, 1);
                        int mIdx = (int)(mIntensity * 9);
                        mIdx = Math.Clamp(mIdx, 0, 9);
                        dc.DrawRectangle(_motionScoreBrushes[mIdx], null,
                            new Rect(sx, y + TrackHeight - 3, sw, 2));
                    }
                }
            }

            y += TrackHeight + TrackGap;
        }
    }

    private void DrawSegmentMarkers(DrawingContext dc, double canvasW)
    {
        if (_segments.Count == 0) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        var selectedDateStart = SelectedDate.Date;
        double trackTop = RulerHeight + 1;
        double trackH = GetTotalTrackHeight();

        // Deduplicate markers at the same second position
        var seenPositions = new HashSet<double>();

        foreach (var seg in _segments)
        {
            double segStartSecs = (seg.StartTime - selectedDateStart).TotalSeconds;
            double cx = segStartSecs * pixelsPerSec;
            if (cx < scrollOff - 1 || cx > scrollOff + vpW) { continue; }

            // Bucket by rounded second to avoid duplicate lines
            double bucket = Math.Round(segStartSecs);
            if (!seenPositions.Add(bucket)) { continue; }

            int idx = seg.RecordType switch { 0 => 0, 1 => 1, 2 => 2, _ => 3 };
            dc.DrawLine(_markerPens[idx], new Point(cx, trackTop), new Point(cx, trackTop + trackH));
        }
    }

    private void DrawPlayhead(DrawingContext dc, double canvasW)
    {
        double playheadSecs = PositionFraction * 86400;
        double cx = playheadSecs * (canvasW / 86400.0); // canvas-absolute
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;

        if (cx < scrollOff || cx > scrollOff + vpW) { return; }

        double vx = cx - scrollOff; // viewport-relative for labels

        dc.DrawLine(PlayheadPen, new Point(cx, RulerHeight + 1), new Point(cx, RulerHeight + GetTotalTrackHeight()));

        if (_playheadTriangleCache is null)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(-6, RulerHeight), true, true);
                ctx.LineTo(new Point(6, RulerHeight), true, false);
                ctx.LineTo(new Point(0, TopMargin - 2), true, false);
            }
            g.Freeze();
            _playheadTriangleCache = g;
        }
        dc.PushTransform(new TranslateTransform(cx, 0));
        dc.DrawGeometry(Brushes.OrangeRed, null, _playheadTriangleCache);
        dc.Pop();

        int totalSec = (int)(PositionFraction * 86400);
        int hh = totalSec / 3600;
        int mm = (totalSec % 3600) / 60;
        int ss = totalSec % 60;
        var timeText = new FormattedText(
            $"{hh:D2}:{mm:D2}:{ss:D2}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White,
            _cachedDpi);

        double tx = vx - timeText.Width / 2;
        if (tx < 2) { tx = 2; }
        if (tx + timeText.Width > vpW - 2) { tx = vpW - timeText.Width - 2; }
        dc.DrawRectangle(Brushes.OrangeRed, null,
            new Rect(tx + scrollOff - 2, TopMargin - 1, timeText.Width + 4, timeText.Height));
        dc.DrawText(timeText, new Point(tx + scrollOff, TopMargin));
    }

    private void DrawSelectionBracket(DrawingContext dc, double canvasW)
    {
        if (SelectionStartSeconds <= 0 || SelectionEndSeconds <= 0) { return; }
        if (Math.Abs(SelectionEndSeconds - SelectionStartSeconds) <= 1) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double trackH = GetTotalTrackHeight();

        double scx = SelectionStartSeconds * pixelsPerSec;
        double ecx = SelectionEndSeconds * pixelsPerSec;
        if (scx > ecx) { (scx, ecx) = (ecx, scx); }

        if (scx > scrollOff + vpW || ecx < scrollOff) { return; }

        double drawSx = Math.Max(scx, scrollOff);
        double drawEx = Math.Min(ecx, scrollOff + vpW);

        // Fill region between brackets
        dc.DrawRectangle(null, _selectionFillPen, new Rect(drawSx, RulerHeight + 1, drawEx - drawSx, trackH));

        // Bracket lines
        if (scx >= scrollOff && scx <= scrollOff + vpW)
            dc.DrawLine(_selectionLinePen, new Point(scx, RulerHeight + 1), new Point(scx, RulerHeight + trackH));
        if (ecx >= scrollOff && ecx <= scrollOff + vpW)
        {
            dc.DrawLine(_selectionLinePen, new Point(ecx, RulerHeight + 1), new Point(ecx, RulerHeight + trackH));
        }

        // Bracket info label: start / end / duration
        double sSec = Math.Min(SelectionStartSeconds, SelectionEndSeconds);
        double eSec = Math.Max(SelectionStartSeconds, SelectionEndSeconds);
        int durSec = (int)(eSec - sSec);
        int sh = (int)sSec / 3600, sm = (int)sSec % 3600 / 60, ss = (int)sSec % 60;
        int eh = (int)eSec / 3600, em = (int)eSec % 3600 / 60, es = (int)eSec % 60;
        int dh = durSec / 3600, dm = durSec % 3600 / 60, ds = durSec % 60;
        string info = $"{sh:D2}:{sm:D2}:{ss:D2} → {eh:D2}:{em:D2}:{es:D2}  ({dh:D2}:{dm:D2}:{ds:D2})";
        var infoFt = new FormattedText(info,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White, _cachedDpi);
        double infoX = Math.Clamp(Math.Min(scx, ecx) + 4, scrollOff + 2, scrollOff + vpW - infoFt.Width - 6);
        double infoY = RulerHeight + trackH + 4;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null,
            new Rect(infoX, infoY, infoFt.Width + 8, infoFt.Height + 4));
        dc.DrawText(infoFt, new Point(infoX + 4, infoY + 2));
    }

    private void DrawLoopRegion(DrawingContext dc, double canvasW)
    {
        if (LoopA < 0 || LoopB < 0) { return; }
        if (Math.Abs(LoopB - LoopA) < 1) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double trackH = GetTotalTrackHeight();

        double acx = LoopA * pixelsPerSec;
        double bcx = LoopB * pixelsPerSec;
        if (acx > bcx) { (acx, bcx) = (bcx, acx); }
        if (acx > bcx) { (acx, bcx) = (bcx, acx); }

        // Draw loop region fill when enabled
        if (LoopEnabled)
        {
            double drawSx = Math.Max(acx, scrollOff);
            double drawEx = Math.Min(bcx, scrollOff + vpW);
            double regionY = RulerHeight + 1;
            if (drawEx > drawSx)
            {
                dc.DrawRectangle(_loopOverlayBrush, null,
                    new Rect(drawSx, regionY, drawEx - drawSx, trackH));
            }
        }

        // A/B marker lines
        var markerPen = LoopEnabled ? _loopMarkerPen : _loopMarkerDisabledPen;
        if (acx >= scrollOff && acx <= scrollOff + vpW)
        {
            dc.DrawLine(markerPen, new Point(acx, RulerHeight + 1), new Point(acx, RulerHeight + trackH));
        }
        if (bcx >= scrollOff && bcx <= scrollOff + vpW)
        {
            dc.DrawLine(markerPen, new Point(bcx, RulerHeight + 1), new Point(bcx, RulerHeight + trackH));
        }

        // A/B label text
        if (_loopLabelA is null || _loopLabelB is null)
        {
            var ci = System.Globalization.CultureInfo.CurrentCulture;
            var tf = new Typeface("Segoe UI");
            _loopLabelA = new FormattedText("A", ci, FlowDirection.LeftToRight, tf, 9, Brushes.LimeGreen, _cachedDpi);
            _loopLabelB = new FormattedText("B", ci, FlowDirection.LeftToRight, tf, 9, Brushes.LimeGreen, _cachedDpi);
        }
        var labelBrush = LoopEnabled ? Brushes.LimeGreen : Brushes.Gray;
        _loopLabelA.SetForegroundBrush(labelBrush);
        _loopLabelB.SetForegroundBrush(labelBrush);

        double avx = acx - scrollOff;
        double bvx = bcx - scrollOff;
        if (avx >= 0 && avx <= vpW)
        {
            dc.DrawText(_loopLabelA, new Point(acx + 2, RulerHeight - 14));
        }
        if (bvx >= 0 && bvx <= vpW)
        {
            dc.DrawText(_loopLabelB, new Point(bcx + 2, RulerHeight - 14));
        }
    }

    private void DrawHoverCursor(DrawingContext dc, double canvasW)
    {
        if (HoverSeconds < 0) { return; }

        double pixelsPerSec = canvasW / 86400.0;
        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double cx = HoverSeconds * pixelsPerSec;

        if (cx < scrollOff || cx > scrollOff + vpW) { return; }

        double trackH = GetTotalTrackHeight();
        double vx = cx - scrollOff;

        // Hover cursor line
        dc.DrawLine(_hoverLinePen, new Point(cx, RulerHeight + 1), new Point(cx, RulerHeight + trackH));

        // Hover time label
        int totalSec = (int)HoverSeconds;
        if (totalSec < 0) { totalSec = 0; }
        int hh = totalSec / 3600;
        int mm = (totalSec % 3600) / 60;
        int ss = totalSec % 60;
        var tt = new FormattedText(
            $"{hh:D2}:{mm:D2}:{ss:D2}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White,
            _cachedDpi);

        double tx = vx - tt.Width / 2;
        if (tx < 2) { tx = 2; }
        if (tx + tt.Width > vpW - 2) { tx = vpW - tt.Width - 2; }
        dc.DrawRectangle(_hoverLabelBgBrush, null,
            new Rect(tx + scrollOff - 2, TopMargin, tt.Width + 4, tt.Height));
        dc.DrawText(tt, new Point(tx + scrollOff, TopMargin));
    }

    private void DrawSegmentTooltip(DrawingContext dc, double canvasW)
    {
        if (HoverSegment is null) { return; }
        var seg = HoverSegment;

        _channelsByCameraId.TryGetValue(seg.CameraId, out var chData);
        var channelName = chData?.CameraName ?? seg.CameraId;
        var typeName = seg.RecordType switch { 0 => "連續", 1 => "位移", 2 => "警報", _ => "AI事件" };
        var duration = seg.EndTime.HasValue
            ? (seg.EndTime.Value - seg.StartTime).ToString(@"mm\:ss")
            : "未知";

        string motionInfo = seg.MotionScore >= 0
            ? $"動作分數: {seg.MotionScore:F2}"
            : "";

        string[] lines = [
            $"CH: {channelName}",
            $"類型: {typeName}",
            $"開始: {seg.StartTime:HH:mm:ss}",
            $"結束: {seg.EndTime:HH:mm:ss}",
            $"時長: {duration}",
            motionInfo
        ];

        double lineH = 14;
        double pad = 6;
        double maxW = 0;
        var formattedLines = new FormattedText[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            formattedLines[i] = new FormattedText(lines[i],
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Brushes.White, _cachedDpi);
            if (formattedLines[i].Width > maxW) { maxW = formattedLines[i].Width; }
        }

        double tooltipW = maxW + pad * 2;
        double tooltipH = lines.Length * lineH + pad * 2;

        double scrollOff = ScrollOffset;
        double vpW = ViewportWidthForCanvas;
        double vpRight = scrollOff + vpW;

        // Position tooltip near hover point within viewport bounds
        double tx = HoverPosition.X + 12;
        double ty = HoverPosition.Y - tooltipH - 8;
        if (tx + tooltipW > vpRight) { tx = vpRight - tooltipW - 4; }
        if (tx < scrollOff) { tx = scrollOff + 4; }
        if (ty < 0) { ty = HoverPosition.Y + 12; }

        dc.DrawRectangle(_tooltipBgBrush, _tooltipBorderPen,
            new Rect(tx, ty, tooltipW, tooltipH));

        for (int i = 0; i < lines.Length; i++)
        {
            dc.DrawText(formattedLines[i], new Point(tx + pad, ty + pad + i * lineH));
        }
    }

    // Cached brushes resolved once from resources to avoid FindResource in OnRender
    private static Brush? _surfaceBrush;
    private static Brush? _textBrush;
    private static Brush? _borderBrush;
    private static DrawingBrush? _gapHatchBrush;

    private static DrawingBrush GapHatchBrush
    {
        get
        {
            if (_gapHatchBrush is null)
            {
                var gb = new DrawingBrush
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 6, 6),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Drawing = new GeometryDrawing
                    {
                        Geometry = new LineGeometry(new Point(0, 0), new Point(6, 6)),
                        Pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1)
                    }
                };
                gb.Freeze();
                _gapHatchBrush = gb;
            }
            return _gapHatchBrush;
        }
    }

    private Brush GetBrush(string resourceKey)
    {
        return resourceKey switch
        {
            "SurfaceBrush" => _surfaceBrush ??= ResolveBrush(resourceKey),
            "SecondaryTextBrush" => _textBrush ??= ResolveBrush(resourceKey),
            "BorderBrush" => _borderBrush ??= ResolveBrush(resourceKey),
            _ => ResolveBrush(resourceKey)
        };
    }

    private Brush ResolveBrush(string key)
    {
        try { return (Brush)FindResource(key); }
        catch { return Brushes.Transparent; }
    }
}

/// <summary>Timeline channel descriptor</summary>
public class TimelineChannelData
{
    public int ChannelNumber { get; set; }
    public string CameraName { get; set; } = "";
    public string CameraId { get; set; } = "";
    public double[]? HourlyActivity { get; set; }
}

/// <summary>Recording segment data from VideoIndexService for timeline rendering</summary>
public class SegmentRenderData
{
    public string CameraId { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    /// <summary>0=Continuous, 1=Motion, 2=Alarm, 3=AI Event</summary>
    public int RecordType { get; set; }
    /// <summary>Motion score 0.0~1.0; -1 if unavailable; derived from AI metadata</summary>
    public double MotionScore { get; set; } = -1;
}

/// <summary>Bookmark at a specific timestamp in the timeline</summary>
public class PlaybackBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Seconds from 00:00 (0~86400)</summary>
    public double Seconds { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Note { get; set; } = "";
    public double NoteWidth { get; set; } = 80;
    public double NoteHeight { get; set; } = 18;
}
