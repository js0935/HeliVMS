using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>回放視窗（M3）：挑選頻道/日期/錄影片段，以 PlaybackSession 播放。</summary>
public partial class PlaybackWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly SegmentRepository _segments;

    private PlaybackSession? _session;
    private SegmentRecord? _current;
    private double _basePos;
    private double _posInside;
    private bool _stopping;
    private long _sessionVersion;
    private WriteableBitmap? _bitmap;
    private int _lastWidth;
    private int _lastHeight;

    private sealed record SegmentItem(SegmentRecord Segment, string StartLabel, string DurationLabel, string SizeLabel);

    public PlaybackWindow(SqliteStore store)
    {
        _store = store;
        _channels = new ChannelRepository(_store);
        _segments = new SegmentRepository(_store);
        InitializeComponent();
        DatePick.SelectedDate = DateTime.Today;
        LoadChannels();
    }

    private void LoadChannels()
    {
        ChannelCombo.ItemsSource = _channels.List();
        if (ChannelCombo.Items.Count > 0)
        {
            ChannelCombo.SelectedIndex = 0;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => LoadSegments();

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
            SegmentList.ItemsSource = segs;
            LoadHint.Text = $"當日 {segs.Count} 段。";
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