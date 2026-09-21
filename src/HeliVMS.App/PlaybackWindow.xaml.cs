using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>回放視窗（M3/M8）：挑選頻道/日期/錄影片段，以 PlaybackSession 播放；含當日時間軸帶與進度列跳轉。</summary>
public partial class PlaybackWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly SegmentRepository _segments;
    private readonly long? _focusChannelId;
    private readonly DateTime? _focusUtc;

    private PlaybackSession? _session;
    private SegmentRecord? _current;
    private double _basePos;
    private double _posInside;
    private bool _stopping;
    private long _sessionVersion;
    private WriteableBitmap? _bitmap;
    private int _lastWidth;
    private int _lastHeight;
    private IReadOnlyList<SegmentItem> _bandSegments = [];
    private List<AlarmEventRecord> _events = [];
    private HeliVMS.Storage.PlaybackTimeline? _timeline;
    private bool _seeking;
    private bool _pendingFramePause;
    private readonly Rectangle _cursor = new() { Width = 2, Fill = Brushes.White, IsHitTestVisible = false };

    private sealed record SegmentItem(SegmentRecord Segment, string StartLabel, string DurationLabel, string SizeLabel);

    private sealed record EventItem(long Id, string StartLabel, string TypeLabel, string DetailLabel);

    public PlaybackWindow(SqliteStore store, long? focusChannelId = null, DateTime? focusUtc = null)
    {
        _store = store;
        _focusChannelId = focusChannelId;
        _focusUtc = focusUtc;
        _channels = new ChannelRepository(_store);
        _segments = new SegmentRepository(_store);
        InitializeComponent();
        DatePick.SelectedDate = DateTime.Today;
        LoadChannels();
        BandCanvas.Children.Add(_cursor);
        Canvas.SetZIndex(_cursor, 10);
    }

    private void LoadChannels()
    {
        ChannelCombo.ItemsSource = _channels.List();
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        if (ChannelCombo.Items.Count > 0)
        {
            if (_focusChannelId is { } focus && _channels.List().FirstOrDefault(c => c.Id == focus) is { } match)
            {
                ChannelCombo.SelectedItem = match;
            }
            else
            {
                ChannelCombo.SelectedIndex = 0;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSegments();
        if (_focusUtc is { } focus)
        {
            foreach (var item in _bandSegments)
            {
                var seg = item.Segment;
                var end = seg.EndUtc ?? seg.StartUtc.AddSeconds(seg.DurationSec ?? 10);
                if (seg.StartUtc <= focus && end >= focus)
                {
                    SegmentList.SelectedItem = item;
                    PlayFrom(seg, Math.Max(0, (focus - seg.StartUtc).TotalSeconds));
                    break;
                }
            }
        }
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSegments();

    private void OnDateChanged(object sender, SelectionChangedEventArgs e) => LoadSegments();

    private void OnLoadClicked(object sender, RoutedEventArgs e) => LoadSegments();

    /// <summary>依頻道與日期載入當日錄影片段（Final 限定）。</summary>
    private void LoadSegments()
    {
        StopPlayback();
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            SegmentList.ItemsSource = null;
            _bandSegments = [];
            RenderBlocks();
            LoadHint.Text = "沒有頻道，請先在上層視窗加入。";
            return;
        }

        if (DatePick.SelectedDate is not DateTime day)
        {
            return;
        }

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(day.Date);
        var endUtc = startUtc.AddDays(1);

        try
        {
            var segs = _segments.ListByRange(ch.Id, "main", startUtc, endUtc)
                .Where(s => s.Status == SegmentStatus.Final)
                .OrderBy(s => s.StartUtc)
                .Select(s => new SegmentItem(
                    s,
                    TimeZoneInfo.ConvertTimeFromUtc(s.StartUtc, TimeZoneInfo.Local).ToString("HH:mm:ss"),
                    s.DurationSec is double d ? $"{d:0.#}秒" : "-",
                    s.SizeBytes > 0 ? $"{s.SizeBytes / 1024d:0}KB" : "-"))
                .ToList();
            _bandSegments = segs;
            SegmentList.ItemsSource = segs;

            var startEvt = TimeZoneInfo.ConvertTimeToUtc(day.Date);
            var endEvt = startEvt.AddDays(1);
            _events = new AlarmEventRepository(_store).ListByRange(ch.Id, startEvt, endEvt)
                .OrderBy(e => e.StartUtc)
                .ToList();

            var camNames = new ChannelRepository(_store).List()
                .ToDictionary(c => c.Id, c => c.Name);

            EventList.ItemsSource = _events
                .Select(e =>
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, TimeZoneInfo.Local);
                    return new EventItem(
                        e.Id,
                        local.ToString("HH:mm:ss"),
                        e.EventType,
                        camNames.TryGetValue(e.ChannelId, out var cn) ? cn : $"ch {e.ChannelId}");
                })
                .ToList();
            EventCountText.Text = _events.Count > 0 ? $" 共 {_events.Count} 條" : "";

            RenderBlocks();
            LoadHint.Text = _events.Count > 0
                ? $"當日 {segs.Count} 段、{_events.Count} 事件。"
                : $"當日 {segs.Count} 段。";
        }
        catch (Exception ex)
        {
            LoadHint.Text = $"載入失敗：{ex.Message}";
        }
    }

    private void OnSegmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SegmentList.SelectedItem is SegmentItem item)
        {
            PlayFrom(item.Segment, 0);
        }
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not EventItem evt)
        {
            return;
        }

        var rec = _events.FirstOrDefault(x => x.Id == evt.Id);
        if (rec is null)
        {
            return;
        }

        var band = _bandSegments
            .FirstOrDefault(b => b.Segment.StartUtc <= rec.StartUtc
                              && rec.StartUtc <= b.Segment.StartUtc.AddSeconds(b.Segment.DurationSec ?? 0));
        if (band is not null)
        {
            PlayFrom(band.Segment, (rec.StartUtc - band.Segment.StartUtc).TotalSeconds);
        }
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_session is null || _stopping)
        {
            return;
        }

        if (PlayPauseButton.Content.ToString() == "播放")
        {
            var pos = _posInside;
            StopPlayback(keepSelection: true);
            if (_current is not null)
            {
                PlayFrom(_current, pos);
            }
        }
        else
        {
            _session.Stop();
            StopButton.IsEnabled = true;
            PlayPauseButton.Content = "播放";
            StatusText.Text = "已暫停";
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e) => StopPlayback();

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is not null && !_stopping)
        {
            _session.Speed = GetSpeed();
        }
    }

    /// <summary>全視窗鍵盤快捷鍵（M17，對齊 ARCHITECTURE §8.5）：Space 播放/暫停、方向鍵±10秒、Ctrl 方向鍵±1分、Shift 方向鍵±10分、F 逐幀、Esc 停止。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.ComboBox
            or System.Windows.Controls.DatePicker
            or System.Windows.Controls.Slider)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Space:
                if (_current is not null)
                {
                    e.Handled = true;
                    OnPlayPauseClicked(this, new RoutedEventArgs());
                }
                break;

            case Key.Left:
                e.Handled = true;
                SeekBy(ctrl ? -60 : shift ? -600 : -10);
                break;

            case Key.Right:
                e.Handled = true;
                SeekBy(ctrl ? 60 : shift ? 600 : 10);
                break;

            case Key.F when !ctrl && !shift:
                e.Handled = true;
                StepFrame();
                break;

            case Key.Escape:
                e.Handled = true;
                StopPlayback();
                break;
        }
    }

    /// <summary>依目前位置向後/向前跳轉指定秒數；越過片段邊界時自動跨段，最後 clamp 於當日整段範圍。</summary>
    private void SeekBy(double seconds)
    {
        if (_current is null || _bandSegments.Count == 0)
        {
            return;
        }

        var targetUtc = _current.StartUtc.AddSeconds(_posInside + seconds);
        foreach (var item in _bandSegments)
        {
            var seg = item.Segment;
            var end = seg.EndUtc ?? seg.StartUtc.AddSeconds(seg.DurationSec ?? 10);
            if (seg.StartUtc <= targetUtc && targetUtc <= end)
            {
                SegmentList.SelectedItem = item;
                PlayFrom(seg, Math.Max(0, (targetUtc - seg.StartUtc).TotalSeconds));
                return;
            }
        }

        var first = _bandSegments[0].Segment;
        if (targetUtc < first.StartUtc)
        {
            SegmentList.SelectedItem = _bandSegments[0];
            PlayFrom(first, 0);
            return;
        }

        var lastItem = _bandSegments[^1];
        var last = lastItem.Segment;
        SegmentList.SelectedItem = lastItem;
        PlayFrom(last, Math.Max(0, (last.DurationSec ?? 1) - 0.001));
    }

    /// <summary>逐幀（M17）：播放中先暫停，再向前推進約一幀（依 15fps 估約 66ms），並於首幀到達後再次暫停。</summary>
    private void StepFrame()
    {
        if (_current is null)
        {
            return;
        }

        if (_session is not null && !_stopping && PlayPauseButton.Content.ToString() == "暫停")
        {
            OnPlayPauseClicked(this, new RoutedEventArgs());
        }

        var dur = _current.DurationSec ?? 0;
        if (dur <= 0)
        {
            return;
        }

        var frame = 1.0 / 15.0;
        var target = Math.Min(_posInside + frame, dur);
        PlayFrom(_current, target);
        _pendingFramePause = true;
    }

    private double GetSpeed() => SpeedCombo.SelectedIndex switch
    {
        0 => 0.5,
        1 => 1,
        2 => 2,
        3 => 4,
        4 => 8,
        _ => 1,
    };

    private void PlayFrom(SegmentRecord seg, double seekSeconds)
    {
        StopPlayback(keepSelection: true);
        _current = seg;
        _basePos = seekSeconds;
        _posInside = seekSeconds;
        _stopping = false;
        var version = ++_sessionVersion;
        SetPlayingUi();

        var dur = seg.DurationSec ?? 0;
        SeekSlider.IsEnabled = dur > 0;
        SeekSlider.Value = dur > 0 ? Math.Min(seekSeconds / dur, 1) * 10000 : 0;
        UpdateCursor(seekSeconds);

        var session = new PlaybackSession(seg) { Speed = GetSpeed() };
        _session = session;
        session.FrameDecoded += (_, frame) => OnFrame(version, seg, frame);
        session.Ended += (_, args) => OnEnded(version, seg, args.Completed);
        session.PlaybackError += (_, ex) => Dispatcher.BeginInvoke(() =>
        {
            if (version != _sessionVersion)
            {
                return;
            }

            StatusText.Text = $"解碼錯誤：{ex.Message}";
        });

        _ = session.PlayAsync(seekSeconds);
    }

    private void OnFrame(long version, SegmentRecord seg, VideoFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _sessionVersion || _stopping)
            {
                return;
            }

            EnsureBitmap(frame);
            if (_bitmap is null)
            {
                return;
            }

            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Width * 3,
                0);

            _posInside = _basePos + frame.PtsMs / 1000.0;
            TimeText.Text = $"{FormatTime(_posInside)} / {FormatTime(seg.DurationSec ?? 0)}";
            StatusText.Text = $"播放中（{GetSpeed():0.##}×）";
            if (!_seeking && (seg.DurationSec ?? 0) > 0)
            {
                SeekSlider.Value = Math.Min(_posInside / (seg.DurationSec ?? 1), 1) * 10000;
            }

            UpdateCursor(_posInside);

            if (_pendingFramePause)
            {
                _pendingFramePause = false;
                StopPlayback(keepSelection: true);
                StatusText.Text = "已暫停";
            }
        });
    }

    private void OnEnded(long version, SegmentRecord seg, bool completed)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (version != _sessionVersion)
            {
                return;
            }

            _session = null;

            if (completed && AutoAdvanceCheck.IsChecked == true)
            {
                var next = FindNextSegment(seg);
                if (next is not null)
                {
                    _posInside = 0;
                    PlayFrom(next, 0);
                    return;
                }
            }

            SetIdleUi();
            TimeText.Text = $"{FormatTime(_posInside)} / {FormatTime(seg.DurationSec ?? 0)}";
            StatusText.Text = completed
                ? (AutoAdvanceCheck.IsChecked == true ? "播放完畢（無後續片段）" : "已停止播放")
                : "播放失敗";
        });
    }

    private SegmentRecord? FindNextSegment(SegmentRecord current)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            return null;
        }

        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(current.StartUtc.ToLocalTime().Date);

        try
        {
            return _segments.ListByRange(ch.Id, current.Stream, dayStartUtc, dayStartUtc.AddDays(1))
                .Where(s => s.Status == SegmentStatus.Final && s.StartUtc > current.StartUtc)
                .OrderBy(s => s.StartUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void EnsureBitmap(VideoFrame frame)
    {
        if (_bitmap is not null && _lastWidth == frame.Width && _lastHeight == frame.Height)
        {
            return;
        }

        _lastWidth = frame.Width;
        _lastHeight = frame.Height;
        _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
        PlayerImage.Source = _bitmap;
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void StopPlayback(bool keepSelection = false)
    {
        _stopping = true;
        var session = _session;
        _session = null;
        _sessionVersion++;
        session?.Stop();
        DisposeQuietly(session);
        SetIdleUi();
    }

    /// <summary>當日時間軸帶尺寸改變時重新繪製。</summary>
    private void OnBandSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderBlocks();
        UpdateCursor(_posInside);
    }

    /// <summary>繪製當日時間軸帶（M84）：依 PlaybackTimeline L0 的 fractions 畫每段藍色區塊＋事件標記＋覆蓋率標籤。</summary>
    private void RenderBlocks()
    {
        for (var i = BandCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(BandCanvas.Children[i], _cursor))
            {
                BandCanvas.Children.RemoveAt(i);
            }
        }

        var hasData = _bandSegments.Count > 0 || _events.Count > 0;
        if (!hasData)
        {
            CoverageText.Text = string.Empty;
            _timeline = null;
            return;
        }

        var dayStart = DayStartUtc();
        _timeline = PlaybackTimelineBuilder.Build(
            _bandSegments.Select(i => i.Segment).ToArray(),
            _events,
            dayStart);

        if (BandCanvas.ActualWidth > 0)
        {
            var fill = (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0xC9)).GetAsFrozen();
            foreach (var bar in _timeline.Bars)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(2, bar.WidthFraction * BandCanvas.ActualWidth),
                    Height = 26,
                    Fill = fill,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, Math.Max(0, bar.LeftFraction) * BandCanvas.ActualWidth);
                Canvas.SetTop(rect, 2);
                BandCanvas.Children.Add(rect);
            }
        }

        var coveragePct = (int)Math.Round((1 - _timeline.GapFraction) * 100);
        CoverageText.Text = $"覆蓋 {coveragePct}% · 未涵蓋 {_timeline.GapCount} 區間";
        AutomationProperties.SetName(CoverageText, $"COV:{coveragePct}:{_timeline.GapCount}:{_timeline.Bars.Count}");

        RenderMarkers();
    }

    /// <summary>時間軸帶當日零時（UTC）。</summary>
    private DateTime DayStartUtc()
    {
        if (_bandSegments.Count > 0)
        {
            return TimeZoneInfo.ConvertTimeToUtc(_bandSegments[0].Segment.StartUtc.ToLocalTime().Date);
        }

        if (_events.Count > 0)
        {
            return TimeZoneInfo.ConvertTimeToUtc(_events[0].StartUtc.ToLocalTime().Date);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.Now.Date);
    }

    /// <summary>一天內比例（0..1）。</summary>
    private double FracOfDay(DateTime utc) => Math.Max(0, Math.Min(1, (utc - DayStartUtc()).TotalMinutes / 1440.0));

    /// <summary>繪製當日事件標記（M84）：以 PlaybackTimeline.Markers 的 XFraction 定位；點擊帶上對應時刻即跳播（OnBandClicked）。</summary>
    private void RenderMarkers()
    {
        var markers = _timeline?.Markers;
        if (markers is null || markers.Count == 0 || BandCanvas.ActualWidth <= 0)
        {
            return;
        }

        foreach (var marker in markers)
        {
            var rec = _events.FirstOrDefault(e => e.Id == marker.EventId);
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = MarkerBrush(marker.Kind),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                IsHitTestVisible = true,
                ToolTip = $"{TimeZoneInfo.ConvertTimeFromUtc(marker.Utc, TimeZoneInfo.Local):HH:mm:ss} 「{marker.Kind}」{rec?.Detail}",
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            Canvas.SetLeft(dot, marker.XFraction * BandCanvas.ActualWidth - 5);
            Canvas.SetTop(dot, 10);
            BandCanvas.Children.Add(dot);
        }
    }

    private static Brush MarkerBrush(string eventType) => eventType switch
    {
        "ai_person" => (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x3C)).GetAsFrozen(),
        "ai_vehicle" => (Brush)new SolidColorBrush(Color.FromRgb(0x3C, 0xA0, 0xE5)).GetAsFrozen(),
        "motion" => (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0xC8, 0x3C)).GetAsFrozen(),
        "offline" => (Brush)new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB5)).GetAsFrozen(),
        _ => (Brush)new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7A)).GetAsFrozen(),
    };

    /// <summary>點擊時間軸帶：定位到該時刻所在片段並從該處播放。</summary>
    private void OnBandClicked(object sender, MouseButtonEventArgs e)
    {
        if (_bandSegments.Count == 0 || BandCanvas.ActualWidth <= 0)
        {
            return;
        }

        var frac = Math.Max(0, Math.Min(1, e.GetPosition(BandCanvas).X / BandCanvas.ActualWidth));
        var dayStartLocal = _bandSegments[0].Segment.StartUtc.ToLocalTime().Date;
        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(dayStartLocal).AddMinutes(frac * 1440);

        foreach (var item in _bandSegments)
        {
            var seg = item.Segment;
            var end = seg.EndUtc ?? seg.StartUtc.AddSeconds(seg.DurationSec ?? 10);
            if (seg.StartUtc <= targetUtc && targetUtc <= end)
            {
                SegmentList.SelectedItem = item;
                PlayFrom(seg, Math.Max(0, (targetUtc - seg.StartUtc).TotalSeconds));
                return;
            }
        }

        var last = _bandSegments[^1];
        SegmentList.SelectedItem = last;
        PlayFrom(last.Segment, 0);
    }

    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _seeking = true;

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seeking = false;
        if (_current is null)
        {
            return;
        }

        var dur = _current.DurationSec ?? 0;
        if (dur <= 0)
        {
            return;
        }

        var offset = Math.Max(0, Math.Min(1, SeekSlider.Value / 10000)) * dur;
        PlayFrom(_current, offset);
    }

    /// <summary>將播放位置遊標移到時間軸帶上的對應時刻。</summary>
    private void UpdateCursor(double posSeconds)
    {
        if (_current is null || BandCanvas.ActualWidth <= 0)
        {
            Canvas.SetLeft(_cursor, -8);
            return;
        }

        var dayStartUtc = DayStartUtc();
        var frac = (_current.StartUtc.AddSeconds(posSeconds) - dayStartUtc).TotalMinutes / 1440.0;
        Canvas.SetLeft(_cursor, Math.Max(0, Math.Min(1, frac)) * BandCanvas.ActualWidth - 1);
        Canvas.SetTop(_cursor, 2);
    }

    private void DisposeQuietly(PlaybackSession? session)
    {
        if (session is null)
        {
            return;
        }

        _ = session.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void SetPlayingUi()
    {
        PlayPauseButton.Content = "暫停";
        PlayPauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
    }

    private void SetIdleUi()
    {
        PlayPauseButton.Content = "播放";
        PlayPauseButton.IsEnabled = _current is not null;
        StopButton.IsEnabled = false;
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _sessionVersion++;
        var session = _session;
        _session = null;
        session?.Stop();
        DisposeQuietly(session);
    }
}