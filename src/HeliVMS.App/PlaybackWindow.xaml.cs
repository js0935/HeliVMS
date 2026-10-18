using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>回放視窗（M3/M8）：挑選頻道/日期/錄影片段，以 PlaybackSession 播放；含當日時間軸帶與進度列跳轉。</summary>
public partial class PlaybackWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly SegmentRepository _segments;
    private readonly RedactionRepository _redactions;
    private readonly BookmarkRepository _bookmarkRepo;
    private long? _focusChannelId;
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
    private List<BookmarkRecord> _bookmarks = [];

    /// <summary>目前載入日的零時（UTC）；書籤標記與色帶共用同一條時間軸的起點。</summary>
    private DateTime _dayStartUtc;
    private HeliVMS.Storage.PlaybackTimeline? _timeline;
    private bool _seeking;
    private bool _scrubbing;
    private bool _pendingFramePause;
    private bool _suppressSegmentAutoplay;
    private readonly Rectangle _cursor = new() { Width = 2, Fill = Brushes.White, IsHitTestVisible = false };
    private IReadOnlyList<RedactionRegion> _activeRegions = [];

    // 回放牆（§8.5）：主畫面以外的同步次畫面。每一格各自解自己的頻道，但共用主畫面的
    // 播放頭時鐘（見 SyncPanes）；焦點一律是主畫面（ChannelCombo 選取的那台）。
    private readonly List<Pane> _panes = [];
    private readonly DispatcherTimer _syncTimer;
    private const double PaneDriftToleranceSec = 1.5;

    /// <summary>回放牆上的一個次畫面（頻道、影像、解碼工作與其當日區段）。</summary>
    private sealed class Pane
    {
        public required ChannelInfo Channel { get; init; }

        public required Border Frame { get; init; }

        public required Image Image { get; init; }

        public required TextBlock Osd { get; init; }

        public IReadOnlyList<SegmentRecord> Segments { get; init; } = [];

        public PlaybackSession? Session { get; set; }

        public SegmentRecord? Current { get; set; }

        public double BasePos { get; set; }

        public double PosInside { get; set; }

        public long Version { get; set; }

        public WriteableBitmap? Bitmap { get; set; }

        public int LastWidth { get; set; }

        public int LastHeight { get; set; }

        public bool NoRecording { get; set; }
    }

    private static readonly Brush s_redactFilledStroke = Frozen(Color.FromRgb(0xE5, 0x53, 0x3C));
    private static readonly Brush s_redactBlurStroke = Frozen(Color.FromRgb(0xE5, 0xC8, 0x3C));
    private static readonly Brush s_redactFilledFill = Frozen(Color.FromArgb(0x59, 0xE5, 0x53, 0x3C));
    private static readonly Brush s_redactBlurFill = Frozen(Color.FromArgb(0x33, 0xE5, 0xC8, 0x3C));

    // 時間軸帶分兩列：上班放錄影區塊，下班放事件標記，標記才不會壓在區塊上看不清楚（§8.5 專業時間軸）。
    private static readonly Brush s_bandEmptyFill = Frozen(Color.FromRgb(0x33, 0x41, 0x4F));
    private const double BandBarTop = 3;
    private const double BandMarkerSize = 12;
    private const double BandMarkerMargin = 3;

    private sealed record SegmentItem(SegmentRecord Segment, string StartLabel, string DurationLabel, string SizeLabel);

    private sealed record EventItem(long Id, string StartLabel, string TypeLabel, string DetailLabel);

    private sealed record BookmarkItem(long Id, DateTime AtUtc, string StartLabel, string NoteLabel);

    public PlaybackWindow(SqliteStore store, long? focusChannelId = null, DateTime? focusUtc = null)
    {
        _store = store;
        _focusChannelId = focusChannelId;
        _focusUtc = focusUtc;
        _channels = new ChannelRepository(_store);
        _segments = new SegmentRepository(_store);
        _redactions = new RedactionRepository(_store);
        _bookmarkRepo = new BookmarkRepository(_store);
        InitializeComponent();
        ApplyI18n();
        DatePick.SelectedDate = DateTime.Today;
        LoadChannels();
        BandCanvas.Children.Add(_cursor);
        Canvas.SetZIndex(_cursor, 10);

        // 次畫面不需要每一幀都重算：主畫面時鐘每 150ms 檢查一次，只有明顯飄掉或需要換段時才重啟該格。
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _syncTimer.Tick += (_, _) => SyncPanes(force: false);
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Playback.Title");
        PlaybackChannelLabel.Text = Localizer.T("Playback.Channel");
        PlaybackDateLabel.Text = Localizer.T("Playback.Date");
        LoadButton.Content = Localizer.T("Playback.Load");
        LoadButton.ToolTip = Localizer.T("Playback.LoadTip");
        RedactExportButton.Content = Localizer.T("Playback.RedactExport");
        RedactExportButton.ToolTip = Localizer.T("Playback.RedactTip");
        PlaybackSegmentsLabel.Text = Localizer.T("Playback.Segments");
        PlaybackDayEventsLabel.Text = Localizer.T("Playback.DayEvents");
        PlaybackDayEventsLabel2.Text = Localizer.T("Playback.DayEvents");
        SegColStart.Header = Localizer.T("Playback.ColStart");
        SegColDuration.Header = Localizer.T("Playback.ColDuration");
        SegColSize.Header = Localizer.T("Playback.ColSize");
        EvtColStart.Header = Localizer.T("Playback.ColStart");
        EvtColType.Header = Localizer.T("Playback.ColType");
        EvtColDetail.Header = Localizer.T("Playback.ColDetail");
        PlaybackBookmarksLabel.Text = Localizer.T("Playback.Bookmarks");
        BmColStart.Header = Localizer.T("Playback.ColStart");
        BmColNote.Header = Localizer.T("Playback.ColNote");
        AddBookmarkMenuItem.Header = Localizer.T("Playback.AddBookmark");
        RenameBookmarkMenuItem.Header = Localizer.T("Playback.RenameBookmark");
        DeleteBookmarkMenuItem.Header = Localizer.T("Playback.DeleteBookmark");
        EmptyHint.Text = Localizer.T("Playback.EmptyHint");
        PlayPauseButton.Content = Localizer.T("Playback.Play");
        PlayPauseButton.ToolTip = Localizer.T("Playback.PlayPauseTip");
        StopButton.Content = Localizer.T("Playback.Stop");
        StopButton.ToolTip = Localizer.T("Playback.StopTip");
        PlaybackSpeedLabel.Text = Localizer.T("Playback.Speed");
        PlaybackSplitLabel.Text = Localizer.T("Playback.Split");
        AutoAdvanceCheck.Content = Localizer.T("Playback.AutoAdvance");
        AutoAdvanceCheck.ToolTip = Localizer.T("Playback.AutoAdvanceTip");
        RedactionOverlayCheck.Content = Localizer.T("Playback.ShowRedaction");
        RedactionOverlayCheck.ToolTip = Localizer.T("Playback.ShowRedactionTip");
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

    /// <summary>重定向本窗到指定頻道（供主視窗右鍵「此頻道」重用同窗）。</summary>
    public void FocusChannel(long channelId)
    {
        _focusChannelId = channelId;
        LoadChannels();
        LoadSegments();
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
                    SelectSegment(item);
                    PlayFrom(seg, Math.Max(0, (focus - seg.StartUtc).TotalSeconds));
                    break;
                }
            }
        }
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSegments();

    private void OnDateChanged(object sender, SelectionChangedEventArgs e) => LoadSegments();

    private void OnLoadClicked(object sender, RoutedEventArgs e) => LoadSegments();

    /// <summary>
    /// 以目前選取的頻道／區段預先帶入遮蔽匯出視窗（§14.7 #5）。
    /// 以前這裡只能先在回放記下時間、再自己到遮蔽窗重挑一次，很容易挑錯段；
    /// 帶入選取的區段範圍後，操作員看到的與正在回放的是同一段。
    /// </summary>
    private void OnRedactFromPlaybackClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            StatusText.Text = Localizer.T("Playback.NeedChannel");
            return;
        }

        DateTime? fromUtc = null;
        DateTime? toUtc = null;
        if (SegmentList.SelectedItem is SegmentItem item)
        {
            fromUtc = item.Segment.StartUtc;
            toUtc = item.Segment.EndUtc ?? item.Segment.StartUtc.AddSeconds(item.Segment.DurationSec ?? 0);
        }

        new RedactionWindow(_store, MainWindow.ResolveDataRoot(), ch.Id, fromUtc, toUtc) { Owner = this }.ShowDialog();
    }

    /// <summary>依頻道與日期載入當日錄影片段（Final 限定）。</summary>
    private void LoadSegments()
    {
        StopPlayback();
        _activeRegions = [];
        RedactOverlay.Children.Clear();
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            SegmentList.ItemsSource = null;
            _bandSegments = [];
            _bookmarks = [];
            BookmarkList.ItemsSource = null;
            RebuildPanes();
            RenderBlocks();
            LoadHint.Text = Localizer.T("Playback.NoChannel");
            return;
        }

        if (DatePick.SelectedDate is not DateTime day)
        {
            RebuildPanes();
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
                    s.DurationSec is double d ? string.Format(Localizer.T("Playback.SecondsShort"), d.ToString("0.#")) : "-",
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
            EventCountText.Text = _events.Count > 0 ? string.Format(Localizer.T("Playback.EventCount"), _events.Count) : "";

            _dayStartUtc = startUtc;
            ReloadBookmarks(ch.Id, startUtc, endUtc);

            RebuildPanes();
            RenderBlocks();
            LoadHint.Text = _events.Count > 0
                ? string.Format(Localizer.T("Playback.SummaryBoth"), segs.Count, _events.Count)
                : string.Format(Localizer.T("Playback.SummarySegments"), segs.Count);
        }
        catch (Exception ex)
        {
            LoadHint.Text = string.Format(Localizer.T("Playback.LoadFailed"), ex.Message);
        }
    }

    /// <summary>
    /// 只有「使用者直接在清單點選區段」才自動從頭播放。
    /// 時間軸點擊／事件跳轉／跨段跳轉是由程式改選取狀態後再自行指定起播秒數
    /// （<see cref="SelectSegment"/>）；不抑制的話這裡會先從 0 起一段、外部再起第二段，
    /// 每跳一次就多開一個 ffmpeg 行程並閃一下畫面。
    /// </summary>
    private void OnSegmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSegmentAutoplay)
        {
            return;
        }

        if (SegmentList.SelectedItem is SegmentItem item)
        {
            PlayFrom(item.Segment, 0);
        }
    }

    /// <summary>程式化選取區段（不觸發 <see cref="OnSegmentSelected"/> 的自動播放），起播位置由呼叫端指定。</summary>
    private void SelectSegment(SegmentItem item)
    {
        _suppressSegmentAutoplay = true;
        try
        {
            SegmentList.SelectedItem = item;
        }
        finally
        {
            _suppressSegmentAutoplay = false;
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
            // 讓左側區段清單的高亮跟著事件跳轉一起移動，否則清單停在舊選取、畫面上卻在播別段。
            SelectSegment(band);
            PlayFrom(band.Segment, (rec.StartUtc - band.Segment.StartUtc).TotalSeconds);
        }
    }

    /// <summary>載入某通道當日的書籤並綁定到側欄清單（§8.5 §11.4）。</summary>
    private void ReloadBookmarks(int channelId, DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            _bookmarks = _bookmarkRepo.ListByRange(channelId, fromUtc, toUtc).ToList();
        }
        catch (Exception)
        {
            _bookmarks = [];
        }

        BookmarkList.ItemsSource = _bookmarks
            .Select(b => new BookmarkItem(
                b.Id,
                b.AtUtc,
                TimeZoneInfo.ConvertTimeFromUtc(b.AtUtc, TimeZoneInfo.Local).ToString("HH:mm:ss"),
                b.Note))
            .ToList();
    }

    /// <summary>
    /// 在目前播放頭加入書籤（右鍵選單或 <c>B</c>）：記下當下這一格的絕對時刻與筆記。
    /// 沒有正在播放的片段時明確回報，而不是靜默新增一個 00:00 的書籤。
    /// </summary>
    private void OnAddBookmarkClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch || _current is not SegmentRecord seg)
        {
            StatusText.Text = Localizer.T("Playback.BookmarkNoSegment");
            return;
        }

        var atUtc = seg.StartUtc.AddSeconds(_posInside);
        var note = PromptNote(Localizer.T("Playback.AddBookmark"), string.Empty);
        if (note is null)
        {
            return;
        }

        _bookmarkRepo.Add(ch.Id, atUtc, note, Environment.UserName, DateTime.UtcNow);
        ReloadBookmarks(ch.Id, _dayStartUtc, _dayStartUtc.AddDays(1));
        RenderBlocks();
        StatusText.Text = string.Format(
            Localizer.T("Playback.BookmarkAdded"),
            TimeZoneInfo.ConvertTimeFromUtc(atUtc, TimeZoneInfo.Local).ToString("HH:mm:ss"));
    }

    /// <summary>點書籤跳到其時刻（定位到所含片段並從該處起播）。</summary>
    private void OnBookmarkSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkItem bm)
        {
            return;
        }

        var band = _bandSegments
            .FirstOrDefault(b => b.Segment.StartUtc <= bm.AtUtc
                              && bm.AtUtc <= b.Segment.StartUtc.AddSeconds(b.Segment.DurationSec ?? 0));
        if (band is not null)
        {
            SelectSegment(band);
            PlayFrom(band.Segment, Math.Max(0, (bm.AtUtc - band.Segment.StartUtc).TotalSeconds));
        }
        else
        {
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
        }
    }

    private void OnRenameBookmarkClicked(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkItem bm || ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            return;
        }

        var note = PromptNote(Localizer.T("Playback.RenameBookmark"), bm.NoteLabel);
        if (note is null)
        {
            return;
        }

        _bookmarkRepo.UpdateNote(bm.Id, note);
        ReloadBookmarks(ch.Id, _dayStartUtc, _dayStartUtc.AddDays(1));
        RenderBlocks();
    }

    private void OnDeleteBookmarkClicked(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkItem bm || ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            return;
        }

        _bookmarkRepo.Remove(bm.Id);
        ReloadBookmarks(ch.Id, _dayStartUtc, _dayStartUtc.AddDays(1));
        RenderBlocks();
    }

    /// <summary>
    /// 單行文字輸入對話框（書籤筆記用）。以程式碼建立而非新增 XAML 視窗：
    /// 這個對話框只有「輸入一行字」一種用途，不值得多一個視窗檔與多語系棘輪。
    /// 回傳 null 表示取消。
    /// </summary>
    private string? PromptNote(string title, string initial)
    {
        var box = new TextBox { Text = initial ?? string.Empty, MinWidth = 280, Margin = new Thickness(0, 0, 0, 10) };
        var ok = new Button { Content = Localizer.T("Playback.NoteOk"), IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Localizer.T("Playback.NoteCancel"), IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Frozen(Color.FromRgb(0x0B, 0x16, 0x22)),
            Foreground = Brushes.White,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return dialog.ShowDialog() == true ? result : null;
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_session is null || _stopping)
        {
            return;
        }

        if (PlayPauseButton.Content.ToString() == Localizer.T("Playback.Play"))
        {
            var pos = _posInside;
            StopPlayback();
            if (_current is not null)
            {
                PlayFrom(_current, pos);
            }
        }
        else
        {
            // 暫停所有格（主畫面＋回放牆次畫面），畫面停在最後一幀；不重設 _current，才能原處續播。
            StopPlayback();
            StatusText.Text = Localizer.T("Playback.Paused");
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e) => StopPlayback();

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is not null && !_stopping)
        {
            _session.Speed = GetSpeed();
        }

        foreach (var pane in _panes)
        {
            if (pane.Session is { } paneSession)
            {
                paneSession.Speed = GetSpeed();
            }
        }
    }

    /// <summary>回放牆格數切換（1／2／4）：重建次畫面並重新對齊到目前播放頭。</summary>
    private void OnSplitChanged(object sender, SelectionChangedEventArgs e)
    {
        RebuildPanes();
        if (_session is not null)
        {
            StartSync();
            SyncPanes(force: true);
        }
    }

    /// <summary>全視窗鍵盤快捷鍵（M17，對齊 ARCHITECTURE §8.5）：Space 播放/暫停、方向鍵±10秒、Ctrl 方向鍵±1分、Shift 方向鍵±10分、F 逐幀、E 遮蔽匯出、Esc 停止。</summary>
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

            case Key.E when !ctrl && !shift:
                e.Handled = true;
                OnRedactFromPlaybackClicked(this, new RoutedEventArgs());
                break;

            case Key.B when !ctrl && !shift:
                e.Handled = true;
                OnAddBookmarkClicked(this, new RoutedEventArgs());
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
                SelectSegment(item);
                PlayFrom(seg, Math.Max(0, (targetUtc - seg.StartUtc).TotalSeconds));
                return;
            }
        }

        var first = _bandSegments[0].Segment;
        if (targetUtc < first.StartUtc)
        {
            SelectSegment(_bandSegments[0]);
            PlayFrom(first, 0);
            return;
        }

        var lastItem = _bandSegments[^1];
        var last = lastItem.Segment;
        SelectSegment(lastItem);
        PlayFrom(last, Math.Max(0, (last.DurationSec ?? 1) - 0.001));
    }

    /// <summary>逐幀（M17）：播放中先暫停，再向前推進約一幀（依 15fps 估約 66ms），並於首幀到達後再次暫停。</summary>
    private void StepFrame()
    {
        if (_current is null)
        {
            return;
        }

        if (_session is not null && !_stopping && PlayPauseButton.Content.ToString() == Localizer.T("Playback.Pause"))
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
        StopPlayback();
        _current = seg;
        _basePos = seekSeconds;
        _posInside = seekSeconds;
        _stopping = false;
        LoadRedactionRegions(seg);
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

            StatusText.Text = string.Format(Localizer.T("Playback.DecodeError"), ex.Message);
        });

        _ = session.PlayAsync(seekSeconds);

        // 主畫面（焦點）時鐘就是回放牆的母鐘：一換段／一跳轉，立刻把次畫面帶到同一絕對時刻。
        StartSync();
        SyncPanes(force: true);
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
            StatusText.Text = string.Format(Localizer.T("Playback.Playing"), GetSpeed().ToString("0.##"));
            if (!_seeking && (seg.DurationSec ?? 0) > 0)
            {
                SeekSlider.Value = Math.Min(_posInside / (seg.DurationSec ?? 1), 1) * 10000;
            }

            // 拖曳中播放頭由使用者控制；這裡若跟著解碼位置跑，游標會在手指底下被拉走。
            if (!_scrubbing)
            {
                UpdateCursor(_posInside);
            }

            if (_pendingFramePause)
            {
                _pendingFramePause = false;
                StopPlayback();
                StatusText.Text = Localizer.T("Playback.Paused");
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

            StopSync();
            StopAllPanes();
            SetIdleUi();
            TimeText.Text = $"{FormatTime(_posInside)} / {FormatTime(seg.DurationSec ?? 0)}";
            StatusText.Text = completed
                ? (AutoAdvanceCheck.IsChecked == true ? Localizer.T("Playback.EndedNoNext") : Localizer.T("Playback.EndedStopped"))
                : Localizer.T("Playback.PlayFailed");
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
        ApplyPlayerFrameGeometry();
    }

    /// <summary>中央區域尺寸變動時，重算播放框的比例與置中位置。</summary>
    private void OnPlayerAreaSizeChanged(object sender, SizeChangedEventArgs e) => ApplyPlayerFrameGeometry();

    /// <summary>
    /// 讓播放框貼合來源畫面比例並置中（沿用即時監看同一套 <see cref="MonitorGrid.Fit"/>）。
    /// </summary>
    /// <remarks>
    /// 原本播放框固定撐滿整個中央區域，來源比例與框不同時就在框內留黑邊。
    /// 改成框本身依畫面比例縮放後，藍框會緊貼影像、外圈留白一致；
    /// 尚未解碼出畫面前先以區域比例計算（等於填滿，維持原本的提示畫面）。
    /// </remarks>
    private void ApplyPlayerFrameGeometry()
    {
        if (PlayerArea is null || PlayerFrame is null)
        {
            return;
        }

        var w = PlayerArea.ActualWidth;
        var h = PlayerArea.ActualHeight;
        var cells = 1 + _panes.Count;

        if (cells <= 1)
        {
            // 單格：框緊貼來源畫面比例並置中（沿用即時監看同一套 Fit）。
            var aspect = _lastWidth > 0 && _lastHeight > 0
                ? (double)_lastWidth / _lastHeight
                : w > 0 && h > 0 ? w / h : 16.0 / 9.0;

            var fit = MonitorGrid.Fit(1, w, h, aspect);
            if (fit.Width > 0 && fit.Height > 0)
            {
                PlayerFrame.Width = fit.Width;
                PlayerFrame.Height = fit.Height;
                Canvas.SetLeft(PlayerFrame, (w - fit.Width) / 2);
                Canvas.SetTop(PlayerFrame, (h - fit.Height) / 2);
            }
            else
            {
                PlayerFrame.Width = double.NaN;
                PlayerFrame.Height = double.NaN;
            }
        }
        else
        {
            // 多格：主畫面＋次畫面共用一個 16:9 網格，全部鎖同一播放頭跨機比對。
            var fit = MonitorGrid.Fit(cells, w, h);
            var left0 = (w - fit.Width) / 2;
            var top0 = (h - fit.Height) / 2;
            var cols = Math.Max(1, fit.Columns);
            var cw = fit.Width / cols;
            var ch = fit.Height / Math.Max(1, fit.Rows);

            void Place(FrameworkElement element, int index)
            {
                var row = index / cols;
                var col = index % cols;
                element.Width = Math.Max(1, cw - 2);
                element.Height = Math.Max(1, ch - 2);
                Canvas.SetLeft(element, left0 + col * cw + 1);
                Canvas.SetTop(element, top0 + row * ch + 1);
            }

            Place(PlayerFrame, 0);
            for (var i = 0; i < _panes.Count; i++)
            {
                Place(_panes[i].Frame, i + 1);
            }
        }

        LayoutRedactionOverlay();
    }

    /// <summary>
    /// 載入目前片段時窗內的已登錄遮蔽區域（M279，§14.7 #5）：與匯出共用「頻道＋時間窗」語意
    /// （<see cref="RedactionRepository.QueryByTime"/>），只差在查詢用整段範圍、回放沿用整段。
    /// 查詢失敗不應中斷回放，故查詢例外一律視為無區域。
    /// </summary>
    private void LoadRedactionRegions(SegmentRecord seg)
    {
        _activeRegions = [];
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            return;
        }

        try
        {
            var end = seg.EndUtc ?? seg.StartUtc.AddSeconds(seg.DurationSec ?? 0);
            _activeRegions = _redactions.QueryByTime((int)ch.Id, seg.StartUtc, end);
        }
        catch (Exception)
        {
            _activeRegions = [];
        }

        LayoutRedactionOverlay();
    }

    /// <summary>
    /// 在播放畫面上以半透明色框標示已登錄的遮蔽區域（M279，§14.7 #5）：原始畫面仍可見，
    /// 供操作員預覽匯出時會遮住哪裡（實心＝紅、模糊＝黃）。投影用
    /// <see cref="RedactionOverlay.Project"/>，與解碼畫面尺寸及目前顯示框尺寸同步。
    /// </summary>
    private void LayoutRedactionOverlay()
    {
        RedactOverlay.Children.Clear();
        if (RedactionOverlayCheck.IsChecked != true || _activeRegions.Count == 0)
        {
            return;
        }

        var targetWidth = PlayerFrame.ActualWidth;
        var targetHeight = PlayerFrame.ActualHeight;
        if (_lastWidth <= 0 || _lastHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return;
        }

        RedactOverlay.Width = targetWidth;
        RedactOverlay.Height = targetHeight;

        foreach (var box in RedactionOverlay.Project(_activeRegions, _lastWidth, _lastHeight, targetWidth, targetHeight))
        {
            var rect = new Rectangle
            {
                Width = box.Width,
                Height = box.Height,
                StrokeThickness = 2,
                Stroke = box.Filled ? s_redactFilledStroke : s_redactBlurStroke,
                Fill = box.Filled ? s_redactFilledFill : s_redactBlurFill,
                ToolTip = box.Filled
                    ? Localizer.T("Playback.RedactionFilled")
                    : Localizer.T("Playback.RedactionBlurred"),
            };
            Canvas.SetLeft(rect, box.X);
            Canvas.SetTop(rect, box.Y);
            RedactOverlay.Children.Add(rect);
        }
    }

    private void OnRedactionOverlayToggled(object sender, RoutedEventArgs e) => LayoutRedactionOverlay();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void StopPlayback()
    {
        _stopping = true;
        var session = _session;
        _session = null;
        _sessionVersion++;
        session?.Stop();
        DisposeQuietly(session);
        StopSync();
        StopAllPanes();
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

        var width = BandCanvas.ActualWidth;
        var height = BandCanvas.ActualHeight;

        // 先鋪一層「無錄影」灰底（§8.5 驗收：無錄影區間色帶灰顯示），再疊上真正有錄影的藍色區塊，
        // 操作員一眼就能分辨哪裡有錄、哪裡是空的；整天完全沒有錄影時也要整條灰，
        // 否則帶子會是空的、看不出是「這天沒錄」還是「還沒載入」。
        if (width > 0 && height > 0)
        {
            var backdrop = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = s_bandEmptyFill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(backdrop, 0);
            Canvas.SetTop(backdrop, 0);
            BandCanvas.Children.Add(backdrop);
        }

        // 書籤標記獨立於錄影區塊：就算這天沒有錄影/事件，標過的書籤也要看得見。
        RenderBookmarks();

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

        // 區塊只佔上面的區塊列，下面留給事件標記；帶子加高（48）後兩列才不會互相重疊。
        if (width > 0)
        {
            var fill = (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0xC9)).GetAsFrozen();
            var barHeight = Math.Max(6, height - BandBarTop - BandMarkerSize - BandMarkerMargin * 2);
            foreach (var bar in _timeline.Bars)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(2, bar.WidthFraction * width),
                    Height = barHeight,
                    Fill = fill,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, Math.Max(0, bar.LeftFraction) * width);
                Canvas.SetTop(rect, BandBarTop);
                BandCanvas.Children.Add(rect);
            }
        }

        var coveragePct = (int)Math.Round((1 - _timeline.GapFraction) * 100);
        CoverageText.Text = string.Format(Localizer.T("Playback.Coverage"), coveragePct, _timeline.GapCount);
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

    /// <summary>在時間軸帶上畫當日書籤（橙色點，滑過顯示筆記）。</summary>
    private void RenderBookmarks()
    {
        var width = BandCanvas.ActualWidth;
        if (width <= 0 || _bookmarks.Count == 0)
        {
            return;
        }

        var brush = (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x3C)).GetAsFrozen();
        var start = _dayStartUtc == default ? DayStartUtc() : _dayStartUtc;
        foreach (var b in _bookmarks)
        {
            var frac = Math.Max(0, Math.Min(1, (b.AtUtc - start).TotalMinutes / 1440.0));
            var dot = new Ellipse
            {
                Width = BandMarkerSize,
                Height = BandMarkerSize,
                Fill = brush,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                ToolTip = b.Note,
            };
            Canvas.SetLeft(dot, frac * width - BandMarkerSize / 2);
            Canvas.SetTop(dot, Math.Max(0, BandCanvas.ActualHeight - BandMarkerSize - BandMarkerMargin));
            BandCanvas.Children.Add(dot);
        }
    }

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
                Width = BandMarkerSize,
                Height = BandMarkerSize,
                Fill = MarkerBrush(marker.Kind),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                IsHitTestVisible = true,
                ToolTip = string.Format(
                    Localizer.T("Playback.MarkerTip"),
                    TimeZoneInfo.ConvertTimeFromUtc(marker.Utc, TimeZoneInfo.Local).ToString("HH:mm:ss"),
                    marker.Kind,
                    rec?.Detail),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            Canvas.SetLeft(dot, marker.XFraction * BandCanvas.ActualWidth - BandMarkerSize / 2);
            Canvas.SetTop(dot, Math.Max(0, BandCanvas.ActualHeight - BandMarkerSize - BandMarkerMargin));
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

    /// <summary>
    /// 時間軸帶按下：開始拖曳，先只把播放頭搬到指標位置（<see cref="PreviewBandAt"/>），
    /// 不啟動任何解碼。真正跳轉留到放開滑鼠的 <see cref="OnBandClicked"/>。
    /// 拖曳途中逐格啟動 ffmpeg 會讓手感變成一片卡頓，而且每動一格就多開一個行程。
    /// </summary>
    private void OnBandScrubStart(object sender, MouseButtonEventArgs e)
    {
        if (BandCanvas.ActualWidth <= 0 || _bandSegments.Count == 0)
        {
            return;
        }

        _scrubbing = true;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }

        PreviewBandAt(e.GetPosition(BandCanvas).X);
    }

    private void OnBandMouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing)
        {
            PreviewBandAt(e.GetPosition(BandCanvas).X);
        }
    }

    /// <summary>
    /// 放開滑鼠（或單純點擊）才真正跳轉：定位到該時刻所在片段並從該處播放。
    /// 落在無錄影區間（灰底）時只回報「此時段無錄影」，不再像以前那樣默默跳到最後一段——
    /// 那會讓操作員以為點到的地方有錄影（§8.5 驗收：無錄影區間點擊要有明確回饋）。
    /// </summary>
    private void OnBandClicked(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        if (sender is UIElement element && element.IsMouseCaptured)
        {
            element.ReleaseMouseCapture();
        }

        if (BandCanvas.ActualWidth <= 0)
        {
            return;
        }

        if (_bandSegments.Count == 0)
        {
            // 整天都沒有錄影：整條帶子都是灰的，任何位置都算無錄影，給明確回饋而不是沒反應。
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
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
                SelectSegment(item);
                PlayFrom(seg, Math.Max(0, (targetUtc - seg.StartUtc).TotalSeconds));
                return;
            }
        }

        StatusText.Text = Localizer.T("Playback.NoRecordingHere");
    }

    /// <summary>拖曳中即時把播放頭移到指標位置並顯示該時刻；只搬畫面，不碰解碼（見 <see cref="OnBandScrubStart"/>）。</summary>
    private void PreviewBandAt(double x)
    {
        var width = BandCanvas.ActualWidth;
        if (width <= 0 || _bandSegments.Count == 0)
        {
            return;
        }

        var frac = Math.Max(0, Math.Min(1, x / width));
        Canvas.SetLeft(_cursor, frac * width - 1);
        _cursor.Height = BandCanvas.ActualHeight;
        Canvas.SetTop(_cursor, 0);

        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(_bandSegments[0].Segment.StartUtc.ToLocalTime().Date);
        TimeText.Text = TimeZoneInfo
            .ConvertTimeFromUtc(dayStartUtc.AddMinutes(frac * 1440), TimeZoneInfo.Local)
            .ToString("HH:mm:ss");
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
        _cursor.Height = BandCanvas.ActualHeight;
        Canvas.SetTop(_cursor, 0);
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
        PlayPauseButton.Content = Localizer.T("Playback.Pause");
        PlayPauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
    }

    private void SetIdleUi()
    {
        PlayPauseButton.Content = Localizer.T("Playback.Play");
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
        StopSync();
        StopAllPanes();
        var session = _session;
        _session = null;
        session?.Stop();
        DisposeQuietly(session);
    }

    /// <summary>回放牆格數（1／2／4）。</summary>
    private int SplitCount() => SplitCombo.SelectedIndex switch
    {
        1 => 2,
        2 => 4,
        _ => 1,
    };

    /// <summary>依目前焦點頻道與格數重建次畫面（焦點永遠是主畫面 ChannelCombo 那台）。</summary>
    private void RebuildPanes()
    {
        StopAllPanes();
        foreach (var pane in _panes)
        {
            PlayerArea.Children.Remove(pane.Frame);
        }

        _panes.Clear();

        var channels = ChannelCombo.Items.OfType<ChannelInfo>().ToList();
        if (channels.Count == 0
            || ChannelCombo.SelectedItem is not ChannelInfo focus
            || DatePick.SelectedDate is not DateTime day)
        {
            ApplyPlayerFrameGeometry();
            return;
        }

        foreach (var id in PlaybackSyncPlanner.WallChannelIds(channels.Select(c => c.Id).ToList(), focus.Id, SplitCount()).Skip(1))
        {
            var channel = channels.FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                continue;
            }

            var pane = CreatePane(channel, LoadPaneSegments(id, day));
            _panes.Add(pane);
            PlayerArea.Children.Add(pane.Frame);
        }

        ApplyPlayerFrameGeometry();
    }

    private Pane CreatePane(ChannelInfo channel, IReadOnlyList<SegmentRecord> segments)
    {
        var osd = new TextBlock
        {
            Text = channel.Name,
            Foreground = Brushes.White,
            Background = Frozen(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
            FontSize = 11,
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            IsHitTestVisible = false,
        };

        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);

        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(osd);

        var frame = new Border
        {
            BorderBrush = Frozen(Color.FromRgb(0x2A, 0x4A, 0x73)),
            BorderThickness = new Thickness(1),
            Background = Frozen(Color.FromRgb(0x05, 0x09, 0x0E)),
            Cursor = Cursors.Hand,
            ToolTip = channel.Name,
            Child = grid,
        };

        var pane = new Pane { Channel = channel, Frame = frame, Image = image, Osd = osd, Segments = segments };

        // 任一畫面點選即為主焦點（§8.5）：把該台切到主畫面，時間軸／清單／遮蔽疊圖都跟著它。
        frame.MouseLeftButtonDown += (_, _) => FocusPane(pane);
        return pane;
    }

    private void FocusPane(Pane pane)
    {
        foreach (var candidate in ChannelCombo.Items.OfType<ChannelInfo>())
        {
            if (candidate.Id != pane.Channel.Id)
            {
                continue;
            }

            if (!ReferenceEquals(ChannelCombo.SelectedItem, candidate))
            {
                ChannelCombo.SelectedItem = candidate;
            }

            return;
        }
    }

    private IReadOnlyList<SegmentRecord> LoadPaneSegments(int channelId, DateTime day)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(day.Date);
        try
        {
            return _segments.ListByRange(channelId, "main", startUtc, startUtc.AddDays(1))
                .Where(s => s.Status == SegmentStatus.Final)
                .OrderBy(s => s.StartUtc)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void StartSync()
    {
        if (_syncTimer is not null && !_syncTimer.IsEnabled)
        {
            _syncTimer.Start();
        }
    }

    private void StopSync() => _syncTimer?.Stop();

    private void StopAllPanes()
    {
        foreach (var pane in _panes)
        {
            StopPane(pane);
        }
    }

    private void StopPane(Pane pane)
    {
        pane.Version++;
        var session = pane.Session;
        pane.Session = null;
        session?.Stop();
        DisposeQuietly(session);
    }

    /// <summary>
    /// 以主畫面（焦點）的絕對時刻為母鐘，把次畫面帶到同一時刻。
    /// 某格此刻落在空隙（<see cref="PlaybackSyncPlanner.Resolve"/> 回 null）就停在那裡、不硬跳別段，
    /// 否則兩台會看似同步卻在播不同時刻——這正是多路比對最怕的假同步。
    /// </summary>
    private void SyncPanes(bool force)
    {
        if (_current is not SegmentRecord focusSegment || _panes.Count == 0)
        {
            return;
        }

        var target = focusSegment.StartUtc.AddSeconds(_posInside);

        foreach (var pane in _panes)
        {
            var cursor = PlaybackSyncPlanner.Resolve(pane.Segments, target);
            if (cursor is null)
            {
                if (pane.Session is not null)
                {
                    StopPane(pane);
                }

                pane.NoRecording = true;
                pane.Osd.Text = $"{pane.Channel.Name} · {Localizer.T("Playback.NoRecordingHere")}";
                continue;
            }

            pane.NoRecording = false;
            var paneUtc = pane.Current is { } current ? current.StartUtc.AddSeconds(pane.PosInside) : (DateTime?)null;
            var restart = force
                || pane.Session is null
                || pane.Current is null
                || pane.Current.Id != cursor.Value.Segment.Id
                || paneUtc is null
                || Math.Abs((paneUtc.Value - target).TotalSeconds) > PaneDriftToleranceSec;

            if (restart)
            {
                PlayPane(pane, cursor.Value.Segment, cursor.Value.OffsetSeconds);
            }
        }
    }

    private void PlayPane(Pane pane, SegmentRecord segment, double offsetSeconds)
    {
        StopPane(pane);
        pane.Current = segment;
        pane.BasePos = offsetSeconds;
        pane.PosInside = offsetSeconds;
        pane.NoRecording = false;
        var version = ++pane.Version;
        var session = new PlaybackSession(segment) { Speed = GetSpeed() };
        pane.Session = session;
        session.FrameDecoded += (_, frame) => Dispatcher.BeginInvoke(() => OnPaneFrame(pane, version, frame));
        session.Ended += (_, _) => Dispatcher.BeginInvoke(() => OnPaneEnded(pane, version));
        _ = session.PlayAsync(offsetSeconds);
    }

    private void OnPaneFrame(Pane pane, long version, VideoFrame frame)
    {
        if (version != pane.Version || pane.Session is null)
        {
            return;
        }

        if (pane.Bitmap is null || pane.LastWidth != frame.Width || pane.LastHeight != frame.Height)
        {
            pane.LastWidth = frame.Width;
            pane.LastHeight = frame.Height;
            pane.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            pane.Image.Source = pane.Bitmap;
        }

        pane.Bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Width * 3,
            0);

        pane.PosInside = pane.BasePos + frame.PtsMs / 1000.0;
        pane.Osd.Text = $"{pane.Channel.Name}  {FormatTime(pane.PosInside)}";
    }

    private void OnPaneEnded(Pane pane, long version)
    {
        if (version != pane.Version)
        {
            return;
        }

        // 不自行跨段：交給同步計時器依母鐘決定要進下一段（有錄影）還是停在空隙。
        pane.Session = null;
    }
}