using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private readonly SegmentIndexReconciler _reconciler;
    private readonly RedactionRepository _redactions;
    private readonly BookmarkRepository _bookmarkRepo;
    private readonly SegmentKeyframeRepository _keyframes;
    private long? _focusChannelId;
    private readonly DateTime? _focusUtc;

    /// <summary>開窗後直接播當日最後一段（從即時畫面／地圖圖釘按「回放」時使用）。</summary>
    private readonly bool _autoPlayLatest;

    /// <summary>
    /// 回放前把正在錄的段收檔的服務；null 表示這個入口沒有錄影可收（例如設定中心的獨立回放）。
    /// </summary>
    private readonly IPlaybackRecordingFinalizer? _finalizer;

    /// <summary>
    /// 等收檔的上限。收檔正常是幾百毫秒（ffmpeg 寫完一個 fragment），
    /// 但攝影機卡住時不能讓「按回放」變成等半分鐘——逾時就照舊載入索引。
    /// </summary>
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(2);

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

    /// <summary>已載入日的關鍵幀索引（segment id → 關鍵幀）；背景補建完成後整份換掉。</summary>
    private IReadOnlyDictionary<long, IReadOnlyList<SegmentKeyframe>> _keyframeCache = new Dictionary<long, IReadOnlyList<SegmentKeyframe>>();

    /// <summary>關鍵幀索引世代：換日／換頻道時遞增，讓上一輪的背景探測自行放棄。</summary>
    private int _keyframeGeneration;

    /// <summary>目前載入日的零時（UTC）；書籤標記與色帶共用同一條時間軸的起點。</summary>
    private DateTime _dayStartUtc;
    private HeliVMS.Storage.PlaybackTimeline? _timeline;

    /// <summary>時間軸目前的檢視窗（UTC）。載入日時重設為全日；滾輪縮放夾在當日窗內（§3.4/§8.5）。</summary>
    private HeliVMS.Storage.TimelineViewWindow? _view;
    private bool _seeking;
    private bool _scrubbing;

    /// <summary>選擇區間窗（§8.5「拖拽選取範圍」）：Shift+左鍵在帶子上拖出的 UTC 起止；null＝無選擇。</summary>
    private (DateTime StartUtc, DateTime EndUtc)? _rangeSelection;
    private bool _selecting;
    private DateTime _selectAnchorUtc;

    /// <summary>選擇窗覆蓋層：半透明矩形＋左右起止手柄；當一個單位整組換掉，不會殘留上一次的手柄。</summary>
    private FrameworkElement? _selectionRect;

    /// <summary>正在拖曳的起止手柄：-1＝左緣、+1＝右緣、0＝不是拖手柄（是重新框選）。</summary>
    private int _rangeResizeEdge;

    /// <summary>左側清單是否已收合（§8.5「可收合側欄」）。</summary>
    private bool _sidebarCollapsed;

    /// <summary>重播所選區間時的上界；播放頭一越過就自動停（PlayFrom／停止／換載入都會清掉）。</summary>
    private DateTime? _rangeStopUtc;
    private bool _pendingFramePause;
    private bool _suppressSegmentAutoplay;

    /// <summary>
    /// 這一段是「播到檔尾」才停下來的。按播放要從頭，而不是從終點再結束一次。
    /// </summary>
    /// <remarks>
    /// 不能只靠「位置是否已達段長」判斷：段長常常是 null（剛切出來的那一段還沒回填），
    /// 那時位置比不到終點，按播放會從檔尾續播、立刻又結束——操作員只看到按鈕沒反應。
    /// </remarks>
    private bool _playbackEnded;

    /// <summary>
    /// 重新載入索引的世代：換頻道、換日、按「載入」各遞增一次，只有最後一代的結果會寫回畫面。
    /// </summary>
    /// <remarks>
    /// 收檔（<see cref="FinalizeRunningSegmentAsync"/>）是非同步的，最長等兩秒；連續換台或換日時
    /// 兩次載入會交錯。沒有世代編號的話，先出發的那一次會後抵達，用舊的頻道／日期覆蓋新選取——
    /// 操作員看到的是「選了 B 台、清單卻是 A 台」。
    /// </remarks>
    private int _reloadGeneration;

    /// <summary>
    /// 由程式自己改選取（換台、時間跳轉換日）時為 true，事件處理常式不要再排一次載入。
    /// </summary>
    /// <remarks>
    /// <c>SelectedItem</c>／<c>SelectedDate</c> 一改，WPF 就同步送出 SelectionChanged，而那個處理常式
    /// 會 <c>await</c> 收檔後才 <see cref="LoadSegments"/>（裡面第一件事就是 <see cref="StopPlayback"/>）。
    /// 呼叫端若緊接著自己開播，兩秒後就被那次遲到的載入停掉：拖進回放牆的畫面播一下就黑、
    /// 點次畫面放大的那台停住不動，而日誌一片乾淨。
    /// </remarks>
    private bool _suppressSelectionReload;

    /// <summary>
    /// XAML 建樹完成前 <see cref="OnSplitChanged"/> 會先被觸發（SplitCombo 帶 SelectedIndex="0"，
    /// InitializeComponent 的 EndInit 就送出 SelectionChanged）；那時 PlayerArea／PlayerFrame 還沒建立。
    /// </summary>
    private bool _uiReady;

    /// <summary>播放頭線身：2px 白線貫穿帶子，是播放位置的主體（§8.5「播放頭(菱形)」）。</summary>
    private readonly Rectangle _cursor = new() { Width = 2, Fill = Brushes.White, IsHitTestVisible = false };

    /// <summary>播放頭頂端菱形帽：單獨一條白線只是「一條豎線」，看不出頭在哪一端；
    /// 頂端菱形標出播放頭位置，也與選擇區間窗的方形區塊在視覺上分得開。</summary>
    private readonly Rectangle _cursorCap = new()
    {
        Width = CursorCapSize,
        Height = CursorCapSize,
        Fill = Brushes.White,
        IsHitTestVisible = false,
        RenderTransform = new RotateTransform(45, CursorCapSize / 2, CursorCapSize / 2),
    };
    private IReadOnlyList<RedactionRegion> _activeRegions = [];

    /// <summary>回放日曆本體（藏在 DatePicker 彈窗裡），第一次開啟彈窗時抓一次。</summary>
    private Calendar? _dateCalendar;
    private bool _dateCalendarWired;

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
    private static readonly Brush s_selectionFill = Frozen(Color.FromArgb(0x33, 0x4D, 0x9F, 0xFF));
    private static readonly Brush s_selectionStroke = Frozen(Color.FromRgb(0x4D, 0x9F, 0xFF));

    /// <summary>通道色帶列（§8.5）每排的色條高度與間距；4 路牆總高 26px，不搶主帶子的空間。</summary>
    private const double ChannelBandStripHeight = 5;
    private const double ChannelBandStripGap = 2;

    /// <summary>起止手柄的抓取容差（像素）：描邊只有 1px，照描邊抓根本抓不到。</summary>
    private const double RangeHandlePx = 6;

    /// <summary>播放頭頂端菱形帽的邊長（像素）。</summary>
    private const double CursorCapSize = 8;

    /// <summary>牆邊框的預設色；通道色只在有牆時上色，沒有通道時回到這個底色。</summary>
    private static readonly Brush s_defaultFrameBorder = Frozen(Color.FromRgb(0x2A, 0x4A, 0x73));

    /// <summary>通道色盤（§8.5）：色帶列與牆邊框共用，索引由 PlaybackChannelPalette 決定。</summary>
    private static readonly Brush[] s_channelPalette =
    [
        Frozen(Color.FromRgb(0x2A, 0x7F, 0xC9)),
        Frozen(Color.FromRgb(0x3F, 0xB9, 0x84)),
        Frozen(Color.FromRgb(0xD9, 0x8E, 0x32)),
        Frozen(Color.FromRgb(0xB0, 0x5A, 0xC9)),
        Frozen(Color.FromRgb(0x5A, 0xC9, 0xD9)),
        Frozen(Color.FromRgb(0xC9, 0x5A, 0x6B)),
        Frozen(Color.FromRgb(0x8A, 0xA3, 0x3C)),
        Frozen(Color.FromRgb(0x7F, 0x8C, 0xA3)),
    ];

    private static Brush ChannelBrush(int channelId)
        => s_channelPalette[HeliVMS.Storage.PlaybackChannelPalette.IndexFor(channelId)];

    /// <summary>日曆有錄影日的字色：與鍵盤焦點環同色 #4D9FFF，深底 #12233C 上對比約 5.8:1。</summary>
    private static readonly Brush s_recordedDayForeground = Frozen(Color.FromRgb(0x4D, 0x9F, 0xFF));
    private const double BandBarTop = 3;
    private const double BandMarkerSize = 12;
    private const double BandMarkerMargin = 3;

    // 關鍵幀刻度（§3.4「播放頭為關鍵幀吸附」）：比錄影色塊更深一階，只在放大到看得清間隔時才畫。
    private static readonly Brush s_keyframeTick = Frozen(Color.FromRgb(0x0B, 0x24, 0x3B));
    private const double KeyframeTickMinPx = 3;

    // 主時間列（§8.5）：刻度是輔助線，顏色只能比色帶底色亮一點，搶過錄影色塊就本末倒置。
    private static readonly Brush s_rulerTick = Frozen(Color.FromRgb(0x2A, 0x4A, 0x73));
    private static readonly Brush s_rulerText = Frozen(Color.FromRgb(0x8F, 0xA8, 0xC4));

    private sealed record SegmentItem(SegmentRecord Segment, string StartLabel, string DurationLabel, string SizeLabel);

    private sealed record EventItem(long Id, string StartLabel, string TypeLabel, string DetailLabel);

    private sealed record BookmarkItem(long Id, DateTime AtUtc, string StartLabel, string NoteLabel);

    /// <summary>
    /// 建立回放視窗。
    /// </summary>
    /// <param name="store">資料庫。</param>
    /// <param name="focusChannelId">開窗即選定的頻道。</param>
    /// <param name="focusUtc">開窗即跳到的時刻（事件中心用）；有值時優先於 <paramref name="autoPlayLatest"/>。</param>
    /// <param name="autoPlayLatest">
    /// 開窗後直接播當日最後一段。從即時畫面或地圖圖釘按「回放」時必須開啟：
    /// 操作員要的是「這台剛才的畫面」，只把頻道選好而播放器停在黑畫面，看起來就像回放壞了。
    /// </param>
    /// <param name="playbackFinalizer">
    /// 載入時間軸前先把正在錄的那一段收檔的服務。少了它，清單裡永遠少掉「最後一分鐘」，
    /// 而畫面上完全看不出來少了什麼（錄影是一段一個檔，正在寫的那一段在分段時間到期前
    /// 不存在於索引）。null 時照舊載入索引現況。
    /// </param>
    public PlaybackWindow(
        SqliteStore store,
        long? focusChannelId = null,
        DateTime? focusUtc = null,
        bool autoPlayLatest = false,
        IPlaybackRecordingFinalizer? playbackFinalizer = null)
    {
        _store = store;
        _focusChannelId = focusChannelId;
        _focusUtc = focusUtc;
        _autoPlayLatest = autoPlayLatest;
        _finalizer = playbackFinalizer;
        _channels = new ChannelRepository(_store);
        _segments = new SegmentRepository(_store);
        _reconciler = new SegmentIndexReconciler(_segments);
        _redactions = new RedactionRepository(_store);
        _bookmarkRepo = new BookmarkRepository(_store);
        _keyframes = new SegmentKeyframeRepository(_store);
        InitializeComponent();
        ApplyI18n();
        DatePick.SelectedDate = DateTime.Today;
        LoadChannels();
        BandCanvas.Children.Add(_cursor);
        BandCanvas.Children.Add(_cursorCap);
        Canvas.SetZIndex(_cursor, 10);
        Canvas.SetZIndex(_cursorCap, 11);

        // 次畫面不需要每一幀都重算：主畫面時鐘每 150ms 檢查一次，只有明顯飄掉或需要換段時才重啟該格。
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _syncTimer.Tick += (_, _) => SyncPanes(force: false);

        // XAML 建樹完成後才放行 OnSplitChanged：見 _uiReady 註解。
        _uiReady = true;
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
        ReplayRangeMenuItem.Header = Localizer.T("Playback.ReplayRange");
        ExportRangeMenuItem.Header = Localizer.T("Playback.ExportRange");
        ClearRangeMenuItem.Header = Localizer.T("Playback.ClearRange");
        try { if (CloseButton != null) CloseButton.ToolTip = Localizer.T("Playback.CloseTip"); } catch { if (CloseButton != null) CloseButton.ToolTip = "關閉"; }
        EmptyHint.Text = Localizer.T("Playback.EmptyHint");
        PlayPauseButton.Content = Localizer.T("Playback.Play");
        PlayPauseButton.ToolTip = Localizer.T("Playback.PlayPauseTip");
        StopButton.Content = Localizer.T("Playback.Stop");
        StopButton.ToolTip = Localizer.T("Playback.StopTip");
        PlaybackSpeedLabel.Text = Localizer.T("Playback.Speed");
        PlaybackSplitLabel.Text = Localizer.T("Playback.Split");
        ZoomSlider.Minimum = PlaybackTimelineScale.SliderValueFromSpan(TimeSpan.FromSeconds(PlaybackTimelineScale.MinSpanSeconds));
        ZoomSlider.Maximum = PlaybackTimelineScale.SliderValueFromSpan(TimeSpan.FromSeconds(PlaybackTimeline.DaySeconds));
        ZoomSlider.ToolTip = Localizer.T("Playback.ZoomTip");
        PlaybackTimeJumpLabel.Text = Localizer.T("Playback.TimeJump");
        JumpButton.Content = Localizer.T("Playback.Jump");
        JumpButton.ToolTip = Localizer.T("Playback.TimeJumpTip");
        SnapshotMenuItem.Header = Localizer.T("Snapshot.Capture");
        WallCapacityText.ToolTip = Localizer.T("Playback.WallCapacityTip");
        SidebarToggleButton.ToolTip = Localizer.T("Playback.SidebarTip");
        SidebarToggleButton.Content = Localizer.T("Playback.SidebarHide");
        AutoAdvanceCheck.Content = Localizer.T("Playback.AutoAdvance");
        AutoAdvanceCheck.ToolTip = Localizer.T("Playback.AutoAdvanceTip");
        RedactionOverlayCheck.Content = Localizer.T("Playback.ShowRedaction");
        RedactionOverlayCheck.ToolTip = Localizer.T("Playback.ShowRedactionTip");
    }

    private void LoadChannels()
    {
        var channels = _channels.List();
        ChannelCombo.ItemsSource = channels;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);

        if (channels.Count == 0)
        {
            return;
        }

        // 一定要挑 ItemsSource 裡的那一個實例。ComboBox.SelectedItem 是「參考」比對：
        // 給一個同 Id 但來自另一份 List() 的物件會被 Selector 直接丟掉（SelectedItem 變 null、
        // SelectedIndex 變 -1），於是 LoadSegments 認為沒選頻道。實際症狀就是從即時畫面按
        // 「回放」時視窗開得出來、頻道卻沒帶進去，播放器全黑。
        if (_focusChannelId is { } focus && channels.FirstOrDefault(c => c.Id == focus) is { } match)
        {
            ChannelCombo.SelectedItem = match;
        }
        else
        {
            ChannelCombo.SelectedIndex = 0;
        }
    }

    /// <summary>重定向本窗到指定頻道（供主視窗右鍵「此頻道」重用同窗）。</summary>
    public void FocusChannel(long channelId) => _ = SwitchChannelAsync(channelId);

    /// <summary>
    /// 頻道是否還存在。以資料庫為準，不只看下拉清單：視窗開著的時候操作員可以新增攝影機，
    /// 那份 ItemsSource 是開窗當下讀的，只比它會把新頻道判成「不存在」——拖進來完全沒反應。
    /// </summary>
    private bool ChannelExists(long channelId)
    {
        try
        {
            return _channels.List().Any(c => c.Id == channelId);
        }
        catch (Exception)
        {
            // 資料庫讀不到時退回目前清單：已載入的頻道仍可切換，不讓一次查詢失敗卡住整個操作。
            return ChannelCombo.Items.OfType<ChannelInfo>().Any(c => c.Id == channelId);
        }
    }

    /// <summary>
    /// 切到指定頻道並等當日索引載入完成；回傳 false 表示頻道不存在，或這次載入已被更新的操作取代。
    /// </summary>
    /// <remarks>
    /// 一定要等到 <see cref="LoadSegments"/> 跑完才讓呼叫端開播：換台本身會先
    /// <see cref="StopPlayback"/>，而收檔是非同步的。先開播再載入，等於畫面出現兩秒後被那次
    /// 載入自己停掉（現場症狀：拖進回放牆的頻道播一下就黑、點次畫面放大後停住）。
    /// </remarks>
    private async Task<bool> SwitchChannelAsync(long channelId)
    {
        if (!ChannelExists(channelId))
        {
            // 頻道不存在（已刪台）時什麼都不做：LoadChannels 找不到會退回第一台，
            // 那會變成「拖 A 卻播出 B」，比沒反應更糟。
            return false;
        }

        _focusChannelId = channelId;

        // 這裡改選取是「程式決定的」，事件處理常式不必再排一次載入；
        // 放行的話會多一次收檔＋載入，正好把呼叫端剛開的播放停掉。
        _suppressSelectionReload = true;
        try
        {
            LoadChannels();
        }
        catch (Exception ex)
        {
            // 由 async void 事件鏈發動（拖放、主視窗重用同窗）：例外漏出去就是整個程式結束。
            StatusText.Text = string.Format(Localizer.T("Playback.LoadFailed"), ex.Message);
            return false;
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        return await ReloadForPlaybackAsync();
    }

    /// <summary>
    /// 拖入分屏立即回放（§8.5）：把即時監看某台拖進本窗，就切到該台並開播。
    /// <para>
    /// 先前已有播放頭（本窗正在播）就從同一時刻接上，維持多路比對的時刻一致；
    /// 沒有播放頭時從當日最後一段開頭播——操作員把畫面拖進回放，通常想看剛發生的事。
    /// </para>
    /// </summary>
    public void FocusChannelAndPlay(long channelId) => _ = FocusChannelAndPlayAsync(channelId);

    private async Task FocusChannelAndPlayAsync(long channelId)
    {
        // 播放頭要在換台之前記：LoadSegments 會清掉它（那時的 _current 還屬於上一台）。
        var resumeUtc = _session is not null && _current is not null
            ? _current.StartUtc.AddSeconds(_posInside)
            : (DateTime?)null;

        if (!await SwitchChannelAsync(channelId))
        {
            return;
        }

        if (resumeUtc is { } target)
        {
            if (ResumePlaybackAt(target))
            {
                return;
            }

            // 拖進來的那台在那一刻沒有錄影（空檔，或今天根本沒錄）：留在原地就是一個
            // 全黑播放器，沒有錯誤、沒有日誌，看起來像拖放沒有作用。拖進回放要看的是
            // 「剛剛發生的事」，所以改播當日最後一段，並說明為什麼時刻變了。
            if (_bandSegments.Count > 0)
            {
                StatusText.Text = Localizer.T("Playback.ResumeFellBackToLatest");
            }

            PlayLatestSegment();
            return;
        }

        if (_bandSegments.Count > 0)
        {
            var last = _bandSegments[^1];
            SelectSegment(last);
            PlayFrom(last.Segment, 0);
        }
    }

    private void OnPlaybackDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedChannelId(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPlaybackDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedChannelId(e.Data, out var channelId))
        {
            return;
        }

        e.Handled = true;
        FocusChannelAndPlay(channelId);
    }

    /// <summary>從拖放資料取出頻道 Id；自訂格式優先，文字格式當後備（其他來源也能拖進來）。</summary>
    private static bool TryGetDroppedChannelId(System.Windows.IDataObject data, out long channelId)
    {
        channelId = 0;
        var raw = data.GetDataPresent(DragFormats.ChannelId)
            ? data.GetData(DragFormats.ChannelId) as string
            : null;
        raw ??= data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
        return raw is not null && long.TryParse(raw, out channelId);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 先把正在錄的那一段收檔再讀索引：否則清單裡永遠沒有「剛剛發生的事」，
        // 而操作員從即時畫面按回放要看的正是那一段。
        if (!await ReloadForPlaybackAsync())
        {
            // 開窗期間已經有更新的載入接手（例如拖放指定頻道），讓那一次決定要播什麼。
            return;
        }

        if (_focusUtc is { } focus)
        {
            var item = _bandSegments.FirstOrDefault(
                i => i.Segment.StartUtc <= focus && EndOf(i.Segment) >= focus);
            if (item is not null)
            {
                SelectSegment(item);
                PlayFrom(item.Segment, Math.Max(0, (focus - item.Segment.StartUtc).TotalSeconds));
            }
            else
            {
                // 從事件中心／地圖點進來的那一刻正好落在空檔：留在原位並說清楚，
                // 默默開一個全黑的播放器會讓人以為是解碼壞了。
                StatusText.Text = Localizer.T("Playback.NoRecordingHere");
            }

            return;
        }

        if (_autoPlayLatest)
        {
            PlayLatestSegment();
        }
    }

    /// <summary>
    /// 從當日最後一段開始播。
    /// </summary>
    /// <remarks>
    /// 最後一段是「剛剛發生的事」，也就是操作員從即時畫面按回放時想看的那一段；
    /// 想看更早的再從時間軸或清單往回找。挑不到可播區段時必須出聲：空白播放器
    /// 與「解碼失敗」在畫面上長得一模一樣，不講清楚會變成查錯方向。
    /// </remarks>
    private void PlayLatestSegment()
    {
        if (_bandSegments.Count == 0)
        {
            LoadHint.Text = Localizer.T("Playback.NoSegmentsToday");
            return;
        }

        var last = _bandSegments[^1];
        SelectSegment(last);
        PlayFrom(last.Segment, 0);
    }

    /// <summary>
    /// 重新載入時間軸（換頻道／換日／按「載入」）：先把正在錄的段收檔，再看索引。
    /// </summary>
    /// <remarks>
    /// 這個方法自己吞掉所有例外：它是由事件處理常式 <c>await</c> 的，
    /// 例外一漏出去就是整個程式崩掉，而「收檔失敗」根本不該有這種後果。
    /// </remarks>
    /// <returns>
    /// false＝這次載入已被更新的操作取代（收檔期間又換了台或換了日），呼叫端不要再依
    /// <see cref="_bandSegments"/> 開播或跳轉——那份資料已經不是使用者最後選的東西。
    /// </returns>
    private async Task<bool> ReloadForPlaybackAsync()
    {
        var generation = Interlocked.Increment(ref _reloadGeneration);
        try
        {
            await FinalizeRunningSegmentAsync();
            if (generation != Volatile.Read(ref _reloadGeneration))
            {
                return false;
            }

            LoadSegments();
            return true;
        }
        catch (Exception ex)
        {
            // 這條路徑由 async void 事件處理常式 await：例外一漏出去就是整個程式結束。
            // 收檔或重畫失敗只該讓回放停在「載入失敗」，不該連視窗一起帶走；
            // 但也不能只吞掉——沒有日誌的空白畫面在現場完全無法查。
            BoundedFileLog.Append(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helivms-playback.log"),
                $"{DateTime.UtcNow:O} 回放載入失敗：{ex}\n",
                256 * 1024);

            if (generation == Volatile.Read(ref _reloadGeneration))
            {
                ShowEmptyState(string.Format(Localizer.T("Playback.LoadFailed"), ex.Message));
            }

            return false;
        }
    }

    /// <summary>
    /// 要求目前選取頻道的錄影立刻收檔並等它進索引（最多 <see cref="FinalizeTimeout"/>）。
    /// </summary>
    /// <remarks>
    /// 只在看「今天」時收檔：翻看歷史時截斷正在錄的段沒有任何好處，
    /// 而連按日期會讓錄影一直被迫換段（每一段都要重連 RTSP）。
    /// </remarks>
    private async Task FinalizeRunningSegmentAsync()
    {
        if (_finalizer is null || !IsViewingToday())
        {
            return;
        }

        int? channelId = ChannelCombo.SelectedItem is ChannelInfo ch ? ch.Id : null;
        await _finalizer.FinalizeAsync(channelId, FinalizeTimeout);
    }

    /// <summary>目前選取的是否為今天（未選日期時視為今天，載入流程本來就會擋掉）。</summary>
    private bool IsViewingToday()
        => DatePick.SelectedDate is not DateTime day || day.Date == DateTime.Today;

    private async void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 建構子裡的 LoadChannels 也會改選取：那時視窗還沒開，不該因此收檔。
        // 程式自己換台（拖放、點次畫面放大、主視窗指定頻道）也走同一條路，但載入由呼叫端
        // await，這裡再排一次就等於把對方剛開的播放停掉。
        if (!_uiReady || _suppressSelectionReload)
        {
            return;
        }

        await ReloadForPlaybackAsync();
    }

    private async void OnDateChanged(object sender, SelectionChangedEventArgs e)
    {
        // 建構子指派 DatePick.SelectedDate 時也會走到這裡：那時視窗還沒開，不該因此收檔。
        // 時間跳轉自己換日期時同理（見 JumpToLocalTime）。
        if (!_uiReady || _suppressSelectionReload)
        {
            return;
        }

        await ReloadForPlaybackAsync();
    }

    private async void OnLoadClicked(object sender, RoutedEventArgs e)
    {
        await ReloadForPlaybackAsync();
    }

    /// <summary>
    /// 日期彈窗開啟時抓 Calendar 本體、掛上換月重算，並排入粗體標記（§3.4）。
    ///
    /// <para>
    /// 必須掛 CalendarOpened：彈窗內容（Popup.Child 的 Calendar）是開啟當下才建的，
    /// 掛在 OnLoaded 上時它還不存在，第一次開啟永遠抓不到本體。
    /// </para>
    /// </summary>
    private void OnCalendarOpened(object sender, EventArgs e)
    {
        if (!_dateCalendarWired
            && DatePick.Template?.FindName("PART_Popup", DatePick) is System.Windows.Controls.Primitives.Popup popup
            && popup.Child is Calendar cal)
        {
            _dateCalendarWired = true;
            _dateCalendar = cal;
            cal.DisplayDateChanged += OnCalendarDisplayDateChanged;
        }

        QueueRecordingDayMarks();
    }

    /// <summary>換月後重算粗體；排到版面之後才套用（見 <see cref="QueueRecordingDayMarks"/>）。</summary>
    private void OnCalendarDisplayDateChanged(object? sender, CalendarDateChangedEventArgs e)
        => QueueRecordingDayMarks();

    /// <summary>
    /// 把「當天有錄影」的日期標成粗體＋藍字，其餘日期重設回樣式預設（§3.4）。
    ///
    /// <para>
    /// 每一格都明確寫回兩種狀態，不能只標不取消：CalendarDayButton 是重複使用的按鈕，
    /// 只在開啟時標一次的話，上個月的粗體與藍字會跟著按鈕流到下個月（實測換月後舊值仍在）。
    /// </para>
    /// <para>
    /// 口徑與 <see cref="LoadSegments"/> 完全一致：只認 final 段、重疊式查詢、同一條
    /// 時區算法（起點就地轉 UTC 再加一天），兩者不一致就會出現「有粗體但清單是空的」
    /// 或「當天有錄影卻不粗」，比沒有粗體更容易誤導操作員。
    /// </para>
    /// <para>
    /// 字重要設在按鈕上、字色必須設在日字上，理由見 <see cref="DayText"/>：兩個屬性
    /// 雖然都叫 Foreground／FontWeight，走的卻不是同一條路。
    /// </para>
    /// </summary>
    private void ApplyRecordingDayMarks()
    {
        if (_dateCalendar is not Calendar cal)
        {
            return;
        }

        var buttons = new List<System.Windows.Controls.Primitives.CalendarDayButton>();
        CollectDayButtons(cal, buttons);
        if (buttons.Count == 0)
        {
            return;
        }

        // 格位順序：CalendarDaysView 依序放進 6x7 格子，第 0 格永遠是「顯示月份的
        // 第一個 FirstDayOfWeek」那天，往後逐格 +1 天（已以探針實測 42 格全對位）。
        var first = new DateTime(cal.DisplayDate.Year, cal.DisplayDate.Month, 1);
        var offset = ((int)first.DayOfWeek - (int)cal.FirstDayOfWeek + 7) % 7;
        var firstCell = first.AddDays(-offset);

        var windows = new List<(DateTime StartUtc, DateTime EndUtc)>(buttons.Count);
        for (var i = 0; i < buttons.Count; i++)
        {
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(firstCell.AddDays(i));
            windows.Add((startUtc, startUtc.AddDays(1)));
        }

        IReadOnlyList<bool> has;
        if (ChannelCombo.SelectedItem is ChannelInfo ch)
        {
            // 粗體前先就地對帳：使用者把整天的錄影資料夾刪掉之後，索引原本要等最多一小時的
            // 背景對帳才知道；而日曆是操作員判斷「這天有沒有錄影」的第一個畫面，讓他等就是在騙他。
            // 只涉及本月份格位對應的資料夾（一格一次 Directory.Exists），成本與標記同一等級。
            ReconcileDayFolderExistence(ch.Id, windows);
            has = _segments.HasSegmentsInWindows(ch.Id, "main", windows);
        }
        else
        {
            has = new bool[buttons.Count];
        }

        for (var i = 0; i < buttons.Count; i++)
        {
            var day = buttons[i];
            day.FontWeight = has[i] ? FontWeights.Bold : FontWeights.Normal;

            // 無錄影日用 ClearValue 而不是寫回白色：清掉本機值才會退回樣式，
            // 樣式日後改色，被寫死的格子不會跟著改。
            if (DayText(day) is { } text)
            {
                if (has[i])
                {
                    text.Foreground = s_recordedDayForeground;
                }
                else
                {
                    text.ClearValue(TextElement.ForegroundProperty);
                }
            }
        }
    }

    /// <summary>
    /// 日期格中央的日字：ContentPresenter 為字串內容內建的 TextBlock。
    ///
    /// <para>
    /// 字色只能設在這裡。<c>CalendarDayButton.Foreground</c> 是 <c>Control.ForegroundProperty</c>，
    /// 文字繼承的是 <c>TextElement.ForegroundProperty</c>，兩者是不同的 DP，而 CalendarDayButton
    /// 的模板不接這座橋：在按鈕上設 Foreground，屬性讀值會變成設的顏色、畫面卻永遠維持樣式白
    /// （探針以紅字實測，像素完全不變；設到日字上才真的變色，ClearValue 也能退回樣式白）。
    /// 字重沒有這個問題——<c>Control</c> 與 <c>TextElement</c> 的 FontWeight 是同一個 DP，
    /// 所以粗體照舊設在按鈕上（探針比墨量：粗 54 對 normal 50，確實畫出來）。
    /// </para>
    /// </summary>
    private static TextBlock? DayText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                return text;
            }

            if (DayText(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 排到 Dispatcher 已完成版面（Loaded 優先度）再標記。
    ///
    /// <para>
    /// 彈窗的格子是開啟當下才生成的，CalendarOpened 同步執行時往往一格都還沒長出來；
    /// 而 DisplayDateChanged 會在新月份內容重填之前觸發，同步標記會對到舊月份。
    /// 排到佇列尾端後兩種情況都會對到已長完、已換月的版面，且早於 Render，畫面不會閃。
    /// </para>
    /// </summary>
    private void QueueRecordingDayMarks()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => ApplyRecordingDayMarks()));

    /// <summary>走視覺樹收集全部日期格；年／月視圖的 CalendarButton 不是日期格，型別就把它們擋掉了。</summary>
    private static void CollectDayButtons(DependencyObject root, List<System.Windows.Controls.Primitives.CalendarDayButton> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Primitives.CalendarDayButton day)
            {
                into.Add(day);
            }

            CollectDayButtons(child, into);
        }
    }

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
            toUtc = EndOf(item.Segment);
        }

        new RedactionWindow(_store, MainWindow.ResolveDataRoot(), ch.Id, fromUtc, toUtc) { Owner = this }.ShowDialog();
    }

    /// <summary>載入既有關鍵幀索引（純 SQLite 查詢，不跑 ffprobe）。</summary>
    /// <remarks>
    /// 一天可能有上千段；逐段一次 <c>List(id)</c> 會在 UI 執行緒上做上千次來回，
    /// 故改走一趟 <see cref="SegmentKeyframeRepository.ListForSegments"/>（無索引的段自然缺席）。
    /// </remarks>
    private void LoadKeyframeCache(IReadOnlyList<SegmentItem> segs)
    {
        var ids = new long[segs.Count];
        for (var i = 0; i < segs.Count; i++)
        {
            ids[i] = segs[i].Segment.Id;
        }

        _keyframeCache = _keyframes.ListForSegments(ids);
    }

    /// <summary>
    /// 背景補建缺少的關鍵幀索引，完成後只重畫色帶。
    /// </summary>
    /// <remarks>
    /// 對每個區段跑一次 ffprobe 是百毫秒級（4 路 × 一天數十段＝數十秒），絕不能擋在
    /// <see cref="LoadSegments"/> 裡——那會讓換日／換頻道卡住整個 UI。世代編號是這裡的關鍵：
    /// 使用者連續換日時，舊的探測還在跑，回來必須自己作廢，否則會把上一天的關鍵幀畫到今天，
    /// 在帶子上看到一段根本不存在的密集刻度。探不到關鍵幀的區段刻意不寫入索引，
    /// 否則它會被 <see cref="SegmentKeyframeRepository.ListMissingIndexIds"/> 永遠當成「已建」而不再重試。
    /// </remarks>
    private void StartKeyframeIndexing(
        long channelId,
        IReadOnlyList<SegmentItem> segs,
        int generation,
        DateTime startUtc,
        DateTime endUtc)
    {
        if (segs.Count == 0)
        {
            return;
        }

        var missing = _keyframes.ListMissingIndexIds(channelId, "main", startUtc, endUtc).ToHashSet();
        var work = segs.Where(item => missing.Contains(item.Segment.Id)).ToArray();
        if (work.Length == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var item in work)
            {
                if (Volatile.Read(ref _keyframeGeneration) != generation)
                {
                    return;
                }

                try
                {
                    var path = item.Segment.FilePath;
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    // 探測失敗回空清單；空結果不寫入索引，下一輪才還會重試。
                    var rows = KeyframeProbe.Probe(path);
                    if (rows.Count > 0)
                    {
                        _keyframes.Replace(item.Segment.Id, rows);
                    }
                }
                catch (Exception)
                {
                    // 索引是 best-effort：資料庫忙碌或檔案被保留策略刪掉都不該讓這個背景作業
                    // 變成未觀察的例外，也不該影響其他區段繼續補建。
                }
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _keyframeGeneration) != generation)
                {
                    return;
                }

                LoadKeyframeCache(segs);
                RenderBlocks();
            });
        });
    }

    /// <summary>
    /// 濾掉索引說有、磁碟上卻已經不存在的段（幽靈段），順手把它們在索引裡標成 missing。
    ///
    /// <para>
    /// 為什麼要在回放這條路上做：使用者把錄影資料刪掉之後，清單、帶子與日曆仍然照著索引顯示，
    /// 點下去才失敗。背景對帳每小時才跑一輪（見 <see cref="SegmentReconcileCoordinator"/>），
    /// 而使用者要的是「打開就看得到實情」，所以開日清單時就地驗一次——當日最多一千多段，
    /// 一次 stat 的量在毫秒級，驗過的結果寫回索引後，日曆與用量統計下一輪就自己修正。
    /// </para>
    /// </summary>
    private (IReadOnlyList<SegmentRecord> Playable, int Missing, int Unverified) ReconcileSegments(
        IReadOnlyList<SegmentRecord> rows)
    {
        try
        {
            var (playable, report) = _reconciler.VerifyExisting(rows);
            return (playable, report.MarkedMissing, report.Unverified);
        }
        catch (Exception)
        {
            // 逐檔檢查失敗（權限、磁碟忙碌）不該讓整個回放打不開；原樣顯示，
            // 寧可暫時多列一段，也不要整個視窗只剩錯誤訊息。
            return (rows, 0, 0);
        }
    }

    /// <summary>
    /// 日曆粗體前的資料夾層級對帳：把「整個 yyyyMMdd 資料夾已經不在磁碟上」的日子就地標成
    /// missing，讓同一輪重算的粗體立刻消失。
    ///
    /// <para>
    /// 與 <see cref="ReconcileSegments"/> 分開是因為成本形狀不同：這裡只看目前格位涵蓋的
    /// 資料夾（≤42 個 <c>Directory.Exists</c>），逐檔 stat 交給開日清單的那條路徑。失敗一律
    /// 靜靜放行——日曆是輔助視覺，不值得為它擋掉整個彈窗。
    /// </para>
    /// </summary>
    private void ReconcileDayFolderExistence(int channelId, IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        try
        {
            var folders = _segments.ListFinalFoldersInRange(
                channelId, "main", windows[0].StartUtc, windows[^1].EndUtc);
            if (folders.Count > 0)
            {
                _reconciler.VerifyFolders(folders);
            }
        }
        catch (Exception)
        {
            // 對帳失敗（權限、磁碟忙碌）不該讓日曆標不出來；照舊顯示索引的現況。
        }
    }

    /// <summary>依頻道與日期載入當日錄影片段（Final 限定）。</summary>
    private void LoadSegments()
    {
        // 換日／換頻道即作廢上一輪的背景關鍵幀探測，避免它把上一天的結果畫到今天。
        var keyframeGeneration = Interlocked.Increment(ref _keyframeGeneration);
        _keyframeCache = new Dictionary<long, IReadOnlyList<SegmentKeyframe>>();

        // 播放頭屬於上一台／上一天，必須先清掉：留著它，「播放」按鈕會拿另一台的檔案續播
        // （畫面有影像、清單卻是別台），時間與進度列也停在舊值。
        ClearPlaybackHead();
        StopPlayback();
        _activeRegions = [];
        RedactOverlay.Children.Clear();
        _rangeSelection = null;
        _rangeStopUtc = null;
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            ShowEmptyState(Localizer.T("Playback.NoChannel"));
            return;
        }

        if (DatePick.SelectedDate is not DateTime day)
        {
            // 日期被清空（可以手動清 DatePicker）與沒選頻道同一個下場：清單留著上一天的段，
            // 點下去播的是別天的檔案。寧可整頁空白，也不要留一份對不上的資料。
            ShowEmptyState(Localizer.T("Playback.NoChannel"));
            return;
        }

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(day.Date);
        var endUtc = startUtc.AddDays(1);

        try
        {
            var segs = _segments.ListByRange(ch.Id, "main", startUtc, endUtc, out var indexError);
            var (playable, missingCount, unverifiedCount) = ReconcileSegments(segs);
            var items = playable
                .Where(s => s.Status == SegmentStatus.Final)
                .OrderBy(s => s.StartUtc)
                .Select(s => new SegmentItem(
                    s,
                    TimeZoneInfo.ConvertTimeFromUtc(s.StartUtc, TimeZoneInfo.Local).ToString("HH:mm:ss"),
                    s.DurationSec is double d ? string.Format(Localizer.T("Playback.SecondsShort"), d.ToString("0.#")) : "-",
                    s.SizeBytes > 0 ? $"{s.SizeBytes / 1024d:0}KB" : "-"))
                .ToList();
            _bandSegments = items;
            SegmentList.ItemsSource = items;

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

            // 載入新的一天＝回到全日視窗：留著昨天的縮放窗看今天，帶子上會是一片沒有對到資料的空白。
            _view = HeliVMS.Storage.PlaybackTimelineScale.FullDay(startUtc);
            ReloadBookmarks(ch.Id, startUtc, endUtc);

            // 先同步載入既有索引（純 SQLite，毫秒級）讓刻度立刻出現，缺的再丟背景補建。
            LoadKeyframeCache(items);
            StartKeyframeIndexing(ch.Id, items, keyframeGeneration, startUtc, endUtc);

            RebuildPanes();
            RenderBlocks();
            var hint = _events.Count > 0
                ? string.Format(Localizer.T("Playback.SummaryBoth"), items.Count, _events.Count)
                : string.Format(Localizer.T("Playback.SummarySegments"), items.Count);
            if (missingCount > 0)
            {
                // 說清楚是「檔案不見了」而不是「沒有錄影」：操作員看到清單變短，
                // 不講的話第一反應是系統壞了。
                hint = hint + " " + string.Format(Localizer.T("Playback.MissingSegments"), missingCount);
            }

            if (unverifiedCount > 0)
            {
                hint = hint + " " + Localizer.T("Playback.DiskNotReady");
            }

            if (!string.IsNullOrEmpty(indexError))
            {
                // 「索引讀不到」與「這段時間沒有錄影」在畫面上長得一樣，處理方式卻完全不同：
                // 前者要修資料庫或關掉佔用它的行程，後者只是換個時間。不講清楚，操作員會
                // 一直以為錄影沒有寫進來，而磁碟上明明有檔案。
                hint = hint + " " + string.Format(Localizer.T("Playback.IndexUnavailable"), indexError);
            }

            LoadHint.Text = hint;
        }
        catch (Exception ex)
        {
            // 讀取失敗時必須把上一輪的清單與時間軸一併清掉：只寫一行提示碼的話，操作員眼前
            // 還留著前一天的段，點下去播放器一片黑（檔案不屬於這天），而他會以為是解碼壞了。
            ShowEmptyState(string.Format(Localizer.T("Playback.LoadFailed"), ex.Message));
        }
    }

    /// <summary>
    /// 清空清單／事件／書籤／時間軸，只留一行說明。
    /// </summary>
    /// <remarks>
    /// 沒選頻道、日期被清空、索引讀取失敗都走這裡：這三種情況畫面上都只剩提示，
    /// 但清單必須一起清掉——留著上一輪的資料等於提供一份「點得到但不屬於這天」的片段。
    /// </remarks>
    private void ShowEmptyState(string hint)
    {
        // 清畫面也要清播放頭：走到這裡可能是「收檔擲例外」這種還沒開始 LoadSegments 的情況，
        // 那時舊的 session 還在跑——只清清單會變成「畫面有影像、清單是空的」。
        ClearPlaybackHead();
        StopPlayback();
        SegmentList.ItemsSource = null;
        _bandSegments = [];
        _events = [];
        EventList.ItemsSource = null;
        EventCountText.Text = string.Empty;
        _bookmarks = [];
        BookmarkList.ItemsSource = null;
        _keyframeCache = new Dictionary<long, IReadOnlyList<SegmentKeyframe>>();
        _dayStartUtc = default;
        _view = null;
        _timeline = null;
        RebuildPanes();
        RenderBlocks();
        HideCursor();
        LoadHint.Text = hint;
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

        // 事件時刻落在哪一段，由全應用程式唯一一份規則回答（PlaybackSeekPlanner.ResolveAt，
        // 涵蓋範圍 [起點, 結束)）。舊寫法自己比對閉區間而且取先出現的那一段：事件剛好落在
        // 段尾或兩段接縫時，會落在前一段的「第 60 秒」——那裡沒有影格，點下去立刻回報
        // 「已播放完畢」，而事件其實就寫在下一段的開頭。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.ResolveAt(
            _bandSegments.Select(b => b.Segment),
            rec.StartUtc);
        var band = landing is { } hit
            ? _bandSegments.FirstOrDefault(b => ReferenceEquals(b.Segment, hit.Segment))
            : null;
        if (landing is { } found && band is not null)
        {
            // 讓左側區段清單的高亮跟著事件跳轉一起移動，否則清單停在舊選取、畫面上卻在播別段。
            SelectSegment(band);
            PlayFrom(found.Segment, found.OffsetSeconds);
            return;
        }

        // 事件落在空檔（例如移動事件比錄影早幾秒）也要出聲：靜默不動作看起來就是點壞了。
        StatusText.Text = Localizer.T("Playback.NoRecordingHere");
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

        // 與事件、時間跳轉、時間軸點擊共用同一份落點規則；書籤正好記在段尾或接縫上時，
        // 舊的閉區間寫法會落進前一段沒有影格的最後一刻（點下去只回報「已播放完畢」）。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.ResolveAt(
            _bandSegments.Select(b => b.Segment),
            bm.AtUtc);
        var band = landing is { } hit
            ? _bandSegments.FirstOrDefault(b => ReferenceEquals(b.Segment, hit.Segment))
            : null;
        if (landing is { } found && band is not null)
        {
            SelectSegment(band);
            PlayFrom(found.Segment, found.OffsetSeconds);
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

    /// <summary>播放／暫停切換：狀態看 <see cref="_session"/>，不看按鈕上的字。</summary>
    /// <remarks>
    /// 舊寫法有兩個錯誤疊在一起：（1）開頭就用 <c>_session is null</c> 早退，而暫停與停止都會把
    /// <c>_session</c> 設成 null，所以「暫停」之後按鈕（它仍顯示「播放」、也仍可按）永遠沒反應——
    /// 整段錄影只能靠重新點一次清單才能繼續；（2）用按鈕文字判斷目前是播還是停，一旦
    /// ApplyI18n 換語言或按鈕文字被別處改寫，判斷就跟著錯。
    /// </remarks>
    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (_session is null)
        {
            // 已暫停／已停止：從同一個播放位置續播。播到尾之後 _posInside 停在終點，
            // 直接續播會立刻再結束一次（按了像沒反應），所以到終點就從頭播。
            // 「已播完」也看 _playbackEnded：段長為 null 時位置永遠比不到終點，
            // 只靠段長判斷會讓未知長度的段播完後按播放毫無反應。
            var duration = _current.DurationSec ?? 0;
            var resumeAt = _playbackEnded || (duration > 0 && _posInside >= duration - 0.5)
                ? 0
                : _posInside;
            PlayFrom(_current, resumeAt);
            return;
        }

        StopPlayback();
        StatusText.Text = Localizer.T("Playback.Paused");
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        StopPlayback();

        // 停止後畫面刻意停在最後一格、播放頭也留著（按播放可續播），所以狀態列必須跟著改：
        // 停住不動卻還寫「播放中（1×）」，操作員會以為按鈕沒作用——現場的回報就是「停止鈕壞了」。
        StatusText.Text = Localizer.T("Playback.EndedStopped");
    }

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
        // SplitCombo 在 XAML 裡帶 SelectedIndex="0"：InitializeComponent 建樹到這格時
        // EndInit 就會送出 SelectionChanged，此刻 PlayerFrame／PlayerArea 還沒建立，
        // RebuildPanes 取用會 NullReferenceException。它被 DispatcherUnhandledException
        // 吞掉（Handled = true），症狀是「按回放完全沒反應」——2026-10-19 現場就是這樣，
        // helivms-crash.log 只留下 RebuildPanes ← OnSplitChanged ← InitializeComponent。
        if (!_uiReady)
        {
            return;
        }

        RebuildPanes();
        if (_session is not null)
        {
            StartSync();
            SyncPanes(force: true);
        }
    }

    /// <summary>全視窗鍵盤快捷鍵（M17，對齊 ARCHITECTURE §8.5）：Space 播放/暫停、方向鍵±10秒、Ctrl 方向鍵±1分、Shift 方向鍵±10分、F 逐幀、E 遮蔽匯出、1–4 選路、Esc 停止。</summary>
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

            // 格面 1–4 選路（§8.5）：與滑鼠點 ChannelCombo 同一條路，改選即 LoadSegments 並重繪牆。
            // 數字鍵盤也吃：NVR 操作台多半用右側數字鍵，兩邊挑一個等於沒有這個快捷鍵。
            case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4
                when !ctrl && !shift:
                var channelIndex = e.Key <= Key.D4 ? e.Key - Key.D1 : e.Key - Key.NumPad1;
                if (channelIndex < ChannelCombo.Items.Count)
                {
                    e.Handled = true;
                    ChannelCombo.SelectedIndex = channelIndex;
                }

                break;

            case Key.Escape:
                e.Handled = true;
                // 與停止鈕同一條路：狀態列才會從「播放中」改成「已停止播放」。
                OnStopClicked(this, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>
    /// 依目前位置向後／向前跳轉指定秒數；越過片段邊界時自動跨段，最後 clamp 於當日整段範圍。
    /// </summary>
    /// <remarks>
    /// 目標時刻落在兩段之間的空檔時，依行進方向落到最近的段邊界（往後＝下一段的開頭，
    /// 往前＝前一段的尾端），而不是一律跳到當日最後一段：白天錄影一中斷，按一下右方向鍵
    /// 就會從早上跳到晚上，而且真正要看的那一段被靜靜跳過。只有已經越過最後一段才停在尾端。
    /// </remarks>
    private void SeekBy(double seconds)
    {
        if (_current is null || _bandSegments.Count == 0)
        {
            return;
        }

        var targetUtc = _current.StartUtc.AddSeconds(_posInside + seconds);

        // 落點規則是純函式（HeliVMS.Shared.PlaybackSeekPlanner），因為「空隙要落在哪一段」
        // 只能靠測試釘死，而 WPF 這邊沒有執行期測試環境；留在這裡就只能靠人眼驗證。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.Resolve(
            _bandSegments.Select(i => i.Segment),
            targetUtc,
            forward: seconds >= 0);
        if (landing is not { } hit)
        {
            return;
        }

        // 落點帶回的是索引裡的同一個實例；用它找回清單項目，左側選取才會跟著移動。
        var item = _bandSegments.FirstOrDefault(i => ReferenceEquals(i.Segment, hit.Segment));
        if (item is null)
        {
            return;
        }

        SelectSegment(item);
        PlayFrom(hit.Segment, hit.OffsetSeconds);
    }

    /// <summary>時間跳轉（§8.5）：輸入時刻或完整日期時間，跳到覆蓋該時刻的片段並從該點起播。
    /// <para>
    /// 規格寫的是 <c>2026-09-12 14:30:00</c>，但日期平常由 DatePick 記著，兩者各管一件事，
    /// 所以兩種輸入都吃：只給 <c>HH:mm[:ss]</c> 時用 DatePick 的日期，給了日期就先把 DatePick
    /// 換到那天、等那天的索引載入完成再比對，否則比對的是別天的片段索引。
    /// </para>
    /// <para>
    /// 該時刻落在錄影空檔時留在原位並回報，不默默跳到鄰近片段——否則操作員會以為
    /// 自己看到的就是所輸入時刻的畫面。
    /// </para>
    /// </summary>
    private async Task JumpToLocalTime(string text)
    {
        if (PlaybackTimeJump.TryParse(text) is not { } jump)
        {
            StatusText.Text = Localizer.T("Playback.TimeJumpInvalid");
            return;
        }

        var (clock, date) = jump;
        if (date is { } requestedDay && DatePick.SelectedDate?.Date != requestedDay)
        {
            // 換日必須等新的一天的索引載入完成才能比對時刻。SelectedDate 一設就換日，但把
            // _bandSegments 換成那天的清單是非同步的（還要先收檔）：同步往下比對，等於拿昨天的
            // 段去比今天的時刻——命中時跳到別天，沒命中就回報「這裡沒有錄影」，兩種都誤導。
            _suppressSelectionReload = true;
            try
            {
                DatePick.SelectedDate = requestedDay;
            }
            finally
            {
                _suppressSelectionReload = false;
            }

            if (!await ReloadForPlaybackAsync())
            {
                // 收檔期間又有更新的操作（換台／換日）：讓那一次決定畫面，這裡不要再搶。
                return;
            }
        }

        if (_bandSegments.Count == 0 || DatePick.SelectedDate is not DateTime day)
        {
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
            return;
        }

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(day.Date + clock);

        // 與時間軸點擊、續播共用「某一瞬間屬於哪一段」的唯一一份規則（PlaybackSeekPlanner.ResolveAt，
        // 涵蓋範圍 [起點, 結束)）。自己再寫一份閉區間迴圈，輸入的時刻剛好是某段結束、或兩段相接時，
        // 會落在前一段的「第 60 秒」——ffmpeg -ss 60 不產生影格，畫面不動卻回報「已播放完畢」，
        // 而右邊一格就是有影像的；操作員只會覺得時間跳轉壞了。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.ResolveAt(
            _bandSegments.Select(i => i.Segment),
            targetUtc);
        var item = landing is { } hit
            ? _bandSegments.FirstOrDefault(i => ReferenceEquals(i.Segment, hit.Segment))
            : null;
        if (landing is not { } found || item is null)
        {
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
            return;
        }

        SelectSegment(item);
        PlayFrom(found.Segment, found.OffsetSeconds);
    }

    private void OnTimeJumpClicked(object sender, RoutedEventArgs e) => _ = JumpToLocalTime(TimeJumpBox.Text);

    private void OnTimeJumpBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = JumpToLocalTime(TimeJumpBox.Text);
    }

    /// <summary>右鍵快照（§8.5）：把目前顯示中的畫面存成 PNG。
    /// <para>
    /// 用 PNG 而非 JPEG：回放快照是事件佐證，無損才有存證價值，體積不是這裡的瓶頸。
    /// 存到與即時監看同一個 snapshots 目錄（共用清理政策），檔名帶 playback 前綴以區分來源。
    /// </para>
    /// </summary>
    private void OnSnapshotClicked(object sender, RoutedEventArgs e)
    {
        if (_bitmap is null || _current is null)
        {
            StatusText.Text = Localizer.T("Snapshot.NoFrame");
            return;
        }

        try
        {
            var dir = System.IO.Path.Combine(MainWindow.ResolveDataRoot(), "snapshots");
            Directory.CreateDirectory(dir);

            var channelId = ChannelCombo.SelectedItem is ChannelInfo ch ? ch.Id : 0;
            var stem = $"playback-ch{channelId:000}-{DateTime.Now:yyyyMMdd-HHmmss}";
            var path = System.IO.Path.Combine(dir, stem + ".png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            StatusText.Text = string.Format(Localizer.T("Snapshot.Saved"), System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Localizer.T("Snapshot.SaveFailed"), ex.Message);
        }
    }

    /// <summary>逐幀（M17）：播放中先暫停，再向前推進約一幀（依 15fps 估約 66ms），並於首幀到達後再次暫停。</summary>
    private void StepFrame()
    {
        if (_current is null)
        {
            return;
        }

        if (_session is not null)
        {
            // 逐幀要先把播放停住；續播位置由下面這次 PlayFrom 決定，不看按鈕上是「播放」還是「暫停」。
            OnPlayPauseClicked(this, new RoutedEventArgs());
        }

        var dur = LengthOf(_current);
        if (dur <= 0)
        {
            return;
        }

        var frame = 1.0 / 15.0;

        // 推到段長本身會落在「沒有影格」的那一瞬間：session 立刻回報播放完畢，畫面停在原處
        // （操作員看不出發生什麼事，只覺得 F 鍵沒作用）。逐幀到最後一格就該停在最後一格。
        var target = Math.Min(
            _posInside + frame,
            HeliVMS.Shared.PlaybackSeekPlanner.LastFrameOf(_current));
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
        5 => 16,
        _ => 1,
    };

    private void PlayFrom(SegmentRecord seg, double seekSeconds)
    {
        // 上一次逐幀留下的「首幀後暫停」不能在這裡生效：在段尾按 F 會讓 session 立刻結束、
        // 根本沒有影格可以觸發那次暫停，旗標於是活到下一次正常播放——播一格就停住。
        _pendingFramePause = false;
        _playbackEnded = false;
        StopPlayback();
        _rangeStopUtc = null;
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
            ShowPlayheadTime(seg);
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

            // 重播所選區間（§8.5）：播放頭一越過區間盡頭就停，不要繼續播到操作員沒選的畫面。
            if (_rangeStopUtc is { } rangeStop && seg.StartUtc.AddSeconds(_posInside) >= rangeStop)
            {
                _rangeStopUtc = null;
                StopPlayback();
                StatusText.Text = Localizer.T("Playback.RangeEnded");
                return;
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

            var finished = _session;
            _session = null;

            // 只有「真的播到檔尾」才算播完；解碼失敗要留在原位置，按播放是重試而不是從頭。
            _playbackEnded = completed;

            // 播完的 session 不會再被 StopPlayback 收掉（它只處理「正在播」的那一個），
            // 不在此處釋放就每段漏一個 CTS／行程控制代碼；長期連續回放會慢慢累積。
            DisposeQuietly(finished);

            // 重播所選區間（§8.5）跨段時要接著播下一段：錄影一段一個檔，選了 10:00–10:30
            // 而這個設定只切出 5 分鐘一段的話，舊行為會在 10:05 停住並回報「區間播畢」——
            // 操作員只看到選好的區間少了大半。上界仍以原本的絕對時刻為準。
            if (completed && _rangeStopUtc is { } rangeStopUtc)
            {
                var nextInRange = FindNextSegment(seg);
                if (nextInRange is not null && nextInRange.StartUtc < rangeStopUtc)
                {
                    PlayFrom(nextInRange, 0);

                    // PlayFrom 會清掉播放上界（它屬於上一次操作），這裡補回來；
                    // 少了這行，區間重播會一路播到當日最後一段。
                    _rangeStopUtc = rangeStopUtc;
                    return;
                }

                // 區間內沒有下一段＝這次重播播完了。不能掉進下面的「自動續播」：
                // 那會播出選取範圍外的一段（第一格就撞上上界再停住），畫面上閃過一段
                // 與選取無關的影片，接著卻說「區間播畢」——看起來像選錯了段。
                _rangeStopUtc = null;
                StopSync();
                StopAllPanes();
                SetIdleUi();
                ShowPlayheadTime(seg);
                StatusText.Text = Localizer.T("Playback.RangeEnded");
                return;
            }

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
            ShowPlayheadTime(seg);
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
            // 跨段跳轉也要驗：跳到一段幽靈段等於整個跳轉靜靜失敗（ffmpeg 打不開就結束），
            // 使用者只看到「按了沒反應」。
            var (playable, _, _) = ReconcileSegments(
                _segments.ListByRange(ch.Id, current.Stream, dayStartUtc, dayStartUtc.AddDays(1)));
            return playable
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

    /// <summary>收合／展開左側清單（§8.5「可收合側欄」）：9／16 分割時那 250px 讓給牆，
    /// 每一格都會明顯變大——這是操作員唯一能為「格子太小」做的調整。
    /// <para>
    /// 收合後主動重算一次牆：SizeChanged 要等排版回合才到，不重算會有一格寬度用舊值的殘影。
    /// </para>
    /// </summary>
    private void OnSidebarToggleClicked(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SideColumn.Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(250);
        SidePanel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggleButton.Content = Localizer.T(_sidebarCollapsed ? "Playback.SidebarShow" : "Playback.SidebarHide");
        ApplyPlayerFrameGeometry();
    }

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

        // 牆容量（§8.5 多路）：能同時播的路數＝分割數與頻道數取小，直接寫在分割選單旁，
        // 選了 16 卻只有 4 台時一眼看得出來差在哪，不必猜是不是壞了。
        var requested = SplitCount();
        var effective = PlaybackWallCapacity.Effective(requested, ChannelCombo.Items.Count);
        WallCapacityText.Text = string.Format(Localizer.T("Playback.WallCapacity"), effective, requested);
        AutomationProperties.SetName(WallCapacityText, $"WALL:{effective}:{requested}");

        // 多格時主畫面格也要提示可點擊放大（焦點台沒有次畫面格）；單格不留提示避免誤導。
        PlayerFrame.ToolTip = cells > 1 ? Localizer.T("Playback.PaneTip") : null;

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
            var end = EndOf(seg);
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

    /// <summary>
    /// 丟掉目前的播放頭（片段與段內位置），讓「播放」沒有東西可續播。
    /// </summary>
    /// <remarks>
    /// 換頻道／換日時一定要呼叫：<see cref="StopPlayback"/> 刻意保留 <c>_current</c> 是為了
    /// 「暫停後原處續播」，但那個片段屬於上一台。留著它，畫面上是 B 台的清單、按播放卻播出
    /// A 台的檔案——比黑畫面更難查，因為看起來一切正常。
    /// </remarks>
    private void ClearPlaybackHead()
    {
        _current = null;
        _basePos = 0;
        _posInside = 0;
        _rangeStopUtc = null;
        _playbackEnded = false;

        // 上一台的最後一格不能留在畫面上：快照會把它存成這一台的檔案（證據指向錯的攝影機），
        // 而「清單是 B 台、畫面是 A 台」看起來一切正常。寧可先顯示空白提示。
        _bitmap = null;
        _lastWidth = 0;
        _lastHeight = 0;
        PlayerImage.Source = null;
        EmptyHint.Visibility = Visibility.Visible;

        SeekSlider.Value = 0;
        SeekSlider.IsEnabled = false;
        ShowPlayheadTime();
    }

    private void StopPlayback()
    {
        _stopping = true;
        _rangeStopUtc = null;
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

    /// <summary>
    /// 繪製主時間列刻度（§8.5「主時間列：刻度」、§3.4「時間刻度依縮放層級切換」）：
    /// 刻度階梯與刻度位置全由 <see cref="HeliVMS.Storage.PlaybackTimelineScale.Ticks"/> 決定，
    /// 這裡只負責把算好的刻度畫上 canvas。
    /// </summary>
    private void RenderRuler()
    {
        RulerCanvas.Children.Clear();
        var view = _view;
        var width = RulerCanvas.ActualWidth;
        if (view is null || width <= 0)
        {
            return;
        }

        foreach (var tick in HeliVMS.Storage.PlaybackTimelineScale.Ticks(view, TimeZoneInfo.Local))
        {
            var x = view.FractionOf(tick.Utc) * width;
            var line = new Rectangle
            {
                Width = 1,
                Height = tick.IsMajor ? 6 : 3,
                Fill = s_rulerTick,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(line, x);
            Canvas.SetTop(line, Math.Max(0, RulerCanvas.ActualHeight - line.Height));
            RulerCanvas.Children.Add(line);

            if (!tick.IsMajor || tick.Label.Length == 0)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = tick.Label,
                FontSize = 10,
                Foreground = s_rulerText,
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, 0);
            RulerCanvas.Children.Add(label);
        }
    }

    /// <summary>
    /// 通道色帶列（§8.5「色帶按通道橫向排列，多路同時比對一目了然」）：回放牆每一格
    /// （主＋次）在帶子上方各佔一排，畫該通道當日在檢視窗內的錄影涵蓋；焦點台那排再疊事件刻度。
    /// 多路比對時「哪台哪一段沒錄」不用逐格 solo 就看得出來。高度隨牆的格數變化（Auto 行）。
    /// </summary>
    private void RenderChannelBands()
    {
        ChannelBandsCanvas.Children.Clear();
        if (_dayStartUtc == default)
        {
            ChannelBandsCanvas.Height = 0;
            return;
        }

        var rows = new List<(ChannelInfo Channel, IReadOnlyList<SegmentRecord> Segments, bool IsFocus)>();
        if (ChannelCombo.SelectedItem is ChannelInfo focus)
        {
            rows.Add((focus, _bandSegments.Select(i => i.Segment).ToList(), true));
        }

        foreach (var pane in _panes)
        {
            rows.Add((pane.Channel, pane.Segments, false));
        }

        var pitch = ChannelBandStripHeight + ChannelBandStripGap;
        ChannelBandsCanvas.Height = Math.Max(0, rows.Count * pitch - ChannelBandStripGap);
        var width = ChannelBandsCanvas.ActualWidth;
        if (rows.Count == 0 || width <= 0)
        {
            return;
        }

        var view = View();
        for (var i = 0; i < rows.Count; i++)
        {
            var top = i * pitch;
            var backdrop = new Rectangle
            {
                Width = width,
                Height = ChannelBandStripHeight,
                Fill = s_bandEmptyFill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(backdrop, 0);
            Canvas.SetTop(backdrop, top);
            ChannelBandsCanvas.Children.Add(backdrop);

            var brush = ChannelBrush(rows[i].Channel.Id);
            foreach (var seg in rows[i].Segments)
            {
                var end = EndOf(seg);
                var left = view.FractionOf(seg.StartUtc);
                var right = view.FractionOf(end);
                if (right <= 0 || left >= 1)
                {
                    continue;
                }

                var clippedLeft = Math.Max(0, left);
                var clippedRight = Math.Min(1, right);
                var rect = new Rectangle
                {
                    Width = Math.Max(1, (clippedRight - clippedLeft) * width),
                    Height = ChannelBandStripHeight,
                    Fill = brush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, clippedLeft * width);
                Canvas.SetTop(rect, top);
                ChannelBandsCanvas.Children.Add(rect);
            }

            // 事件刻度只畫焦點台那排：事件清單本來就只載焦點頻道（LoadSegments），其他排畫了會是空的。
            if (!rows[i].IsFocus)
            {
                continue;
            }

            foreach (var evt in _events)
            {
                var frac = view.FractionOf(evt.StartUtc);
                if (frac < 0 || frac > 1)
                {
                    continue;
                }

                var tick = new Rectangle
                {
                    Width = 1,
                    Height = ChannelBandStripHeight,
                    Fill = Brushes.White,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(tick, frac * width);
                Canvas.SetTop(tick, top);
                ChannelBandsCanvas.Children.Add(tick);
            }
        }
    }

    /// <summary>色帶列寬度跟窗走：視窗縮放後要照新寬度重畫，否則色條會停在舊寬度或整段縮在左邊。</summary>
    private void OnChannelBandsSizeChanged(object sender, SizeChangedEventArgs e) => RenderChannelBands();

    /// <summary>
    /// 繪製時間軸：主時間列刻度＋當日色帶（M84／§8.5）——依 PlaybackTimeline L0 的 fractions
    /// 畫每段藍色區塊＋事件標記＋覆蓋率標籤，全部映射到目前的檢視窗。
    /// </summary>
    private void RenderBlocks()
    {
        // 刻度畫在色帶上方的獨立 canvas，與資料有無無關：空資料的日子也要看得見時間刻度。
        RenderRuler();
        SyncZoomSlider();
        RenderChannelBands();

        for (var i = BandCanvas.Children.Count - 1; i >= 0; i--)
        {
            var child = BandCanvas.Children[i];
            if (!ReferenceEquals(child, _cursor) && !ReferenceEquals(child, _cursorCap))
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

        // 選擇區間窗最後畫（要疊在色塊與書籤之上），縮放/換日後也跟著新檢視窗對齊。
        RenderSelectionWindow();

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
        // Bar 是對全日窗的比例，先轉回時刻再映射到檢視窗：縮放後窗外的區塊整塊跳過，
        // 壓到邊邊的只畫露出來的那一截，否則區塊會伸出帶子或整條錯位。
        if (width > 0)
        {
            var fill = (Brush)new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0xC9)).GetAsFrozen();
            var barHeight = Math.Max(6, height - BandBarTop - BandMarkerSize - BandMarkerMargin * 2);
            var view = View();
            foreach (var bar in _timeline.Bars)
            {
                var leftUtc = _timeline.DayUtc.AddSeconds(bar.LeftFraction * PlaybackTimeline.DaySeconds);
                var rightUtc = leftUtc.AddSeconds(bar.WidthFraction * PlaybackTimeline.DaySeconds);
                var left = view.FractionOf(leftUtc);
                var right = view.FractionOf(rightUtc);
                if (right <= 0 || left >= 1)
                {
                    continue;
                }

                var clippedLeft = Math.Max(0, left);
                var clippedRight = Math.Min(1, right);
                var rect = new Rectangle
                {
                    Width = Math.Max(2, (clippedRight - clippedLeft) * width),
                    Height = barHeight,
                    Fill = fill,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, clippedLeft * width);
                Canvas.SetTop(rect, BandBarTop);
                BandCanvas.Children.Add(rect);
            }

            RenderKeyframeTicks(barHeight);
        }

        var coveragePct = (int)Math.Round((1 - _timeline.GapFraction) * 100);
        CoverageText.Text = string.Format(Localizer.T("Playback.Coverage"), coveragePct, _timeline.GapCount);
        AutomationProperties.SetName(CoverageText, $"COV:{coveragePct}:{_timeline.GapCount}:{_timeline.Bars.Count}");

        RenderMarkers();
    }

    /// <summary>
    /// 在色帶上疊一層關鍵幀刻度（§3.4「播放頭為關鍵幀吸附」的視覺依據）。
    /// </summary>
    /// <remarks>
    /// 只在檢視窗細到「平均關鍵幀間隔 ≥ <see cref="KeyframeTickMinPx"/> 像素」時才畫：
    /// 全日窗下單一區段可能有上百個關鍵幀，全畫會把藍色區塊糊成一條深色帶，
    /// 操作員反而看不出哪裡有錄影——刻度是輔助，搶過錄影色塊就本末倒置。
    /// </remarks>
    private void RenderKeyframeTicks(double bandHeight)
    {
        var width = BandCanvas.ActualWidth;
        if (width <= 0 || _keyframeCache.Count == 0)
        {
            return;
        }

        var view = View();
        var tickHeight = Math.Max(3, bandHeight - 2);

        foreach (var item in _bandSegments)
        {
            if (!_keyframeCache.TryGetValue(item.Segment.Id, out var keys) || keys.Count < 2)
            {
                continue;
            }

            // 密度判斷抽到 Storage 的純函式（PlaybackKeyframeDensityTests 有行為測試）：
            // 太密的區段整段不畫，免得把錄影色塊糊掉。
            if (!PlaybackKeyframeDensity.ShouldDraw(keys, view.Span.TotalSeconds, width, KeyframeTickMinPx))
            {
                continue;
            }

            var startUtc = item.Segment.StartUtc;
            foreach (var key in keys)
            {
                var frac = view.FractionOf(startUtc.AddSeconds(key.TimeSec));
                if (frac < 0 || frac > 1)
                {
                    continue;
                }

                var tick = new Rectangle
                {
                    Width = 1,
                    Height = tickHeight,
                    Fill = s_keyframeTick,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(tick, frac * width);
                Canvas.SetTop(tick, BandBarTop);
                BandCanvas.Children.Add(tick);
            }
        }
    }

    /// <summary>時間軸帶當日零時（UTC）。</summary>
    /// <remarks>
    /// 一律以 DatePicker 選的那一天為準，不看第一段從幾點開始：錄影是連續的，跨午夜的
    /// 那一段（例：23:55–00:05）會同時屬於前後兩天，用它推算「這一天」會整整差一天，
    /// 時間軸建構於是把當天其餘的段全部裁掉，帶子上只剩那一小段。
    /// </remarks>
    private DateTime DayStartUtc()
    {
        if (_dayStartUtc != default)
        {
            return _dayStartUtc;
        }

        var day = DatePick.SelectedDate?.Date ?? DateTime.Today;
        return TimeZoneInfo.ConvertTimeToUtc(day);
    }

    /// <summary>目前檢視窗；未載入日時退回全日窗（色帶、游標、點擊換算共用同一條路徑）。</summary>
    private HeliVMS.Storage.TimelineViewWindow View()
        => _view ?? HeliVMS.Storage.PlaybackTimelineScale.FullDay(
            _dayStartUtc == default ? DayStartUtc() : _dayStartUtc);

    /// <summary>在時間軸帶上畫當日書籤（橙色點，滑過顯示筆記）。</summary>
    private void RenderBookmarks()
    {
        var width = BandCanvas.ActualWidth;
        if (width <= 0 || _bookmarks.Count == 0)
        {
            return;
        }

        var brush = (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x3C)).GetAsFrozen();
        var view = View();
        foreach (var b in _bookmarks)
        {
            var frac = view.FractionOf(b.AtUtc);
            if (frac < 0 || frac > 1)
            {
                continue;
            }

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

        var view = View();
        foreach (var marker in markers)
        {
            var frac = view.FractionOf(marker.Utc);
            if (frac < 0 || frac > 1)
            {
                continue;
            }

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

        // 起止手柄（§8.5「拖拽起止手柄」）：抓著選擇窗任一側只要調那一側，不必整個重畫。
        // 放在 Shift 判定之前：手柄是眼前看得到的東西，抓住它就是調它，不該還要按住 Shift。
        if (_rangeSelection is { } existing)
        {
            var grabX = e.GetPosition(BandCanvas).X;
            var view = View();
            var edge = PlaybackRangeHandle.HitEdge(
                grabX,
                view.FractionOf(existing.StartUtc) * BandCanvas.ActualWidth,
                view.FractionOf(existing.EndUtc) * BandCanvas.ActualWidth,
                RangeHandlePx);
            if (edge != 0)
            {
                _selecting = true;
                _rangeResizeEdge = edge;
                _scrubbing = false;
                if (sender is UIElement handleElement)
                {
                    handleElement.CaptureMouse();
                }

                return;
            }
        }

        // Shift+拖曳＝選取區間（§8.5「拖拽選取範圍」）；一般拖曳仍是拖曳預覽（scrub），兩者互斥。
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _selecting = true;
            _rangeResizeEdge = 0;
            _scrubbing = false;
            if (sender is UIElement selectElement)
            {
                selectElement.CaptureMouse();
            }

            var x = Math.Clamp(e.GetPosition(BandCanvas).X, 0, BandCanvas.ActualWidth);
            _selectAnchorUtc = View().TimeAtFraction(x / BandCanvas.ActualWidth);
            _rangeSelection = null;
            return;
        }

        _rangeResizeEdge = 0;
        _scrubbing = true;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }

        PreviewBandAt(e.GetPosition(BandCanvas).X);
    }

    private void OnBandMouseMove(object sender, MouseEventArgs e)
    {
        if (_selecting)
        {
            if (_rangeResizeEdge != 0)
            {
                ResizeRangeSelection(e.GetPosition(BandCanvas).X);
            }
            else
            {
                UpdateRangeSelection(e.GetPosition(BandCanvas).X);
            }

            return;
        }

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

        // Shift 拖曳選取途中放開：定案選擇、不要順便跳轉——選完再用右鍵選單重播/匯出。
        if (_selecting)
        {
            _selecting = false;
            _rangeResizeEdge = 0;
            if (BandCanvas.ActualWidth <= 0 || _rangeSelection is not { } range
                || (range.EndUtc - range.StartUtc).TotalSeconds < 1)
            {
                // 幾個像素的「拖曳」其實是點擊：清掉零寬區間，免得選單上重播/匯出都點不動。
                _rangeSelection = null;
            }

            RenderSelectionWindow();
            return;
        }

        if (BandCanvas.ActualWidth <= 0)
        {
            return;
        }

        if (_bandSegments.Count == 0)
        {
            // 整天都沒有錄影：整條帶子都是灰的，任何位置都算無錄影，給明確回饋而不是沒反應。
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
            ShowPlayheadTime();
            return;
        }

        var frac = Math.Max(0, Math.Min(1, e.GetPosition(BandCanvas).X / BandCanvas.ActualWidth));
        var targetUtc = View().TimeAtFraction(frac);

        // 「點這一刻」的落點規則與方向鍵共用同一份純函式：接縫歸後一段、段尾那一瞬間算空檔、
        // 空檔就說沒錄影。自己再寫一份閉區間迴圈，點在接縫上會落進前一段的第 60 秒（沒有影格），
        // 立刻回報「已播放結束」——後面明明還有整段錄影。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.ResolveAt(
            _bandSegments.Select(i => i.Segment),
            targetUtc);
        var item = landing is { } hit
            ? _bandSegments.FirstOrDefault(i => ReferenceEquals(i.Segment, hit.Segment))
            : null;
        if (landing is not { } found || item is null)
        {
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
            ShowPlayheadTime();
            return;
        }

        SelectSegment(item);
        PlayFrom(found.Segment, found.OffsetSeconds);
    }

    /// <summary>拖曳中的選擇窗：每像素都在動，只重畫覆蓋層本身，不重畫整條帶子。</summary>
    private void UpdateRangeSelection(double x)
    {
        var frac = Math.Clamp(x / BandCanvas.ActualWidth, 0, 1);
        var utc = View().TimeAtFraction(frac);
        _rangeSelection = _selectAnchorUtc <= utc ? (_selectAnchorUtc, utc) : (utc, _selectAnchorUtc);
        RenderSelectionWindow();
    }

    /// <summary>拖曳起止手柄：只動被抓的那一側，另一側固定不動（計算見 <see cref="PlaybackRangeHandle.Drag"/>，
    /// 含跨過對側時交換兩端與手柄身分）。</summary>
    private void ResizeRangeSelection(double x)
    {
        if (_rangeSelection is not { } range)
        {
            return;
        }

        var frac = Math.Clamp(x / BandCanvas.ActualWidth, 0, 1);
        var utc = View().TimeAtFraction(frac);
        var (start, end, edge) = PlaybackRangeHandle.Drag(
            range.StartUtc, range.EndUtc, utc, _rangeResizeEdge);
        _rangeResizeEdge = edge;
        _rangeSelection = (start, end);
        RenderSelectionWindow();
    }

    /// <summary>畫選擇區間窗（§8.5）：半透明藍底＋描邊＋左右起止手柄，跟著檢視窗縮放對齊；無選擇時只清上一層。</summary>
    private void RenderSelectionWindow()
    {
        if (_selectionRect is { } old && BandCanvas.Children.Contains(old))
        {
            BandCanvas.Children.Remove(old);
        }

        _selectionRect = null;
        if (_rangeSelection is not { } range)
        {
            return;
        }

        var width = BandCanvas.ActualWidth;
        var height = BandCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var view = View();
        var left = view.FractionOf(range.StartUtc);
        var right = view.FractionOf(range.EndUtc);
        if (right <= 0 || left >= 1)
        {
            return;
        }

        var clippedLeft = Math.Max(0, left);
        var clippedRight = Math.Min(1, right);
        var windowWidth = Math.Max(2, (clippedRight - clippedLeft) * width);

        // 整組（底＋兩條手柄）裝在一個 Canvas 裡：換掉時一起換，不會留下上一次的手柄。
        var container = new Canvas { Width = windowWidth, Height = height, IsHitTestVisible = false };
        container.Children.Add(new Rectangle
        {
            Width = windowWidth,
            Height = height,
            Fill = s_selectionFill,
            Stroke = s_selectionStroke,
            StrokeThickness = 1,
        });

        // 起止手柄：兩端各一條亮色豎條，讓「這裡抓得動」看得出來。
        const double GripWidth = 3;
        foreach (var gripAt in new[] { 0d, Math.Max(0, windowWidth - GripWidth) })
        {
            var grip = new Rectangle
            {
                Width = GripWidth,
                Height = height,
                Fill = s_selectionStroke,
            };
            Canvas.SetLeft(grip, gripAt);
            container.Children.Add(grip);
        }

        Canvas.SetLeft(container, clippedLeft * width);
        Canvas.SetTop(container, 0);
        BandCanvas.Children.Add(container);
        _selectionRect = container;
    }

    /// <summary>右鍵選單「重播所選區間」（§8.5「立即可重播此段」）：從區間起點播、越過盡頭自動停。</summary>
    private void OnReplayRangeClicked(object sender, RoutedEventArgs e)
    {
        if (_rangeSelection is not { } range)
        {
            return;
        }

        foreach (var item in _bandSegments)
        {
            var seg = item.Segment;
            var end = EndOf(seg);
            if (seg.StartUtc <= range.StartUtc && range.StartUtc <= end)
            {
                SelectSegment(item);
                PlayFrom(seg, Math.Max(0, (range.StartUtc - seg.StartUtc).TotalSeconds));
                _rangeStopUtc = range.EndUtc;
                return;
            }
        }

        StatusText.Text = Localizer.T("Playback.NoRecordingHere");
    }

    /// <summary>右鍵選單「匯出所選區間」（§8.5「立即可匯出此段」）：開匯出精靈並預填精確起止。</summary>
    private void OnExportRangeClicked(object sender, RoutedEventArgs e)
    {
        if (_rangeSelection is not { } range || ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            return;
        }

        new ExportWindow(_store, MainWindow.ResolveDataRoot(), ch.Id, range.StartUtc, range.EndUtc) { Owner = this }.ShowDialog();
    }

    private void OnClearRangeClicked(object sender, RoutedEventArgs e)
    {
        _rangeSelection = null;
        RenderSelectionWindow();
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
        PlaceCursor(frac * width);

        TimeText.Text = TimeZoneInfo
            .ConvertTimeFromUtc(View().TimeAtFraction(frac), TimeZoneInfo.Local)
            .ToString("HH:mm:ss");
    }

    /// <summary>
    /// 滑鼠離開時間軸：把被 <see cref="PreviewBandAt"/> 蓋掉的讀數還原成播放頭。
    /// 少了這一步，暫停時讀數會永遠停在滑鼠最後掠過的那一刻——看起來像播放頭真的在那裡。
    /// </summary>
    private void OnBandMouseLeave(object sender, MouseEventArgs e) => ShowPlayheadTime();

    /// <summary>
    /// 滾輪無級縮放（§8.5「縮放中心跟隨滑鼠游標」、§3.4「支援滑鼠滾輪縮放」）：
    /// 錨點＝游標所在的畫面位置，對應時刻在縮放前後留在原地。
    /// 只換檢視窗、不重查資料庫：當日資料已經在手，10 秒窗用同一批 segments 就畫得出來。
    /// </summary>
    private void OnBandMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_view is null || BandCanvas.ActualWidth <= 0 || e.Delta == 0)
        {
            return;
        }

        var anchor = Math.Clamp(e.GetPosition(BandCanvas).X / BandCanvas.ActualWidth, 0, 1);
        _view = HeliVMS.Storage.PlaybackTimelineScale.Zoom(
            _view,
            _dayStartUtc,
            _dayStartUtc.AddDays(1),
            anchor,
            e.Delta / 120.0);
        RenderBlocks();
        UpdateCursor(_posInside);
        e.Handled = true;
    }

    /// <summary>滑桿重新輸入期間不回應 ValueChanged，避免「設定值→事件→再設定值」的回授圈。</summary>
    private bool _syncingZoomSlider;

    /// <summary>把目前檢視窗同步到縮放滑桿；經 RenderBlocks 單一漏斗呼叫，滾輪與載入日切換後滑桿位置都會跟上。</summary>
    private void SyncZoomSlider()
    {
        _syncingZoomSlider = true;
        try
        {
            ZoomSlider.Value = PlaybackTimelineScale.SliderValueFromSpan(View().Span);
        }
        finally
        {
            _syncingZoomSlider = false;
        }
    }

    /// <summary>
    /// 縮放滑桿（§8.5 主時間列）：拖曳改窗寬，錨在目前檢視窗中心——滑桿是粗調、
    /// 滾輪才是跟游標的細調。窗夾回當日範圍後照一般縮放重畫刻度與色塊。
    /// </summary>
    private void OnZoomSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoomSlider || _view is null || _dayStartUtc == default)
        {
            return;
        }

        var spanSeconds = PlaybackTimelineScale.SpanSecondsFromSliderValue(e.NewValue);
        var dayEnd = _dayStartUtc.AddDays(1);
        var center = _view.StartUtc + _view.Span / 2;
        var startUtc = center.AddSeconds(-spanSeconds / 2);
        if (startUtc < _dayStartUtc)
        {
            startUtc = _dayStartUtc;
        }

        var endUtc = startUtc.AddSeconds(spanSeconds);
        if (endUtc > dayEnd)
        {
            endUtc = dayEnd;
            startUtc = endUtc.AddSeconds(-spanSeconds);
        }

        _view = new HeliVMS.Storage.TimelineViewWindow(startUtc, endUtc);
        RenderBlocks();
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

        // 拖到最右邊＝段長本身，而段尾那一瞬間沒有影格（ffmpeg -ss <段長> 什麼都不產生）：
        // 直接開播會立刻回報「已播放完畢」、畫面停在上一格，看起來像拖到底就壞掉。
        PlayFrom(
            _current,
            Math.Min(offset, HeliVMS.Shared.PlaybackSeekPlanner.LastFrameOf(_current)));
    }

    /// <summary>將播放位置遊標移到時間軸帶上的對應時刻（映射到目前檢視窗）。</summary>
    private void UpdateCursor(double posSeconds)
    {
        if (_current is null || BandCanvas.ActualWidth <= 0)
        {
            HideCursor();
            return;
        }

        var frac = View().FractionOf(_current.StartUtc.AddSeconds(posSeconds));
        if (frac < 0 || frac > 1)
        {
            // 播放頭在檢視窗外（縮放到別的時段時）：藏起來。夾到帶子邊邊會讓操作員
            // 以為播放頭還在畫面上，點邊邊還真的跳回邊界時刻。
            HideCursor();
            return;
        }

        PlaceCursor(frac * BandCanvas.ActualWidth);
    }

    /// <summary>把播放頭（線身＋頂端菱形）放到帶子上的指定 x；兩個元素必須一起搬，
    /// 分開寫會在之後只改到其中一邊，菱形脫離線頂時才發現。</summary>
    private void PlaceCursor(double x)
    {
        _cursor.Height = BandCanvas.ActualHeight;
        Canvas.SetLeft(_cursor, x - 1);
        Canvas.SetTop(_cursor, 0);
        Canvas.SetLeft(_cursorCap, x - CursorCapSize / 2);
        Canvas.SetTop(_cursorCap, 0);
    }

    /// <summary>把播放頭整組移出畫面（沒有播放中區段或位置在檢視窗外）。</summary>
    private void HideCursor()
    {
        Canvas.SetLeft(_cursor, -20);
        Canvas.SetLeft(_cursorCap, -20);
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

    /// <summary>
    /// 時間讀數回到「播放頭 / 段長」，全視窗唯一一份。
    /// </summary>
    /// <remarks>
    /// 同一個字串原本在 <see cref="OnFrame"/>、<see cref="OnEnded"/> 與
    /// <see cref="ClearPlaybackHead"/> 各寫一次，格式一改就會有三處不同調。
    /// 另外，滑鼠掠過時間軸（<see cref="PreviewBandAt"/>）或點在空檔時，這個唯讀的讀數會被
    /// 暫態的絕對時刻蓋掉；暫停中沒有影格再進來就沒人改回來，操作員看到的是自己從未停過的
    /// 時刻——拿它去抄時間、比對事件都會錯。所以滑鼠離開帶子、點到空檔之後都要叫回這裡。
    /// </remarks>
    private void ShowPlayheadTime(SegmentRecord? segment = null)
    {
        var length = (segment ?? _current) is { } seg ? LengthOf(seg) : 0;
        TimeText.Text = $"{FormatTime(_posInside)} / {FormatTime(length)}";
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>
    /// 段的結束時刻：與時間軸色塊、事件比對、多路同步共用唯一一份換算
    /// （<see cref="HeliVMS.Shared.PlaybackSegmentTiming"/>）；索引沒寫長度時以 10 秒估算。
    /// </summary>
    /// <remarks>
    /// 這個換算散布在跳轉、書籤、事件、色帶、跨段續播五六條路徑上；只要有一條寫成 <c>?? 0</c>
    /// （長度還沒回填的段），那一條就永遠對不到時刻——點下去說「這裡沒有錄影」，色帶上也少一塊。
    /// 因此不要在畫面上另寫一份，一律呼叫這裡。
    /// </remarks>
    private static DateTime EndOf(SegmentRecord seg) => HeliVMS.Shared.PlaybackSegmentTiming.EndOf(seg);

    /// <summary>段長（秒），與 <see cref="EndOf"/> 同一口徑；索引沒寫長度時為 10 秒。</summary>
    private static double LengthOf(SegmentRecord seg) => HeliVMS.Shared.PlaybackSegmentTiming.LengthOf(seg);

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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

    /// <summary>回放牆格數（1／2／4／9／16）。</summary>
    private int SplitCount() => SplitCombo.SelectedIndex switch
    {
        1 => 2,
        2 => 4,
        3 => 9,
        4 => 16,
        _ => 1,
    };

    /// <summary>
    /// 主畫面格單擊也放大成單畫面：焦點台（通常是攝影機 01）永遠排在主格、沒有次畫面可點，
    /// 不補這條的話多路時點主畫面完全沒反應。它本來就是焦點，不需要換台，只要把牆收成一格。
    /// 單格時點擊不做事，避免誤觸。
    /// </summary>
    private void OnMainPlayerClicked(object sender, MouseButtonEventArgs e)
    {
        if (_panes.Count == 0)
        {
            return;
        }

        SplitCombo.SelectedIndex = 0;
    }

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
            PlayerFrame.BorderBrush = s_defaultFrameBorder;
            ApplyPlayerFrameGeometry();
            RenderChannelBands();
            return;
        }

        // 主畫面邊框＝焦點台在色帶列的顏色，與次畫面同一套色盤。
        PlayerFrame.BorderBrush = ChannelBrush(focus.Id);
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
        RenderChannelBands();
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
            // 邊框色＝這台在通道色帶列的顏色：不用讀 OSD 就能把色帶對回畫面（§8.5 多路比對）。
            BorderBrush = ChannelBrush(channel.Id),
            BorderThickness = new Thickness(1),
            Background = Frozen(Color.FromRgb(0x05, 0x09, 0x0E)),
            Cursor = Cursors.Hand,
            ToolTip = $"{channel.Name} · {Localizer.T("Playback.PaneTip")}",
            Child = grid,
        };

        var pane = new Pane { Channel = channel, Frame = frame, Image = image, Osd = osd, Segments = segments };

        // 單擊放大成單畫面（§8.5 主焦點）：把該台切到主畫面並收成一格，時間軸／清單／遮蔽疊圖都跟著它。
        frame.MouseLeftButtonDown += (_, _) => FocusPane(pane);
        return pane;
    }

    private void FocusPane(Pane pane) => _ = FocusPaneAsync(pane);

    private async Task FocusPaneAsync(Pane pane)
    {
        // 先記播放頭：換台會清掉它（那時的 _current 還屬於原來那台），不先記下來放大後就停在原點。
        var resumeUtc = _session is not null && _current is not null
            ? _current.StartUtc.AddSeconds(_posInside)
            : (DateTime?)null;

        // 格數選到「1」＝單畫面（OnSplitChanged 重建牆時就沒有次畫面）；再把點到那台設成焦點。
        // 先切格數再切頻道，避免先換焦點時用舊格數短暫長出一圈要立刻拆掉的次畫面。
        SplitCombo.SelectedIndex = 0;

        // 續播一定要等新頻道的索引載入完成：先續播再載入，會被那次載入的 StopPlayback 停掉，
        // 現場看到的就是「單擊放大後畫面停住」。
        if (!await SwitchChannelAsync(pane.Channel.Id))
        {
            return;
        }

        if (resumeUtc is { } target)
        {
            if (ResumePlaybackAt(target))
            {
                return;
            }

            // 各台錄影的切點不同，點次畫面放大時，那台在母鐘那一刻經常正好是空檔。
            // 換台已經把播放頭清掉，這裡不出聲就只剩一個全黑播放器——和「解碼壞了」
            // 在畫面上長得一模一樣，而且播放鈕還是灰的，操作員無從判斷該換時刻還是換台。
            StatusText.Text = Localizer.T("Playback.NoRecordingHere");
        }
    }

    /// <summary>
    /// 把主畫面接到指定 UTC 時刻繼續播：在當日時間軸找到涵蓋該時刻的片段並從偏移量起播。
    /// 該台此刻無錄影就不硬跳（停在已載入畫面），語意與次畫面同步的空隙處理一致。
    /// </summary>
    /// <returns>true＝已找到涵蓋該時刻的片段並開播；false＝那一刻是空檔，畫面維持原狀。</returns>
    private bool ResumePlaybackAt(DateTime targetUtc)
    {
        // 「這一刻真的有錄影」由 PlaybackSeekPlanner.ResolveAt 回答：空檔回 null，所以不會像
        // 方向鍵那樣悄悄落到下一個小時的畫面（那會讓操作員以為同步壞了）；而接縫與段尾的判定
        // 也因此和其他入口一致。自己寫一份閉區間比對，接縫上會命中前一段的第 60 秒＝沒有影格。
        var landing = HeliVMS.Shared.PlaybackSeekPlanner.ResolveAt(
            _bandSegments.Select(i => i.Segment),
            targetUtc);
        var item = landing is { } hit
            ? _bandSegments.FirstOrDefault(i => ReferenceEquals(i.Segment, hit.Segment))
            : null;
        if (landing is not { } found || item is null)
        {
            return false;
        }

        SelectSegment(item);
        PlayFrom(found.Segment, found.OffsetSeconds);
        return true;
    }

    private IReadOnlyList<SegmentRecord> LoadPaneSegments(int channelId, DateTime day)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(day.Date);
        try
        {
            var (playable, _, _) = ReconcileSegments(
                _segments.ListByRange(channelId, "main", startUtc, startUtc.AddDays(1)));
            return playable
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

                // 同步計時器每 150ms 跑一次；每次都重寫同一個字串會讓次畫面的 TextBlock
                // 反覆重新排版（4 路就是每秒 27 次），畫面在比對時看起來會抖。
                if (!pane.NoRecording)
                {
                    pane.NoRecording = true;
                    pane.Osd.Text = $"{pane.Channel.Name} · {Localizer.T("Playback.NoRecordingHere")}";
                }

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
        var finished = pane.Session;
        pane.Session = null;
        DisposeQuietly(finished);
    }
}