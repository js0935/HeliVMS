using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HeliVMS.Models;
using HeliVMS.Services;

namespace HeliVMS.Controls;

public partial class NxTimeline : UserControl {
    private static readonly Color ColorContinuous = LoadColor("RecordingContinuousColor");
    private static readonly Color ColorMotion = LoadColor("RecordingMotionColor");
    private static readonly Color ColorAlarm = LoadColor("RecordingAlarmColor");
    private static readonly Color ColorAi = LoadColor("RecordingAiColor");
    private static readonly Color ColorNoData = Color.FromArgb(30, 0xFF, 0xFF, 0xFF);

    private static Color LoadColor(string key) {
        if (Application.Current?.TryFindResource(key) is Color c) return c;
        return key switch {
            "RecordingContinuousColor" => Color.FromRgb(0x21, 0x96, 0xF3),
            "RecordingMotionColor" => Color.FromRgb(0xFF, 0x57, 0x22),
            "RecordingAlarmColor" => Color.FromRgb(0xF4, 0x43, 0x36),
            "RecordingAiColor" => Color.FromRgb(0xFF, 0xC1, 0x07),
            _ => Colors.Gray,
        };
    }

    private static readonly double[] ZoomLevels = [86400, 43200, 21600, 10800, 3600];
    private static readonly double[] TickIntervals = [7200, 3600, 1800, 900, 300];

    private DateTime _timelineDay = DateTime.Today;
    private int _zoomIndex;
    private double _positionSeconds;
    private double _viewStartSeconds;
    private List<string> _cameraIds = [];
    private Dictionary<string, string> _cameraNames = [];
    private List<VideoSegment> _segments = [];
    private bool _showContinuous = true;
    private bool _showMotion = true;
    private bool _showAlarm = true;
    private bool _showAi = true;
    private List<PlaybackBookmark> _bookmarks = [];
    private bool _isDragging;
    private bool _isSelecting;
    private Point _selectStart;
    private Point _selectEnd;
    private Rect? _selectionRect;

    public DateTime TimelineDay { get => _timelineDay; set { _timelineDay = value.Date; InvalidateVisual(); Refresh(); } }
    public double ZoomLevel => ZoomLevels[_zoomIndex];
    public double PositionSeconds { get => _positionSeconds; set { _positionSeconds = Math.Clamp(value, 0, 86400); DrawPosition(); PositionChanged?.Invoke(this, _positionSeconds); } }
    public double ViewStartSeconds { get => _viewStartSeconds; set { _viewStartSeconds = Math.Clamp(value, 0, 86400 - ZoomLevels[_zoomIndex]); DrawAll(); } }

    public event EventHandler<double>? PositionChanged;
    public event EventHandler<(DateTime start, DateTime end)?>? SelectionChanged;
    public event EventHandler? GoLiveRequested;
    public event EventHandler<(DateTime start, DateTime end)>? ExportRangeRequested;

    public NxTimeline() {
        InitializeComponent();
        SizeChanged += (_, _) => DrawAll();
    }

    public void LoadSegments(IEnumerable<string> cameraIds, List<VideoSegment> segments, Dictionary<string, string>? cameraNames = null) {
        _cameraIds = cameraIds.ToList();
        _cameraNames = cameraNames ?? [];
        _segments = segments;
        DrawAll();
    }

    public void SetPosition(DateTime time) {
        var secs = (time - _timelineDay).TotalSeconds;
        PositionSeconds = Math.Clamp(secs, 0, 86400);
    }

    public void SetPositionSilent(double seconds) {
        _positionSeconds = Math.Clamp(seconds, 0, 86400);
        DrawPosition();
    }

    private void GoLiveBtn_Click(object sender, RoutedEventArgs e) => GoLiveRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateZoomLabel() {
        var totalSecs = ZoomLevels[_zoomIndex];
        ZoomLabel.Text = totalSecs >= 86400 ? "24h" :
                         totalSecs >= 43200 ? "12h" :
                         totalSecs >= 21600 ? "6h" :
                         totalSecs >= 10800 ? "3h" : "1h";
    }

    public void ZoomIn() {
        if (_zoomIndex > 0) { _zoomIndex--; DrawAll(); }
    }

    public void ZoomOut() {
        if (_zoomIndex < ZoomLevels.Length - 1) { _zoomIndex++; DrawAll(); }
    }

    public (DateTime start, DateTime end)? GetSelection() {
        if (_selectionRect is null) return null;
        var totalSecs = ZoomLevels[_zoomIndex];
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0) return null;
        var x1 = Math.Min(_selectionRect.Value.X, _selectionRect.Value.X + _selectionRect.Value.Width);
        var x2 = Math.Max(_selectionRect.Value.X, _selectionRect.Value.X + _selectionRect.Value.Width);
        var start = _timelineDay.AddSeconds(_viewStartSeconds + x1 / w * totalSecs);
        var end = _timelineDay.AddSeconds(_viewStartSeconds + x2 / w * totalSecs);
        return (start, end);
    }

    public void ClearSelection() {
        _selectionRect = null;
        SelectionCanvas.Children.Clear();
        SelectionChanged?.Invoke(this, null);
    }

    public void ZoomToSelection() {
        var sel = GetSelection();
        if (sel is null) return;
        var (start, end) = sel.Value;
        var duration = (end - start).TotalSeconds;
        for (int i = 0; i < ZoomLevels.Length; i++) {
            if (ZoomLevels[i] <= duration * 1.5 || i == ZoomLevels.Length - 1) {
                _zoomIndex = i;
                _viewStartSeconds = (start - _timelineDay).TotalSeconds;
                ClearSelection();
                DrawAll();
                return;
            }
        }
    }

    public event EventHandler? BookmarkRequested;

    public void Refresh() => DrawAll();

    public void LoadBookmarks(List<PlaybackBookmark> bookmarks) {
        _bookmarks = bookmarks;
        DrawBookmarks();
    }

    public void AddBookmark(PlaybackBookmark bookmark) {
        _bookmarks.Add(bookmark);
        DrawBookmarks();
    }

    public void SetTypeFilter(bool continuous, bool motion, bool alarm, bool ai) {
        _showContinuous = continuous;
        _showMotion = motion;
        _showAlarm = alarm;
        _showAi = ai;
        DrawAll();
    }

    public void InvalidateActivity() {
        if (Dispatcher.CheckAccess())
            DrawActivityOverview();
        else
            Dispatcher.InvokeAsync(DrawActivityOverview);
    }

    private void DrawActivityOverview() {
        ActivityCanvas.Children.Clear();
        var w = ActivityCanvas.ActualWidth;
        if (w <= 0 || _segments.Count == 0) return;

        var totalSecs = ZoomLevels[_zoomIndex];
        var viewEnd = _viewStartSeconds + totalSecs;
        var h = ActivityCanvas.ActualHeight;
        var bucketCount = (int)Math.Ceiling(w);
        var secsPerPixel = totalSecs / w;

        var counts = new int[bucketCount][];
        for (int i = 0; i < bucketCount; i++) counts[i] = new int[4];

        var visibleSegs = _segments.Where(s => IsTypeVisible(s.RecordType));

        foreach (var seg in visibleSegs) {
            var segStart = (seg.StartTime - _timelineDay).TotalSeconds;
            var segEnd = seg.EndTime.HasValue
                ? (seg.EndTime.Value - _timelineDay).TotalSeconds
                : _viewStartSeconds + totalSecs;

            if (segEnd < _viewStartSeconds || segStart > viewEnd) continue;

            int startBucket = Math.Max(0, (int)((segStart - _viewStartSeconds) / secsPerPixel));
            int endBucket = Math.Min(bucketCount - 1, (int)((segEnd - _viewStartSeconds) / secsPerPixel));

            for (int b = startBucket; b <= endBucket; b++) {
                int rt = seg.RecordType;
                if (rt >= 0 && rt < 4) counts[b][rt]++;
            }
        }

        int runStart = -1;
        byte runR = 0, runG = 0, runB = 0;
        byte runA = 0;

        void FlushRun(int end) {
            if (runStart < 0) return;
            double x = runStart;
            double rw = Math.Max(1, end - runStart);
            var rect = new Rectangle {
                Width = rw, Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(runA, runR, runG, runB))
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            ActivityCanvas.Children.Add(rect);
        }

        for (int b = 0; b < bucketCount; b++) {
            var c = counts[b];
            int total = c[0] + c[1] + c[2] + c[3];
            if (total == 0) {
                FlushRun(b);
                runStart = -1;
                continue;
            }

            Color col;
            if (c[2] > 0) col = ColorAlarm;
            else if (c[1] > 0) col = ColorMotion;
            else if (c[3] > 0) col = ColorAi;
            else col = ColorContinuous;

            double opacity = Math.Min(0.15 + total * 0.20, 0.85);
            byte a = (byte)(opacity * 255);

            if (runStart >= 0 && runR == col.R && runG == col.G && runB == col.B && runA == a)
                continue;

            FlushRun(b);
            runStart = b;
            runR = col.R; runG = col.G; runB = col.B; runA = a;
        }
        FlushRun(bucketCount);
    }

    private void ActivityCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        var pos = e.GetPosition(ActivityCanvas);
        var w = ActivityCanvas.ActualWidth;
        if (w <= 0) return;
        var secs = _viewStartSeconds + pos.X / w * ZoomLevels[_zoomIndex];
        PositionSeconds = Math.Clamp(secs, 0, 86400);
        e.Handled = true;
    }

    private void ActivityCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
        _isSelecting = true;
        _selectStart = e.GetPosition(ActivityCanvas);
        _selectEnd = _selectStart;
        ActivityCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void ActivityCanvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) {
        if (!_isSelecting) return;
        _isSelecting = false;
        ActivityCanvas.ReleaseMouseCapture();

        var pos = e.GetPosition(ActivityCanvas);
        _selectEnd = pos;
        var x1 = Math.Min(_selectStart.X, _selectEnd.X);
        var x2 = Math.Max(_selectStart.X, _selectEnd.X);
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0 || Math.Abs(x2 - x1) < 5) { ClearSelection(); return; }

        var totalSecs = ZoomLevels[_zoomIndex];
        var startSecs = _viewStartSeconds + x1 / w * totalSecs;
        var endSecs = _viewStartSeconds + x2 / w * totalSecs;
        _selectionRect = new Rect(x1, 0, x2 - x1, CameraRowsCanvas.ActualHeight);

        DrawSelection();
        SelectionChanged?.Invoke(this, (_timelineDay.AddSeconds(startSecs), _timelineDay.AddSeconds(endSecs)));
        e.Handled = true;
    }

    private void ActivityCanvas_MouseMove(object sender, MouseEventArgs e) {
        if (_isSelecting && e.RightButton == MouseButtonState.Pressed) {
            _selectEnd = e.GetPosition(ActivityCanvas);
            DrawSelection();
        }
        UpdateActivityTooltip(e.GetPosition(ActivityCanvas));
    }

    private void ActivityCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
        Canvas_PreviewMouseWheel(sender, e);
    }

    private void UpdateActivityTooltip(Point pos) {
        var w = ActivityCanvas.ActualWidth;
        if (w <= 0) return;
        var totalSecs = ZoomLevels[_zoomIndex];
        var secsPerPixel = totalSecs / w;
        var bucketStart = _viewStartSeconds + pos.X / w * totalSecs;
        var bucketEnd = bucketStart + secsPerPixel;

        int[] c = new int[4];
        foreach (var seg in _segments) {
            if (!IsTypeVisible(seg.RecordType)) continue;
            var segStart = (seg.StartTime - _timelineDay).TotalSeconds;
            var segEnd = seg.EndTime.HasValue
                ? (seg.EndTime.Value - _timelineDay).TotalSeconds
                : _viewStartSeconds + totalSecs;
            if (segEnd < bucketStart || segStart > bucketEnd) continue;
            int rt = seg.RecordType;
            if (rt >= 0 && rt < 4) c[rt]++;
        }

        int total = c[0] + c[1] + c[2] + c[3];
        if (total == 0) {
            ActivityCanvas.ToolTip = null;
            return;
        }

        var time = _timelineDay.AddSeconds(bucketStart);
        var parts = new List<string>();
        if (c[0] > 0) parts.Add($"連續 {c[0]}");
        if (c[1] > 0) parts.Add($"位移 {c[1]}");
        if (c[2] > 0) parts.Add($"警報 {c[2]}");
        if (c[3] > 0) parts.Add($"AI {c[3]}");
        ActivityCanvas.ToolTip = $"{time:HH:mm:ss} — {total} 段 ({string.Join(" / ", parts)})";
    }

    private void DrawAll() {
        DrawActivityOverview();
        DrawCameraRows();
        DrawTimeScale();
        DrawPosition();
        DrawBookmarks();
        UpdateZoomLabel();
    }

    private double SecsToX(double secs) {
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0 || ZoomLevels[_zoomIndex] <= 0) return 0;
        return (secs - _viewStartSeconds) / ZoomLevels[_zoomIndex] * w;
    }

    private double XToSecs(double x) {
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0) return 0;
        return _viewStartSeconds + x / w * ZoomLevels[_zoomIndex];
    }

    private bool IsTypeVisible(int recordType) => recordType switch {
        0 => _showContinuous,
        1 => _showMotion,
        2 => _showAlarm,
        3 => _showAi,
        _ => true,
    };

    private Color GetColorForRecordType(int recordType) => recordType switch {
        0 => ColorContinuous,
        1 => ColorMotion,
        2 => ColorAlarm,
        3 => ColorAi,
        _ => ColorContinuous,
    };

    private void DrawCameraRows() {
        CameraRowsCanvas.Children.Clear();
        var labelWidth = 60.0;
        var w = CameraRowsCanvas.ActualWidth;
        var h = CameraRowsCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _cameraIds.Count == 0) return;

        var rowH = Math.Max(4, h / (_cameraIds.Count + 1) - 1);
        var totalSecs = ZoomLevels[_zoomIndex];
        var viewEnd = _viewStartSeconds + totalSecs;

        foreach (var (camId, idx) in _cameraIds.Select((id, i) => (id, i))) {
            var y = idx * (rowH + 1);
            var name = _cameraNames.GetValueOrDefault(camId, camId.Length > 8 ? camId[..8] : camId);
            var nameLabel = new TextBlock {
                Text = name, FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xFF, 0xFF)),
                Width = labelWidth - 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(nameLabel, 2); Canvas.SetTop(nameLabel, y + 1);
            CameraRowsCanvas.Children.Add(nameLabel);
            var camSegs = _segments.Where(s => s.CameraId == camId && IsTypeVisible(s.RecordType)).ToList();

            if (camSegs.Count == 0) {
                var bg = new Rectangle { Fill = new SolidColorBrush(ColorNoData), Width = w - labelWidth, Height = rowH };
                Canvas.SetLeft(bg, labelWidth); Canvas.SetTop(bg, y);
                CameraRowsCanvas.Children.Add(bg);
                continue;
            }

            foreach (var seg in camSegs) {
                var segStart = (seg.StartTime - _timelineDay).TotalSeconds;
                var segEnd = seg.EndTime.HasValue ? (seg.EndTime.Value - _timelineDay).TotalSeconds : _viewStartSeconds + totalSecs;

                if (segEnd < _viewStartSeconds || segStart > viewEnd) continue;

                var x1 = Math.Max(0, SecsToX(segStart));
                var x2 = Math.Min(w, SecsToX(segEnd));
                var rectW = Math.Max(1, x2 - x1);
                var availW = w - labelWidth;
                var rx1 = Math.Min(availW, x1);

                var color = GetColorForRecordType(seg.RecordType);
                var rect = new Rectangle {
                    Fill = new SolidColorBrush(Color.FromArgb(180, color.R, color.G, color.B)),
                    Width = Math.Min(rectW, availW - rx1), Height = rowH,
                    ToolTip = $"{seg.CameraId}: {seg.StartTime:HH:mm}\u2013{(seg.EndTime.HasValue ? seg.EndTime.Value.ToString("HH:mm") : "錄影中")} ({(seg.RecordType == 0 ? "連續" : seg.RecordType == 1 ? "位移" : seg.RecordType == 2 ? "警報" : "AI")})"
                };
                Canvas.SetLeft(rect, rx1 + labelWidth); Canvas.SetTop(rect, y);
                CameraRowsCanvas.Children.Add(rect);
            }
        }

        // All Cameras merged row
        var mergedY = _cameraIds.Count * (rowH + 1);
        var merged = _segments.Where(s => IsTypeVisible(s.RecordType)).GroupBy(s => (s.StartTime.Ticks / TimeSpan.TicksPerSecond, s.RecordType));
        foreach (var group in merged) {
            var seg = group.First();
            var segStart = (seg.StartTime - _timelineDay).TotalSeconds;
            var segEnd = seg.EndTime.HasValue ? (seg.EndTime.Value - _timelineDay).TotalSeconds : _viewStartSeconds + totalSecs;
            if (segEnd < _viewStartSeconds || segStart > viewEnd) continue;
            var x1 = Math.Max(0, SecsToX(segStart));
            var x2 = Math.Min(w, SecsToX(segEnd));
            var rectW = Math.Max(1, x2 - x1);
            var color = GetColorForRecordType(seg.RecordType);
            var rect = new Rectangle {
                Fill = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                Width = Math.Min(rectW, w - labelWidth - x1), Height = rowH
            };
            Canvas.SetLeft(rect, x1 + labelWidth); Canvas.SetTop(rect, mergedY);
            CameraRowsCanvas.Children.Add(rect);
        }
    }

    private void DrawTimeScale() {
        TimeScaleCanvas.Children.Clear();
        var w = TimeScaleCanvas.ActualWidth;
        var h = TimeScaleCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var tickInterval = TickIntervals[_zoomIndex];
        var totalSecs = ZoomLevels[_zoomIndex];
        var viewEnd = _viewStartSeconds + totalSecs;

        var firstTick = Math.Ceiling(_viewStartSeconds / tickInterval) * tickInterval;
        for (var t = firstTick; t < viewEnd; t += tickInterval) {
            var x = SecsToX(t);
            if (x < 0 || x > w) continue;
            var line = new Line {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = new SolidColorBrush(Color.FromArgb(60, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 0.5
            };
            TimeScaleCanvas.Children.Add(line);

            var time = TimeSpan.FromSeconds(t);
            var label = new TextBlock {
                Text = time.ToString(@"hh\:mm"),
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xFF, 0xFF)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, x + 2); Canvas.SetTop(label, 0);
            TimeScaleCanvas.Children.Add(label);
        }
    }

    private void DrawPosition() {
        PositionCanvas.Children.Clear();
        var w = CameraRowsCanvas.ActualWidth;
        var h = CameraRowsCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var x = SecsToX(_positionSeconds);
        if (x < 0 || x > w) return;

        var line = new Line {
            X1 = x, Y1 = 0, X2 = x, Y2 = h + TimeScaleCanvas.ActualHeight,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5
        };
        PositionCanvas.Children.Add(line);

        var handle = new Ellipse {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Colors.White),
            Margin = new Thickness(-4, 0, 0, 0)
        };
        Canvas.SetLeft(handle, x); Canvas.SetTop(handle, -4);
        PositionCanvas.Children.Add(handle);
    }

    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
        var delta = e.Delta > 0 ? -1 : 1;
        var newIndex = Math.Clamp(_zoomIndex + delta, 0, ZoomLevels.Length - 1);
        if (newIndex == _zoomIndex) return;

        var oldTotal = ZoomLevels[_zoomIndex];
        var newTotal = ZoomLevels[newIndex];
        var mouseX = e.GetPosition(CameraRowsCanvas).X;
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0) { _zoomIndex = newIndex; DrawAll(); return; }

        var mouseSecs = XToSecs(mouseX);
        _zoomIndex = newIndex;
        _viewStartSeconds = Math.Clamp(mouseSecs - mouseX / w * newTotal, 0, 86400 - newTotal);
        DrawAll();
        e.Handled = true;
    }

    private void Canvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
        _isSelecting = true;
        _selectStart = e.GetPosition(CameraRowsCanvas);
        _selectEnd = _selectStart;
        CameraRowsCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) {
        if (!_isSelecting) return;
        _isSelecting = false;
        CameraRowsCanvas.ReleaseMouseCapture();

        var pos = e.GetPosition(CameraRowsCanvas);
        _selectEnd = pos;
        var x1 = Math.Min(_selectStart.X, _selectEnd.X);
        var x2 = Math.Max(_selectStart.X, _selectEnd.X);
        var w = CameraRowsCanvas.ActualWidth;
        if (w <= 0 || Math.Abs(x2 - x1) < 5) { ClearSelection(); return; }

        var totalSecs = ZoomLevels[_zoomIndex];
        var startSecs = _viewStartSeconds + x1 / w * totalSecs;
        var endSecs = _viewStartSeconds + x2 / w * totalSecs;
        _selectionRect = new Rect(x1, 0, x2 - x1, CameraRowsCanvas.ActualHeight);

        DrawSelection();
        SelectionChanged?.Invoke(this, (_timelineDay.AddSeconds(startSecs), _timelineDay.AddSeconds(endSecs)));
        e.Handled = true;
    }

    private void Canvas_PreviewMouseMove(object sender, MouseEventArgs e) {
        if (_isSelecting && e.RightButton == MouseButtonState.Pressed) {
            _selectEnd = e.GetPosition(CameraRowsCanvas);
            DrawSelection();
        }
    }

    private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        _isDragging = true;
        CameraRowsCanvas.CaptureMouse();
        var pos = e.GetPosition(CameraRowsCanvas);
        PositionSeconds = Math.Clamp(XToSecs(pos.X), 0, 86400);
        e.Handled = true;
    }

    private void Canvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isDragging) {
            _isDragging = false;
            CameraRowsCanvas.ReleaseMouseCapture();
        }
    }

    private void DrawSelection() {
        SelectionCanvas.Children.Clear();
        if (!_isSelecting && _selectionRect is null) return;

        var selRect = _selectionRect;
        var x1 = _isSelecting ? Math.Min(_selectStart.X, _selectEnd.X) : selRect!.Value.X;
        var x2 = _isSelecting ? Math.Max(_selectStart.X, _selectEnd.X) : selRect!.Value.X + selRect!.Value.Width;
        var w = Math.Max(1, x2 - x1);
        var h = SelectionCanvas.ActualHeight;
        if (h <= 0) { h = ActivityCanvas.ActualHeight + CameraRowsCanvas.ActualHeight + TimeScaleCanvas.ActualHeight; }
        if (h <= 0) return;

        var overlay = new Rectangle {
            Fill = new SolidColorBrush(Color.FromArgb(60, 0x21, 0x96, 0xF3)),
            Width = w, Height = h
        };
        Canvas.SetLeft(overlay, x1); Canvas.SetTop(overlay, 0);
        SelectionCanvas.Children.Add(overlay);
    }

    private void CtxZoomToSel(object sender, RoutedEventArgs e) => ZoomToSelection();
    private void CtxClearSel(object sender, RoutedEventArgs e) => ClearSelection();
    private void CtxAddBookmark(object sender, RoutedEventArgs e) => BookmarkRequested?.Invoke(this, EventArgs.Empty);
    private void CtxExportRange(object sender, RoutedEventArgs e) {
        var sel = GetSelection();
        if (sel.HasValue) ExportRangeRequested?.Invoke(this, sel.Value);
    }

    private void DrawBookmarks() {
        BookmarksCanvas.Children.Clear();
        var w = CameraRowsCanvas.ActualWidth;
        var h = CameraRowsCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _bookmarks.Count == 0) return;

        var totalSecs = ZoomLevels[_zoomIndex];
        var viewEnd = _viewStartSeconds + totalSecs;

        foreach (var bm in _bookmarks) {
            if (bm.Seconds < _viewStartSeconds || bm.Seconds > viewEnd) continue;
            var x = SecsToX(bm.Seconds);
            if (x < 0 || x > w) continue;

            var poly = new Polygon {
                Fill = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xC1, 0x07)),
                Stroke = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                StrokeThickness = 0.5,
                Points = new PointCollection([
                    new Point(x, 0),
                    new Point(x + 5, 4),
                    new Point(x, 8),
                    new Point(x - 5, 4),
                ]),
                ToolTip = $"{bm.Note}\n{TimeSpan.FromSeconds(bm.Seconds):hh\\:mm\\:ss}"
            };
            BookmarksCanvas.Children.Add(poly);

            var label = new TextBlock {
                Text = bm.Note,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 0xFF, 0xC1, 0x07)),
                Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, x + 2); Canvas.SetTop(label, 2);
            BookmarksCanvas.Children.Add(label);
        }
    }

    private void Scale_PreviewMouseWheel(object sender, MouseWheelEventArgs e) {
        Canvas_PreviewMouseWheel(sender, e);
    }

    private void Scale_MouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Left) {
            var pos = e.GetPosition(TimeScaleCanvas);
            PositionSeconds = Math.Clamp(XToSecs(pos.X), 0, 86400);
        } else if (e.ChangedButton == MouseButton.Right) {
            if (e.ClickCount == 2) {
                _zoomIndex = 0;
                _viewStartSeconds = 0;
                DrawAll();
                e.Handled = true;
            }
        }
    }
}
