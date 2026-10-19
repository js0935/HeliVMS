using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Alarms;
using HeliVMS.App.Services;
using HeliVMS.Licensing;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Recording.Talk;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Path = System.IO.Path;

namespace HeliVMS.App;

/// <summary>
/// 主視窗：M1–M2 監看（單一/多格佈局，M9 擴充 3×3／4×4 與佈局記憶）與錄影控制中心。
/// 資料基準目錄 C:\HeliVMSData（§2.1）。
/// </summary>
public partial class MainWindow : Window
{
    internal const string DefaultDataRoot = DataRootLayout.DefaultRoot;
    private const int MaxCells = 64;

    /// <summary>佈局下拉的最大索引（對應 <see cref="LayoutCellCounts"/>）。佈局記憶／F11 還原都以此為界，
    /// 之前寫死 3 會讓較大的佈局重開後被打回 1×1。</summary>
    private const int MaxLayoutIndex = 12;

    /// <summary>各佈局選項的目標格數；列×欄由 <see cref="MonitorGrid.Fit"/> 依螢幕比例自動決定。
    /// 依使用者回饋加入能整齊貼合 16:9 畫面的路數（6／12／20／30／42／56）並延伸到 64 路，
    /// 故索引即格數的清單長度減一（見 <see cref="MaxLayoutIndex"/>）。</summary>
    private static readonly int[] LayoutCellCounts = [1, 4, 6, 9, 12, 16, 20, 25, 30, 36, 42, 56, 64];
    private const string UiSettingsFile = "ui.json";

    private readonly string _dataRoot;
    private string _recordingsRoot;
    private readonly string _legacyDir;
    private SqliteStore? _store;
    private ChannelRepository? _channels;
    private SegmentRepository? _segRepo;
    private LicenseUiGate? _licenseGate;
    private LicenseService? _licenseService;
    private ChannelManager? _manager;
    private CancellationTokenSource? _bgCts;
    private WriteableBitmap?[] _bitmap = new WriteableBitmap?[MaxCells];
    private readonly int?[] _cellChannel = new int?[MaxCells];

    /// <summary>每格的 RTSP 狀態。<b>只由 UI 執行緒讀寫</b>（背景執行緒的狀態一律經 Dispatcher 進來）。</summary>
    /// <remarks>
    /// 這張表是修掉一個真實 bug 的關鍵：原本格內狀態文字是從「文字是否以「連線中／重連中」開頭」
    /// 反推出狀態的，而佈局切換會把所有格文字重寫成「未連線」，於是之後影像明明在跑、
    /// 狀態卻永遠停在「未連線」。改成以權威狀態為準，文字只是它的投影。
    /// </remarks>
    private readonly RtspState[] _cellStates = new RtspState[MaxCells];

    /// <summary>每格待繪製的最新影格（latest-wins 合併，避免 Dispatcher 佇列無界膨脹）。</summary>
    private readonly LatestFrameSlots<VideoFrame> _pendingFrames = new(MaxCells);

    /// <summary>每格實際畫面更新率／畫面年齡估算（監看介面原本完全沒有 fps／延遲資訊）。</summary>
    private readonly FrameRateMeter[] _cellMeters = CreateMeters(MaxCells);

    /// <summary>每格目前畫面解析度（由抵達的影格決定，顯示在狀態文字上）。</summary>
    private readonly (int Width, int Height)?[] _cellResolution = new (int, int)?[MaxCells];

    private static FrameRateMeter[] CreateMeters(int count)
    {
        var meters = new FrameRateMeter[count];
        for (var i = 0; i < count; i++)
        {
            meters[i] = new FrameRateMeter();
        }

        return meters;
    }
    private readonly Image[] _cellImages = new Image[MaxCells];
    private readonly Canvas[] _cellOverlays = new Canvas[MaxCells];
    private readonly TextBlock[] _cellTexts = new TextBlock[MaxCells];
    /// <summary>每格運動閃框的到期時刻（UTC）。</summary>
    private readonly DateTime[] _motionFlashUntil = new DateTime[MaxCells];

    /// <summary>錄影停滯中（尚未復原）的頻道 → 停滯資訊。</summary>
    private readonly Dictionary<int, RecordingStallInfo> _recordingStalls = new();

    private DispatcherTimer? _motionTimer;
    private PatrolHostService? _patrolHost;
    private System.Threading.Timer? _notifyBacklogTimer;

    /// <summary>格位對齊（reconcile）的序列化閘門；見 <see cref="OnLayoutChanged"/>。</summary>
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    /// <summary>F11 展開時要聚焦的格；-1 表示從頭開始。由 <see cref="OnLayoutChanged"/> 消耗。</summary>
    private int _pendingSingleCell = -1;

    /// <summary>監看牆自動跳台（每5秒依目前分割跳到下一組）。</summary>
    private bool _wallSequenceEnabled;
    private int _wallSequenceIndex;
    private DispatcherTimer? _wallSequenceTimer;
    private readonly TextBlock[] _cellBadges = new TextBlock[MaxCells];
    private readonly Border[] _cellBorders = new Border[MaxCells];

    /// <summary>
    /// 是否在格內顯示「即時監看·解析度·fps·延遲」狀態文字（app_settings <c>monitor.show_status</c>）。
    /// 預設關閉：這行資訊平時會蓋住畫面，只在需要診斷時由右鍵選單打開。
    /// </summary>
    private bool _showCellStatus;

    /// <summary>各格右鍵選單中的「顯示即時狀態」項目；切換時同步所有格的勾選狀態。</summary>
    private readonly List<MenuItem> _cellStatusMenuItems = [];

    private readonly Dictionary<int, RtspState> _channelStates = new();
    private readonly Dictionary<int, DateTime> _channelLastFrame = new();
    private readonly Dictionary<int, (bool Enabled, int Interval)> _aiPolicyCache = new();

    // 沒有即時影格時的佔位圖：未指派頻道＝Disable.png，已指派但斷線＝Invisible.png（延遲載入）。
    private BitmapSource? _cellNoChannelImage;
    private BitmapSource? _cellDisconnectedImage;
    private int _selectedCell = -1;
    private IReadOnlyList<ChannelInfo> _channelList = [];

    // 監看群組（監控中心頁簽）。_activeGroupId 為 null＝「全部」頁簽（顯示所有頻道）。
    // 群組成員的歸屬與順序由 ChannelGroupRepository 管理（見 MonitorGroupWindow 的群組編輯區）。
    private ChannelGroupRepository? _groups;
    private long? _activeGroupId;
    private bool _rebuildingTabs;

    // 每個頁簽各自記住的格數（＋最後停留的頁簽）。啟動時讀 ui.json，切換頁簽與改佈局時寫回。
    private MonitorLayoutPrefs _layoutPrefs = new();

    /// <summary>每次拖曳調整順序前的「頻道→格位」快照堆疊；「復原」後進先出地退回上一個排序。</summary>
    /// <remarks>
    /// 只存在記憶體、不跨重啟：復原的目的是拖錯一格後一鍵補救，不是版本控制。
    /// 頻道清單被新增／刪除／改名而重載時整疊清空，避免還原到已不存在的頻道對應。
    /// </remarks>
    private readonly Stack<List<(int Id, int? Cell)>> _cellOrderHistory = new();

    // 拖曳調整監看格順序用的狀態（左鍵按住某格後移動）。放開在別格＝搬移順序。
    private int _dragSourceCell = -1;
    private Point _dragStart;
    private bool _dragged;

    /// <summary>單分割時對攝影機畫面的數位放大倍率（滾輪縮放，1＝貼合格子）。</summary>
    /// <remarks>只有單格佈局支援縮放，故單一值即可；每次重建佈局歸零回 1.0。</remarks>
    private double _cellZoom = 1.0;

    /// <summary>單分割縮放時的畫面平移量（父座標系像素），與 <see cref="_cellZoom"/> 一起套用。</summary>
    private double _zoomPanX;
    private double _zoomPanY;

    /// <summary>單分割縮放／平移的套用層（重建時指向第 0 格）；只有它會被套上 RenderTransform。</summary>
    private FrameworkElement? _zoomLayer;

    // 放大後按住左鍵拖曳平移畫面的狀態。
    private bool _panning;
    private bool _panMoved;
    private Point _panOrigin;
    private Point _panStart;

    /// <summary>channelId → 名稱快取（<see cref="ChannelName"/> 每秒呼叫 32 次）。</summary>
    private Dictionary<int, string>? _channelNames;
    private string _footerBase = string.Empty;
    private int _preFullscreenLayout = 1;

    /// <summary>最近一次真正生效的佈局索引。佈局選單最底多一個「復原」項目，選到它時要先把
    /// 下拉還原成這個索引，才不會讓「復原」看起來像一種佈局。</summary>
    private int _lastLayoutIndex;

    /// <summary>為真時忽略 <see cref="OnLayoutChanged"/>：僅用於程式把下拉選取還原成
    /// <see cref="_lastLayoutIndex"/> 的那一次變更，避免重跑一次佈局套用。</summary>
    private bool _restoringLayoutSelection;

    /// <summary>主視窗是否為全螢幕（無邊框、蓋住工作列）。XAML 以 <c>WindowStyle="None"</c> ＋
    /// <c>WindowState="Maximized"</c> 直接以全螢幕啟動，故初始為 true。</summary>
    private bool _isWindowFullScreen = true;

    /// <summary>離開全螢幕時要還原的視窗樣式；預設回到一般可調整大小的單框視窗。</summary>
    private WindowStyle _restoreWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _restoreResizeMode = ResizeMode.CanResize;
    private WindowState _restoreWindowState = WindowState.Normal;

    /// <summary>正在把滿版狀態套回時為 true，避免 <see cref="OnStateChanged"/> 自我遞迴。</summary>
    private bool _restoringFullScreen;

    private RecordingScheduler? _scheduler;

    /// <summary>
    /// 桌面的錄影擁有者仲裁（M321，§11.1 進程分離）。桌面為較低優先序角色：Headless 服務在線時
    /// 搶下租約、桌面退為待命不錄影；服務缺席時桌面才錄。關閉時主動讓出，讓下次啟動或服務立即接手。
    /// </summary>
    private RecordingOwnershipCoordinator? _recordingOwnership;
    private DetectionWriter? _detWriter;
    private NotificationService? _notify;
    private TrayIconHost? _tray;
    private IoMonitorHost? _ioHost;
    private ShareHost? _shareHost;
    private WebHost? _webHost;
    private DispatcherTimer? _ntpTimer;
    private AnalyticsEventEngine? _analytics;
    private AlertRuleRepository? _alertRules;
    private AlarmEventRepository? _alarmEvents;
    private SmartAlertHost? _smartAlerts;

    private LicenseLimitNotifier? _licenseLimits;
    private OffsiteReplicationRepository? _offsite;
    private DispatcherTimer? _offsiteTimer;
    private DispatcherTimer? _reportMailTimer;
    private bool _exiting;

    /// <summary>操作員是否已按下「連線」（佈局切換只在已連線時才補建新格，避免自動連線未選頻道）。</summary>
    private bool _monitoring;
    private readonly Dictionary<string, Window> _children = new();

    private static readonly SolidColorBrush BrOffline = new(Color.FromRgb(0x6B, 0x7B, 0x90));
    private static readonly SolidColorBrush BrConnecting = new(Color.FromRgb(0xD8, 0xA1, 0x2C));
    private static readonly SolidColorBrush BrLive = new(Color.FromRgb(0x56, 0xC8, 0x86));
    private static readonly SolidColorBrush BrPerson = new(Color.FromRgb(0xFF, 0x63, 0x47));
    private static readonly SolidColorBrush BrVehicle = new(Color.FromRgb(0x00, 0xB6, 0xFF));
    private static readonly SolidColorBrush BrCellEdge = new(Color.FromRgb(0x1F, 0x3A, 0x5F));
    private static readonly SolidColorBrush BrHighlight = new(Color.FromRgb(0x4F, 0xC3, 0xF7));

    /// <summary>運動觸發時的格框色（與選取色區隔：選中格同時發生運動時仍以選取色為準）。</summary>
    private static readonly SolidColorBrush BrMotion = Frozen(Color.FromRgb(0xFF, 0xC1, 0x4D));

    /// <summary>錄影停滯的警示色（刻意用紅：畫面正常但沒有檔案，這是最需要被看見的一種失效）。</summary>
    private static readonly SolidColorBrush BrStall = Frozen(Color.FromRgb(0xEF, 0x53, 0x50));

    /// <summary>監看格底色與左下角頻道徽章底色（靜態共用，避免每次重建佈局各配置一次）。</summary>
    private static readonly SolidColorBrush BrCellBg = Frozen(Color.FromRgb(0x06, 0x09, 0x0F));

    private static readonly SolidColorBrush BrBadgeBg = Frozen(Color.FromArgb(0xC0, 0x03, 0x05, 0x08));

    // AI 疊加用填色／標籤底色。原本是「每個偵測框、每一幀」各 new 一個 SolidColorBrush，
    // 16 格 × 15fps × N 框就是每秒數千個一次性 Brush 落到 LOH。改成靜態共用並 Freeze：
    // Freeze 後 WPF 不再為它建立-change 通知鏈，跨執行緒使用也安全。
    private static readonly SolidColorBrush BrBoxFill = Frozen(Color.FromArgb(20, 255, 255, 255));
    private static readonly SolidColorBrush BrLabelBg = Frozen(Color.FromArgb(170, 8, 14, 22));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool _aiVisible;
    private readonly IReadOnlyList<Detection>[] _aiBoxes = new IReadOnlyList<Detection>[MaxCells];

    /// <summary>該格疊加的「世代」：_aiBoxes 內容或顯示設定變動時遞增。</summary>
    private readonly int[] _overlayGen = new int[MaxCells];

    /// <summary>
    /// 決定疊加是否需要重建視覺樹。獨立成可測試型別是因為這層一旦失效症狀很不明顯：
    /// 沒有錯誤、沒有例外，只是 CPU 上升、介面變卡。
    /// </summary>
    private readonly OverlayRenderGate _overlayGate = new(MaxCells);

    /// <summary>單格顯示時畫標籤；多格只畫框（16 格的標籤會糊成一片）。</summary>
    private static bool WithOverlayLabels(int layoutIndex) => layoutIndex == 0;

    private void BumpOverlayGeneration(int cell)
    {
        if (cell is >= 0 and < MaxCells)
        {
            _overlayGen[cell]++;
        }
    }
    private readonly List<string> _alerts = [];
    private readonly List<int?> _alertCells = [];
    private bool _expandInProgress;
    private System.Threading.Timer? _unackTimer;
    private System.Threading.Timer? _uiTimer;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS");
        _dataRoot = ResolveDataRoot();
        // 錄影根目錄一律避開系統碟（見 RecordingStorage）；明確設 HELIVMS_RECORDINGS_ROOT
        // 時照用，讓桌面與遠端服務指到同一個目錄。解析規則與 Agent、WebApi 收斂在同一處。
        _recordingsRoot = RecordingStorage.ResolveRootFromEnvironment();
        _tray = new TrayIconHost(ShowFromTray);
        _tray.ExitRequested += OnTrayExit;
        ApplyI18n();
    }

    /// <summary>
    /// 套用目前語系到主視窗所有靜態文字（M57 §14.7）。設定中心切換語言時會再呼叫一次
    /// （見 <see cref="RefreshTitle"/>）。XAML 刻意全部留空，否則畫面會停在建立視窗當下的語言；
    /// 畫面文字一律經此處指派，並由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        // 標題列
        Title = Localizer.T("Main.Title");
        AiToggle.Content = Localizer.T("Main.AiOverlay");
        AiToggle.ToolTip = Localizer.T("Main.AiOverlayTip");
        HeaderSettingsButton.Content = Localizer.T("Main.Settings");
        HeaderSettingsButton.ToolTip = Localizer.T("Main.SettingsTip");
        HeaderExitButton.Content = Localizer.T("Main.Exit");
        HeaderExitButton.ToolTip = Localizer.T("Main.ExitTip");
        UpdateFullScreenButton();

        // 授權到期浮條
        LicenseBannerText.Text = Localizer.T("Main.LicenseExpiring");
        LicenseBannerAction.Content = Localizer.T("Main.GoSettings");
        LicenseBannerAction.ToolTip = Localizer.T("Main.LicenseBannerTip");

        // 頂列：佈局
        LayoutHeader.Text = Localizer.T("Main.Layout");
        LayoutItemSingle.Content = Localizer.T("Main.LayoutSingle");
        LayoutItem4.Content = Localizer.T("Main.Layout4");
        LayoutItem6.Content = Localizer.T("Main.Layout6");
        LayoutItem9.Content = Localizer.T("Main.Layout9");
        LayoutItem12.Content = Localizer.T("Main.Layout12");
        LayoutItem16.Content = Localizer.T("Main.Layout16");
        LayoutItem20.Content = Localizer.T("Main.Layout20");
        LayoutItem25.Content = Localizer.T("Main.Layout25");
        LayoutItem30.Content = Localizer.T("Main.Layout30");
        LayoutItem36.Content = Localizer.T("Main.Layout36");
        LayoutItem42.Content = Localizer.T("Main.Layout42");
        LayoutItem56.Content = Localizer.T("Main.Layout56");
        LayoutItem64.Content = Localizer.T("Main.Layout64");
        LayoutItemUndo.Content = Localizer.T("Main.UndoCellOrder");
        LayoutItemUndo.ToolTip = Localizer.T("Main.UndoCellOrderTip");

        // 頂欄：常用監看入口（其餘功能已整合進設定中心）
        HeaderEventButton.Content = Localizer.T("Main.Events");
        HeaderEventButton.ToolTip = Localizer.T("Main.EventsTip");
        HeaderPlaybackButton.Content = Localizer.T("Main.Playback");
        HeaderPlaybackButton.ToolTip = Localizer.T("Main.PlaybackTip");
        MapButton.Content = Localizer.T("Main.Map");
        MapButton.ToolTip = Localizer.T("Main.MapTip");
        WallSequenceToggle.Content = Localizer.T("Main.WallSequence");
        WallSequenceToggle.ToolTip = Localizer.T("Main.WallSequenceTip");

        HintText.Text = Localizer.T("Main.EmptyHint");

        // 底欄
        NotifyBacklogBadge.ToolTip = Localizer.T("Main.NotifyBacklogTip");
        UnackBadge.ToolTip = Localizer.T("Main.UnackTip");
        if (string.IsNullOrEmpty(AlertText.Text))
        {
            AlertText.Text = Localizer.T("Main.AlertIdle");
        }

        if (string.IsNullOrEmpty(StatusText.Text))
        {
            StatusText.Text = Localizer.T("Main.StatusVerifying");
        }

        // 語言切換後「全部」頁簽標題要跟著換；_groups 尚未建立時（載入初期）交由
        // OnLoaded 之後的 ReloadGroupTabs 處理。
        ReloadGroupTabs();
    }

    /// <summary>
    /// M26／M251：關閉按鍵（含 Alt+F4、工作列關閉、頂欄「離開」）跳「最小化／離開」選擇。
    /// </summary>
    /// <remarks>
    /// 原本關閉鍵靜默收進系統匣：按了 X 的人以為程式結束了，其實還在背景錄影；而想真正結束又
    /// 只有系統匣選單一條路。改成一律先問。系統匣選單的「結束 HeliVMS」與命令列匯出等已明示的
    /// 路徑會先把 <c>_exiting</c> 設為 true，直接略過詢問。
    /// 選「離開」時不能在 <see cref="OnClosing"/> 內同步再呼叫 <see cref="Window.Close"/>（會與
    /// 這次正在取消的關閉重入），排到 Dispatcher 下一輪再關。
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        // E2E 探針模式（HELIVMS_TRAY_PROBE=1）維持「關閉即收進系統匣」，不跳對話框，
        // 否則自動化流程會卡在一個沒有人能按的模態視窗上，探針檔也永遠不會寫出來。
        if (Environment.GetEnvironmentVariable("HELIVMS_TRAY_PROBE") == "1")
        {
            Hide();
            WriteTrayProbeIfRequested();
            base.OnClosing(e);
            return;
        }

        switch (ExitChoiceWindow.Ask(this, _isWindowFullScreen))
        {
            case ExitChoice.Minimize:
                Hide();
                WriteTrayProbeIfRequested();
                break;
            case ExitChoice.Quit:
                _exiting = true;
                _tray?.Dispose();
                _tray = null;
                Dispatcher.BeginInvoke(Close);
                break;
            case ExitChoice.ExitFullScreen:
                SetWindowFullScreen(!_isWindowFullScreen);
                break;
            // Cancel不處理任何操作
        }

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }

        // 症狀：進設定中心操作完、用滑鼠按 X 關閉後，監控中心「自己縮小」成一般大小的視窗。
        // 主視窗是無邊框＋WindowState=Maximized，而它的一般大小是 XAML 的 1280×800；owned 子視窗
        // 關閉、主視窗被系統重新取得前景時，Windows 會依「還原尺寸」把它退回 Normal，於是看起來
        // 就是縮小。這裡在「目前應為滿版（_isWindowFullScreen 為真）」時立即套回最大化。
        // 使用者主動退出全螢幕（SetWindowFullScreen(false)）會先清掉 _isWindowFullScreen 再改狀態，
        // 故不會被此處攔回，也不會與 SetWindowFullScreen(true) 的中間 Normal 互相遞迴。
        if (_isWindowFullScreen && WindowState == WindowState.Normal && !_restoringFullScreen)
        {
            _restoringFullScreen = true;
            try
            {
                WindowState = WindowState.Maximized;
            }
            finally
            {
                _restoringFullScreen = false;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void ShowFromTray() => BringToFront();

    /// <summary>
    /// 把監控中心帶回前景（由系統匣還原，或子視窗關閉後）。
    /// </summary>
    /// <remarks>
    /// 主視窗是無邊框全螢幕（<see cref="WindowStyle.None"/>）。子視窗（設定中心等）以其為
    /// <see cref="Window.Owner"/> 開啟後，關閉時作業系統不一定會把焦點還給無邊框的擁有者，
    /// 操作員就會「設定關掉後找不到監控中心」。因此呼叫本方法：必要時 <see cref="Window.Show"/>
    /// 重新顯示（縮到最小時 <see cref="OnStateChanged"/> 會把視窗 Hide）、還原最小化、
    /// <see cref="Window.Activate"/> 取回前景。
    /// <para>
    /// 依使用者要求<b>不再設定 <c>Topmost</c></b>：先前置頂會讓監控中心一直蓋在設定中心等子視窗之上，
    /// 造成操作困擾；現在只在需要時把視窗帶到前景，不強制保持在上層。
    /// </para>
    /// </remarks>
    private void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            // 從系統匣還原時，若目前應為滿版就回到最大化，否則會縮成 XAML 的 1280×800。
            WindowState = _isWindowFullScreen ? WindowState.Maximized : _restoreWindowState;
        }

        Activate();
    }

    private void OnTrayExit()
    {
        if (!RequireAdmin())
        {
            return;
        }

        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    /// <summary>E2E 探針（僅 HELIVMS_TRAY_PROBE=1）：OnClosing 攔截成功且圖示可見時寫旗標檔。</summary>
    private void WriteTrayProbeIfRequested()
    {
        if (Environment.GetEnvironmentVariable("HELIVMS_TRAY_PROBE") != "1")
        {
            return;
        }

        if (_tray is null || !_tray.IsVisible)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(@"C:\HeliVMSData");
            File.WriteAllText(@"C:\HeliVMSData\tray-probe.ok", "1");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 小螢幕／高縮放時將視窗收斂至工作區內，避免超出而無法操作標題列（縮小／關閉）。
        var work = SystemParameters.WorkArea;
        if (MinWidth > work.Width)
        {
            MinWidth = Math.Floor(work.Width);
        }

        if (MinHeight > work.Height)
        {
            MinHeight = Math.Floor(work.Height);
        }

        if (Width > work.Width)
        {
            Width = work.Width;
        }

        if (Height > work.Height)
        {
            Height = work.Height;
        }

        if (Left < work.Left)
        {
            Left = work.Left;
        }

        if (Top < work.Top)
        {
            Top = work.Top;
        }

        if (Left + Width > work.Right)
        {
            Left = Math.Max(work.Left, work.Right - Width);
        }

        if (Top + Height > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - Height);
        }
    }

    /// <summary>由執行目錄 Assets 資料夾載入品牌圖檔。</summary>
    public static BitmapSource CreateBitmap(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        return BitmapFrame.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
    }

    /// <summary>
    /// 資料根目錄：優先 C:\HeliVMSData（§2.1）；不可寫時降級至本機資料夾。
    /// 解析規則的單一來源在 <see cref="DataRootLayout"/>，桌面與 Web 服務共用，
    /// 否則兩邊會各自漂移到不同的資料庫去，排程與保留清理就對不起來。
    /// </summary>
    internal static string ResolveDataRoot() => DataRootLayout.ResolveRoot();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLayoutPreference();
        ApplyRoleRestrictions();
        var icon = CreateBitmap("app.ico");
        Icon = icon;
        HeaderLogo.Source = icon;
        MigrateLegacyData();

        _store = new SqliteStore(Path.Combine(_dataRoot, "index.db"));
        _store.Initialize();

        // 啟動時重新驗證授權（M213，§19.4）：license 列的 features／max_cameras 是閘門唯一
        // 的判斷依據，若只在匯入時寫一次，使用者手改資料庫就能無痛放大授權。這裡以授權檔與
        // 資料庫列兩者中「可驗證且較新」的一張重新套用，兩者都驗不過就把列標記失效。
        _licenseService = new LicenseService(_store);
        _licenseService.RefreshDefault("startup", DateTime.UtcNow);

        _licenseGate = new LicenseUiGate(_licenseService);
        ApplyLicenseVisibility();
        RefreshLicenseFooter();

        var settings = new SettingsRepository(_store);
        Localizer.Init(settings);
        _showCellStatus = settings.Get("monitor.show_status") == "1";

        // 格子在 ApplyLayoutPreference 內就已先建好（那時還讀不到設定），把勾選狀態補正，
        // 否則上次開著的人這次開起來選單會顯示成未勾選，與實際顯示不一致。
        foreach (var item in _cellStatusMenuItems)
        {
            item.IsChecked = _showCellStatus;
        }

        ApplyI18n();
        _channels = new ChannelRepository(_store);
        _groups = new ChannelGroupRepository(_store);
        _segRepo = new SegmentRepository(_store);
        _channels.EnsureSeedChannels();
        ReloadGroupTabs();

        // 上次停留的群組若已被刪除，ReloadGroupTabs 會退回「全部」；格數也要跟著換回「全部」頁簽
        // 記住的值，否則監看牆會沿用已刪除群組的格數（重開也一樣，因為偏好檔還記著那個群組）。
        if (MonitorLayoutPrefs.TabKey(_activeGroupId) != _layoutPrefs.ActiveTab)
        {
            var restoredIndex = LayoutIndexForCells(TabCellCount(_activeGroupId));
            if (LayoutCombo.SelectedIndex != restoredIndex)
            {
                LayoutCombo.SelectedIndex = restoredIndex;
            }
            else
            {
                RebuildCells();
            }
        }

        // 錄影根目錄以「設定中心→儲存」選定的磁碟為準（存在共用資料庫，桌面／WebApi／Agent 同讀）；
        // 未設定時才退回環境變數與推導值。在建立 _manager／_scheduler 之前解析，讓首次錄影就用對的磁碟。
        _recordingsRoot = RecordingStorage.ResolveConfiguredRoot(
            _dataRoot,
            settings.Get(RecordingStorage.RootSettingKey));

        if (Environment.GetCommandLineArgs().Contains("--playback", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenChild(new PlaybackWindow(_store)));
        }

        // 登入自啟（--tray，見 AutoStartService）：收進系統匣，不跳監控視窗。
        // 排 ApplicationIdle 是為了排在 OnLoaded 其餘初始化（授權、資料庫、頻道）
        // 之後才隱藏，否則使用者會看到視窗閃一下再消失，像當掉了一樣。
        // 自啟的目的本來就是無人值守錄影，登入時跳一個監控視窗等於宣告
        // 「這台機器剛開機就有人在用」。
        if (Environment.GetCommandLineArgs().Contains(AutoStartService.TrayArgument, StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(Hide, DispatcherPriority.ApplicationIdle);
        }

        if (Environment.GetCommandLineArgs().Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenSettingsWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--export", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenExportWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--exportcenter", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenExportCenterWindow());
        }

        var expIdx = -1;
        for (var i = 1; i < Environment.GetCommandLineArgs().Length - 1; i++)
        {
            if (Environment.GetCommandLineArgs()[i].Equals("--events-export", StringComparison.OrdinalIgnoreCase))
            {
                expIdx = i + 1;
                break;
            }
        }
        if (expIdx > 0)
        {
            var expPath = Environment.GetCommandLineArgs()[expIdx];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ExportEventsCsv(expPath);
                _exiting = true;
                Close();
            }));
        }

        if (Environment.GetCommandLineArgs().Contains("--redaction", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenRedactionWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--alarmmanager", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenAlarmManagerWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--dewarp", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenDewarpWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--share", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenShareWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--analytics", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenAnalyticsWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--reports", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenReportsWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--rules", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenRulesWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--synopsis", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenSynopsisWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--hold", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenLegalHoldWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--patrol", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenPatrolWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--io", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenIoWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--stream", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenStreamSwitchWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--audio", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenAudioWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--failover", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenFailoverWindow());
        }

        var evArgs = Environment.GetCommandLineArgs();
        var evidenceIndex = Array.IndexOf(evArgs, "--evidence");
        if (evidenceIndex >= 0)
        {
            var evidenceDir = evidenceIndex + 1 < evArgs.Length ? evArgs[evidenceIndex + 1] : null;
            Dispatcher.BeginInvoke(() => OpenEvidenceWindow(evidenceDir));
        }

        if (Environment.GetCommandLineArgs().Contains("--map", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenMapWindow());
        }

        if (Environment.GetCommandLineArgs().Contains("--events", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => OpenEventCenter());
        }

        _detWriter = new DetectionWriter(_store);
        _notify = new NotificationService(_store, ruleRepo: new AlertRuleRepository(_store));

        _manager = CreateManager();

        _ioHost = new IoMonitorHost(_store);
        _ioHost.RefreshAndStart();
        _ioHost.EventInserted += (_, record) => RouteAlarm(record);

        _shareHost = new ShareHost(_store);
        _shareHost.ApplySettings(
            new SettingsRepository(_store),
            licenseAllowed: _licenseGate?.Allows(LicenseFeatures.Remote) == true);

        // M305：內嵌 Web 服務。設定了 web.enabled=1 就在啟動時拉起，交由設定中心「網路」頁控制。
        _webHost = new WebHost(_dataRoot, Path.Combine(_dataRoot, "index.db"));
        _webHost.ApplySettings(new SettingsRepository(_store));

        // M305：NTP 對時。啟用時於啟動先對一次，之後定期對時；校正系統時鐘為盡力而為。
        StartNtpSync();

        _analytics = new AnalyticsEventEngine(_store);
        _alertRules = new AlertRuleRepository(_store);
        _alarmEvents = new AlarmEventRepository(_store);
        _licenseLimits = new LicenseLimitNotifier(_store);
        _offsite = new OffsiteReplicationRepository(_store);
        _offsiteTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => RunOffsiteDueJobs(), Dispatcher);
        _offsiteTimer.Start();
        RunOffsiteDueJobs();

        // 排程報表寄送（§14.1 #9）：每 5 分鐘檢查一次，實際是否寄出交由 ReportSchedule.IsDue。
        _reportMailTimer = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background,
            (_, _) => RunReportMailDue(), Dispatcher);
        _reportMailTimer.Start();
        RunReportMailDue();
        _analytics.LoadZones();

        _wallSequenceTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => AdvanceWallSequence(), Dispatcher);
        if (_wallSequenceEnabled)
        {
            _wallSequenceTimer.Start();
        }
        // M54 智慧警報聚合：命中聚合規則的原始事件不再逐一外送，只有「首次跨過門檻」
        // 的那一筆會產生通知。舊的 ShouldSuppressSmartAlert 是逐筆比較窗內計數，
        // 門檻一旦跨過就再也不抑制，等於 8 次的門檻在跨過之後每一筆都照送——
        // 正是這條規則要擋掉的洪泛。
        //
        // 注意 Aggregated 與 Triggers 是兩件事：Aggregated 決定這筆要不要外送，
        // Triggers 決定要外送什麼。早期版本只看「有沒有 trigger」而 return，
        // 連沒有規則命中的事件也一起吞掉，現場症狀是設定聚合規則後所有通知都消失。
        _smartAlerts = new SmartAlertHost(_store);
        _analytics.EventInserted += (_, record) => RouteAlarm(record);

        _scheduler = CreateScheduler();

        // M70/M72：巡航排程真正有人執行。PatrolWindow 只負責編輯與儲存，
        // 沒有這個 host 的話勾「啟用」等於只寫進資料庫，攝影機永遠不會動。
        _patrolHost = new PatrolHostService(_store);
        _patrolHost.Activity += (_, msg) => Dispatcher.BeginInvoke(new Action(() => HintText.Text = msg));
        _patrolHost.PatrolFailed += OnPatrolFailed;
        _patrolHost.Start();

        _bgCts = new CancellationTokenSource();
        _ = RunRetentionLoopAsync(_bgCts.Token);

        RefreshChannelList();

        // 全自動啟動：連線全部已設定頻道，並讓排程器立刻開始全時錄影（見 AutoStartMonitoringAsync）。
        _ = AutoStartMonitoringAsync();

        // 通知外送的 Activity 訂閱：外送失敗／靜默跳過／批次送達都會經過這裡。
        // 沒有訂閱時這些資訊只存在於 log 檔，操作員在畫面上看不到通知到底送沒送出去。
        _notify.Activity += (_, msg) => Dispatcher.BeginInvoke(new Action(() =>
        {
            HintText.Text = msg;
            RefreshNotifyBacklogBadge();
        }));

        // 未確認事件計數：即時一筆，之後每 15 秒（規格 §1.4）
        RefreshUnackBadge();
        RefreshNotifyBacklogBadge();
        UpdateClock();
        _unackTimer = new System.Threading.Timer(
            _ => Dispatcher.InvokeAsync(RefreshUnackBadge),
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));

        // 通知待送徽章：每 10 秒。通知送出失敗時不會經過 Activity（那只有偶發訊息），
        // 但 PendingCount 會停在高位不降，必須定期取樣才看得出「塞住了」。
        _notifyBacklogTimer = new System.Threading.Timer(
            _ => RefreshNotifyBacklogBadge(),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10));

        // 每格徽章、AI 策略與狀態文字：每秒更新（M12）
        _uiTimer = new System.Threading.Timer(
            _ => Dispatcher.InvokeAsync(UpdateCellStatus),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>將 M1 舊資料（%LOCALAPPDATA%\HeliVMS）遷移至 C:\HeliVMSData（若尚未存在）。</summary>
    private void MigrateLegacyData()
    {
        if (!Directory.Exists(_legacyDir))
        {
            return;
        }

        Directory.CreateDirectory(_dataRoot);
        var targetDb = Path.Combine(_dataRoot, "index.db");
        if (!File.Exists(targetDb))
        {
            var legacyDb = Path.Combine(_legacyDir, "helivms.db");
            if (File.Exists(legacyDb))
            {
                File.Copy(legacyDb, targetDb);
            }
        }

        var legacyRec = Path.Combine(_legacyDir, "recordings");
        var targetRec = _recordingsRoot;
        if (Directory.Exists(legacyRec) && !Directory.Exists(targetRec))
        {
            Directory.Move(legacyRec, targetRec);
        }
    }

    private void RefreshChannelList()
    {
        // 依「顯示格位」重排：連線時的第 N 路會顯示在第 N 格（ConnectAsync 依序派格），
        // 所以被指定第 N 格的頻道排到索引 N 即會顯示在第 N 格；未指定的依建立順序補空位。
        _channelList = ChannelLayout.ReorderByCell(_channels!.List(), c => c.DisplayCell);

        // 只把從沒命名過的匯入頻道補上格位預設名；既有名稱一律不動，拖曳調順序時名稱才會跟著攝影機搬。
        // 有改名才需要重新讀取清單。
        if (NormalizeDefaultChannelNames())
        {
            _channelList = ChannelLayout.ReorderByCell(_channels.List(), c => c.DisplayCell);
        }

        _channelNames = null;
    }

    // ---- 監看群組（監控中心頁簽）------------------------------------------------
    // 「全部」是虛擬頁簽（_activeGroupId = null），永遠顯示所有頻道；其餘頁簽各對應一個群組，
    // 只顯示該群組的成員（依群組內順序）。切換頁簽只改派既有工作階段的格位
    // （ChannelManager.ReconcileAsync），不拆除不在畫面上的頻道，錄影因此不會中斷。

    /// <summary>目前頁簽要顯示的頻道；「全部」為整份清單，群組則為其成員（依群組順序）。</summary>
    private IReadOnlyList<ChannelInfo> WallChannels()
    {
        if (_activeGroupId is not long groupId || _groups is null)
        {
            return _channelList;
        }

        var byId = _channelList.ToDictionary(c => c.Id);
        return _groups.ListChannelIds(groupId)
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();
    }

    /// <summary>重建監控中心上方的群組頁簽，並依 <see cref="_activeGroupId"/> 還原選取。</summary>
    /// <remarks>
    /// 群組被刪除而目前頁簽指向它時，退回「全部」頁簽，否則 <see cref="WallChannels"/> 會
    /// 永遠回傳空清單（監看牆空白卻沒有任何錯誤）。
    /// </remarks>
    private void ReloadGroupTabs()
    {
        if (_groups is null || GroupTabs is null)
        {
            return;
        }

        _rebuildingTabs = true;
        try
        {
            GroupTabs.Items.Clear();
            var all = new TabItem { Header = Localizer.T("Main.GroupAll"), Tag = (long?)null };
            GroupTabs.Items.Add(all);
            foreach (var group in _groups.List())
            {
                GroupTabs.Items.Add(new TabItem { Header = group.Name, Tag = (long?)group.Id });
            }

            var target = GroupTabs.Items.OfType<TabItem>().FirstOrDefault(t => (long?)t.Tag == _activeGroupId) ?? all;
            GroupTabs.SelectedItem = target;
            _activeGroupId = target.Tag as long?;
        }
        finally
        {
            _rebuildingTabs = false;
        }
    }

    private async void OnGroupTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // ReloadGroupTabs 重建頁簽時會觸發本事件；那時 _activeGroupId 已直接設定，不需再切。
        if (_rebuildingTabs || GroupTabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        var groupId = tab.Tag as long?;
        if (groupId == _activeGroupId)
        {
            return;
        }

        // 離開目前頁簽前先記住它的格數，切過去才知道要還原多少格。
        _layoutPrefs.Remember(_activeGroupId, CurrentCellCount());
        _activeGroupId = groupId;

        var targetIndex = LayoutIndexForCells(TabCellCount(groupId));
        if (LayoutCombo.SelectedIndex != targetIndex)
        {
            // 改佈局會觸發 OnLayoutChanged：重建格位、依新頁簽改派工作階段，並保存格數。
            LayoutCombo.SelectedIndex = targetIndex;
        }
        else
        {
            // 格數不變（下拉不會變更）：仍需重新改派並保存「最後停留頁簽」。
            SaveLayoutPreference();
            await ApplyWallAsync();
        }
    }

    /// <summary>依目前頁簽改派既有工作階段到格位；不重連、不中斷錄影。</summary>
    private async Task ApplyWallAsync()
    {
        var wall = WallChannels();

        if (_manager is { } mgr && _monitoring && wall.Count > 0)
        {
            await _reconcileGate.WaitAsync();
            try
            {
                await mgr.ReconcileAsync(wall, 0, CurrentCellCount());
            }
            finally
            {
                _reconcileGate.Release();
            }
        }

        ClearCellsBeyondChannels(wall.Count);
        UpdateBadges();
        UpdateFooter();
    }

    /// <summary>
    /// 把「仍沿用裝置名稱」的頻道補成格位預設名（第一格攝影機01、第二格攝影機02…）並寫回資料庫。
    /// </summary>
    /// <remarks>
    /// 症狀：ONVIF 精靈等來源帶進來的是裝置名稱，監看牆上滿是「IPC-1234」之類的名字，操作員認不出機位。
    /// 只補「從未命名過、名稱仍等於裝置名稱」的頻道，補過一次之後名稱就屬於這台攝影機、不再自動變動；
    /// 既有的「攝影機NN」與操作員自取的名稱一律保留，拖曳調順序時名稱才能跟著攝影機搬（見 M266）。
    /// </remarks>
    private bool NormalizeDefaultChannelNames()
    {
        if (_channels is null || _channelList.Count == 0)
        {
            return false;
        }

        var format = Localizer.T("Channels.DefaultName");
        var deviceNames = new DeviceRepository(_store!, new AuditLogRepository(_store!))
            .List()
            .ToDictionary(d => d.Id, d => d.Name);

        var plan = ChannelNaming.ReassignDefaultNames(
            _channelList.Select(c => (c.Name, IsDefault: IsDefaultChannelName(c, deviceNames))).ToList(),
            format);

        var changed = false;
        for (var i = 0; i < _channelList.Count; i++)
        {
            if (plan[i] is string renamed)
            {
                RenameChannel(_channelList[i], renamed);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 是否為「沿用的裝置名稱」：只有從未命名過、名稱仍等於其綁定裝置名稱的頻道才會被自動命名。
    /// </summary>
    /// <remarks>
    /// 刻意不再把既有的「攝影機NN」視為可自動命名：名稱屬於攝影機，拖曳調順序時名稱要跟著攝影機搬，
    /// 若每次載入都依格位重新編號，操作員排好的順序下次開啟就被改回去（見 M266）。
    /// </remarks>
    private static bool IsDefaultChannelName(ChannelInfo channel, IReadOnlyDictionary<int, string> deviceNames)
    {
        return channel.DeviceId is int deviceId
            && deviceNames.TryGetValue(deviceId, out var deviceName)
            && ChannelNaming.CarriesDeviceName(channel.Name, deviceName);
    }

    /// <summary>只改名稱，其餘欄位原值帶回（<see cref="ChannelRepository.Update"/> 覆寫整列）。</summary>
    private void RenameChannel(ChannelInfo channel, string name)
    {
        _channels!.Update(new ChannelInfo
        {
            Id = channel.Id,
            DeviceId = channel.DeviceId,
            Name = name,
            MainStreamUrl = channel.MainStreamUrl,
            SubStreamUrl = channel.SubStreamUrl,
            Codec = channel.Codec,
            AudioEnabled = channel.AudioEnabled,
            AudioEncoder = channel.AudioEncoder,
            MotionEnabled = channel.MotionEnabled,
            MotionSensitivity = channel.MotionSensitivity,
            RecordingMode = channel.RecordingMode,
            Enabled = channel.Enabled,
            DisplayCell = channel.DisplayCell,
        });
    }

    /// <summary>只改指定格位，其餘欄位原值帶回（<see cref="ChannelRepository.Update"/> 覆寫整列）。</summary>
    private void UpdateChannelDisplayCell(ChannelInfo channel, int? displayCell)
    {
        _channels!.Update(new ChannelInfo
        {
            Id = channel.Id,
            DeviceId = channel.DeviceId,
            Name = channel.Name,
            MainStreamUrl = channel.MainStreamUrl,
            SubStreamUrl = channel.SubStreamUrl,
            Codec = channel.Codec,
            AudioEnabled = channel.AudioEnabled,
            AudioEncoder = channel.AudioEncoder,
            MotionEnabled = channel.MotionEnabled,
            MotionSensitivity = channel.MotionSensitivity,
            RecordingMode = channel.RecordingMode,
            Enabled = channel.Enabled,
            DisplayCell = displayCell,
        });
    }

    /// <summary>頻道清單的內容簽章（id／名稱／碼流位址／啟用／指定格／錄影模式）。</summary>
    private static string ChannelSignature(IReadOnlyList<ChannelInfo> channels)
        => string.Join('\n', channels.Select(c =>
            $"{c.Id}|{c.Name}|{c.MainStreamUrl}|{c.SubStreamUrl}|{c.Enabled}|{c.DisplayCell}|{c.RecordingMode}"));

    /// <summary>
    /// 子視窗（設定中心→頻道／攝影機管理）關閉後重載頻道清單；清單有新增、刪除或修改時
    /// 重新連線並讓排程器接手，讓新頻道立即出現在監看區，不必重啟程式。
    /// </summary>
    /// <remarks>
    /// 頻道是在子視窗內建立的，而子視窗關閉時主視窗過去只更新了清單（<see cref="RefreshChannelList"/>）、
    /// 沒有重新連線，新頻道會一直停在「未連線」，操作員必須關掉整個程式再開才看得到。這裡比對清單簽章，
    /// 只有真的變動才重新連線：<see cref="ChannelManager.ConnectAsync"/> 會先拆除舊工作階段再重建，
    /// 若對每次關閉（例如事件中心）都做，會讓所有攝影機錄影短暫中斷，故必須先比對。
    /// </remarks>
    private async Task ReloadChannelsIfChangedAsync()
    {
        if (_channels is null || _manager is null)
        {
            return;
        }

        // 群組可能在頻道管理視窗內被新增／改名／刪除；先重建頁簽（刪掉目前群組會退回「全部」）。
        var tabBefore = MonitorLayoutPrefs.TabKey(_activeGroupId);
        ReloadGroupTabs();

        var before = ChannelSignature(_channelList);
        RefreshChannelList();

        // 目前的群組被刪除而退回「全部」時，格數要換回「全部」頁簽記住的，否則監看牆會沿用
        // 已刪群組的格數（重開也一樣）。改下拉＝走 OnLayoutChanged，重建格位並保存偏好。
        if (MonitorLayoutPrefs.TabKey(_activeGroupId) != tabBefore)
        {
            var tabIndex = LayoutIndexForCells(TabCellCount(_activeGroupId));
            if (LayoutCombo.SelectedIndex != tabIndex)
            {
                LayoutCombo.SelectedIndex = tabIndex;
            }
        }

        if (ChannelSignature(_channelList) == before)
        {
            // 頻道本身沒變，但群組成員歸屬可能改了；目前若在某個群組頁簽就重套用一次。
            if (_activeGroupId is not null)
            {
                await ApplyWallAsync();
            }

            return;
        }

        // 頻道集合已改變（新增／刪除／改名）：快照可能指向已不存在的頻道，整疊清空。
        _cellOrderHistory.Clear();
        UpdateUndoCellOrderButton();

        // 空清單（頻道被刪光）時 AutoStartMonitoringAsync 會直接返回、不拆既有工作階段，
        // 故這裡明確斷線，否則已刪除的頻道仍會繼續串流與錄影。
        if (_channelList.Count == 0)
        {
            await _manager.DisconnectAllAsync();
            _monitoring = false;
            RecBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 重用開機路徑：重建工作階段、套用顯示碼流政策，並讓排程器立即接手新頻道的錄影。
            await AutoStartMonitoringAsync();
        }

        // 重新連線只會重畫還存在的格位；被刪除的頻道其最後一張影格仍留在該格 Image.Source 上，
        // 必須明確清掉，否則監控中心會一直殘留已刪除攝影機的畫面。
        ClearCellsBeyondChannels();

        // ConnectAsync 把所有頻道都派到格位；若目前在某個群組頁簽，需再依該群組重派一次。
        if (_activeGroupId is not null)
        {
            await ApplyWallAsync();
        }

        UpdateFooter();
        HintText.Text = Localizer.T("Main.ChannelsReloaded");
    }

    /// <summary>清掉「目前沒有頻道」的格位（索引 ≥ 頻道數）的殘影，還原成離線狀態。</summary>
    /// <remarks>
    /// 頻道被刪光時這裡等同於把全部格子清空。之所以不能只靠 <see cref="AutoStartMonitoringAsync"/>：
    /// 重新連線只對還存在的頻道重建工作階段，多出來（或已被刪除）的格子不會被重畫，
    /// 最後一張影格就凍在畫面上，看起來像攝影機還在。
    /// </remarks>
    private void ClearCellsBeyondChannels(int? wallCount = null)
    {
        // 預設以「全部頻道」為界（重新載入路徑）；切到群組頁簽時改以該群組的成員數為界，
        // 否則群組看不到的頻道其最後一張影格會留在格子上。
        var bound = wallCount ?? _channelList.Count;
        for (var i = bound; i < MaxCells; i++)
        {
            if (_cellImages[i] is null)
            {
                continue;
            }

            _bitmap[i] = null;
            _cellChannel[i] = null;
            _cellStates[i] = RtspState.Stopped;
            _cellResolution[i] = null;
            _cellMeters[i].Clear();
            // 走統一投影途徑：空格顯示 Disable 佔位圖且不留「未連線」文字。
            ApplyCellState(i, RtspState.Stopped);
            CellOverlay(i)?.Children.Clear();
        }
    }

    /// <summary>
    /// 開機全自動啟動：連線全部已設定頻道（顯示格之外的頻道僅背景串流），
    /// 並立即讓排程器對全時錄影頻道啟動錄影，不必等 30 秒的第一次輪詢。
    /// </summary>
    /// <remarks>
    /// 頻道清單與手動連線／錄影按鈕已自監控中心移除，改由本方法在載入後自動執行；
    /// 授權與排程閘門仍在 <see cref="RecordingScheduler"/> 內生效，這裡不繞過。
    /// </remarks>
    private async Task AutoStartMonitoringAsync()
    {
        if (_manager is null || _channelList.Count == 0)
        {
            return;
        }

        try
        {
            // 連線全部頻道；cell 索引超過顯示格數的頻道仍會串流（僅不顯示），其影像事件由 UI 端防護界線丟棄。
            await _manager.ConnectAsync(_channelList, 0, _channelList.Count);
            _monitoring = _manager.HasActiveSessions;
            UpdateAiPolicy();
            UpdateFooter();

            if (_scheduler is not null)
            {
                await _scheduler.ReconcileAsync();
            }
        }
        catch (Exception)
        {
            // 自動啟動失敗不應讓主視窗崩潰；下一次（排程／重新載入）仍會再嘗試。
        }
    }

    /// <summary>
    /// 建立頻道管理器並接好事件。抽成方法讓設定中心切換錄影磁碟時能重建一個指向新根目錄的
    /// 管理器（舊工作階段在 <see cref="OnRecordingsRootChangedAsync"/> 內先拆除）。
    /// </summary>
    private ChannelManager CreateManager()
    {
        var manager = new ChannelManager(_store!, _recordingsRoot, Path.Combine(_dataRoot, "snapshots"), DetectionModelResolver.TryResolve());
        manager.FrameArrived += (_, e) => OnCellFrame(e.Cell, e.Frame);
        manager.AiDetections += OnManagerAiDetections;
        manager.MotionSignal += (_, e) => OnMotionSignal(e.Cell, e.Ratio);
        manager.CellMappingChanged += (_, e) => OnCellMappingChanged(e.Cell, e.ChannelId);
        manager.StateChanged += (_, e) => OnCellState(e.Cell, e.Channel.Id, e.State);
        manager.HealthRestart += (_, e) =>
            // 來自健康檢查 timer 執行緒，不能直接寫 UI。
            Dispatcher.BeginInvoke(
                new Action(() => HintText.Text = string.Format(Localizer.T("Main.LiveTimeoutReconnect"), e.Channel.Name)),
                DispatcherPriority.Background);
        manager.AlarmEvent += (_, e) => RouteAlarm(e.Record);
        return manager;
    }

    /// <summary>
    /// 建立錄影排程器。isCellRecording 讀 <c>_manager</c> 欄位（而非捕捉當下實例），
    /// 切換磁碟重建管理器後排程器仍會問到最新的管理器。
    /// </summary>
    private RecordingScheduler CreateScheduler()
    {
        // 錄影擁有者仲裁（M321，§11.1 進程分離）：桌面為較低優先序角色，Headless 服務在線時退為待命。
        // 每輪調和續約；未持有者不開新錄影，且會收掉既有錄影機，把錄影交還服務，避免重複錄影。
        _recordingOwnership = RecordingOwnership.CreateDesktop(_store!);
        var scheduler = new RecordingScheduler(
            _store!,
            _recordingsRoot,
            isCellRecording: ch => _manager!.IsRecording(ch),
            // 磁碟滿策略第三階段（§11.1）：可用空間低於寫入門檻時停開新錄影。以目前錄影根目錄
            // 即時判斷（每次調和重算），切換磁碟後下一個週期即依新根判斷。
            diskFull: () => DiskPressureCoordinator.ShouldStopNewRecording(_store!, _recordingsRoot),
            // 錄影心跳寫進資料根目錄（M320）；與 Headless Agent 的 WatchdogWorker 共用同一份。
            heartbeatRoot: ResolveDataRoot(),
            ownsRecording: () => _recordingOwnership!.AcquireOrRenew(DateTime.UtcNow));
        // 排程/巡邏心跳來自背景執行緒，HintText 必須回 UI 執行緒改。
        scheduler.Activity += (_, msg) => Dispatcher.BeginInvoke(new Action(() => HintText.Text = msg));
        scheduler.RecordingStalled += OnRecordingStalled;
        scheduler.RecordingResumed += OnRecordingResumed;
        return scheduler;
    }

    /// <summary>
    /// 設定中心切換錄影磁碟後立即套用（不重啟）：拆除舊排程器與管理器、以新根目錄重建並重新連線。
    /// 舊錄影以絕對路徑留在原磁碟仍可回放；配額清理由主清理迴圈與設定中心的已知根清單接手。
    /// </summary>
    private async Task OnRecordingsRootChangedAsync(string newRoot)
    {
        _recordingsRoot = newRoot;

        var oldScheduler = _scheduler;
        _scheduler = null;
        if (oldScheduler is not null)
        {
            await oldScheduler.StopAllAsync();
            oldScheduler.Dispose();
        }

        await DisconnectAllAsync();
        _manager?.Dispose();
        _manager = CreateManager();
        _scheduler = CreateScheduler();

        await AutoStartMonitoringAsync();
        HintText.Text = Localizer.T("Main.RecordingsRootChanged");
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";

    /// <summary>每小時執行一次配額清理與 tmp 隔離、快照過期清理（設定每圈重讀）。</summary>
    private async Task RunRetentionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 每圈重讀目前錄影根目錄與曾用過的舊根：設定中心切換磁碟後，清理才會改認新根，
                // 且舊根上的既有錄影仍可汰除（見 RetentionService 建構子）。
                var settings = new SettingsRepository(_store!);
                var service = new RetentionService(
                    _segRepo!,
                    _recordingsRoot,
                    new LegalHoldRepository(_store!),
                    RecordingStorage.ParseKnownRoots(settings.Get(RecordingStorage.KnownRootsSettingKey)));
                RunScheduledBackup();

                // 配額汰除 ＋ 暫存隔離 ＋ 快照汰除 ＋ 中繼資料保管期限，串成同一次執行（與 Headless
                // Agent 的 RetentionWorker 共用）。這些表過去從不隨時間汰除；桌面內嵌的 Web 主機
                // 又不掛背景保留服務（見 WebHost），所以桌面部署下它們只會一直長大。
                var alarmDays = (int)settings.GetDoubleOrDefault(MetadataRetentionPruner.AlarmDaysKey, MetadataRetentionPruner.DefaultDays);
                var auditDays = (int)settings.GetDoubleOrDefault(MetadataRetentionPruner.AuditDaysKey, MetadataRetentionPruner.DefaultDays);
                var sweep = RetentionCoordinator.Run(
                    _store!,
                    service,
                    Path.Combine(_dataRoot, "snapshots"),
                    ReadQuotaBytes(),
                    ReadSnapshotDays(),
                    alarmDays,
                    auditDays,
                    DateTime.UtcNow);

                var hintParts = new List<string>();
                if (sweep.DeletedSegments > 0)
                {
                    hintParts.Add(string.Format(Localizer.T("Main.CleanupRemovedSegments"), sweep.DeletedSegments, FormatBytes(sweep.FreedBytes)));
                }

                if (sweep.PurgedTmp > 0)
                {
                    hintParts.Add(string.Format(Localizer.T("Main.CleanupPurgedTmp"), sweep.PurgedTmp));
                }

                if (sweep.SnapshotDeletedFiles > 0)
                {
                    hintParts.Add(string.Format(Localizer.T("Main.CleanupPurgedSnapshots"), sweep.SnapshotDeletedFiles, FormatBytes(sweep.SnapshotFreedBytes)));
                }

                if (hintParts.Count > 0)
                {
                    HintText.Text = string.Format(Localizer.T("Main.QuotaCleanup"), string.Join("、", hintParts));
                }

                // 錄影完整性自我檢查（§11.1 寫盤保護）：與 Headless Agent 走同一入口，每圈抽查少數
                // 已封檔區段，把損壞／遺失寫進稽核。只記錄、不改狀態也不刪檔（見 MediaIntegrityChecker）：
                // 改了 status 會讓壞段退出用量統計與汰除候選，反而變成刪不掉的孤兒。
                try
                {
                    MediaIntegrityCoordinator.RunConfigured(_store!, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    // 檢查失敗（ffprobe 暫時不可用、庫被鎖）不影響監看主線，下一圈再試。
                }

                // 索引對帳：把「索引說有、磁碟上沒有」的段標成 missing。
                // 完整性檢查刻意不改狀態（見 MediaIntegrityChecker），所以外部刪檔或磁碟換掉之後，
                // 回放清單、日曆與用量統計還會照著索引顯示，點下去才失敗——這裡才是修正索引的地方。
                // 與 Headless Agent 共用入口，兩個部署模式的行為才會一致。
                try
                {
                    SegmentReconcileCoordinator.RunConfigured(_store!, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    // 單輪對帳失敗（磁碟忙碌、庫被鎖）不影響監看主線，下一圈再試。
                }

                // 磁碟滿策略第一階段（§11.1）：>85% 預警、低於寫入門檻告警。等級改變才寫稽核
                //（與 Headless Agent 共用入口），並在提示列讓操作員當下看到。
                try
                {
                    var level = DiskPressureCoordinator.RunConfigured(_store!, _recordingsRoot, DateTime.UtcNow);
                    if (level == DiskPressureLevel.Critical)
                    {
                        HintText.Text = Localizer.T("Main.DiskCriticalSpace");
                    }
                    else if (level == DiskPressureLevel.Warning)
                    {
                        HintText.Text = Localizer.T("Main.DiskLowSpace");
                    }
                }
                catch (Exception)
                {
                    // 讀不到磁碟狀態不影響監看主線，下一圈再試。
                }
            }
            catch (Exception)
            {
                // 清理失敗不影響監看主線
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>排程備份（§14.4）：讀 backup.enabled/target/interval_hours，距 backup.last_run 達間隔時背景執行；無目標設定跳過。</summary>
    private void RunScheduledBackup()
    {
        if (_store is null)
        {
            return;
        }

        var settings = new SettingsRepository(_store);
        if (settings.Get("backup.enabled") != "1")
        {
            return;
        }

        var target = settings.Get("backup.target");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var hours = settings.GetDoubleOrDefault("backup.interval_hours", 24);
        if (hours <= 0)
        {
            hours = 24;
        }

        var lastRaw = settings.Get("backup.last_run");
        var last = string.IsNullOrWhiteSpace(lastRaw)
            ? DateTime.MinValue
            : SqliteStore.FromIso(lastRaw);
        if (DateTime.UtcNow - last < TimeSpan.FromHours(hours))
        {
            return;
        }

        settings.Set("backup.last_run", SqliteStore.Iso(DateTime.UtcNow)); // 先記錄，防失敗熱循環
        var source = _recordingsRoot;
        _ = Task.Run(() =>
        {
            try
            {
                var result = new BackupService(_store).Run(source, target);
                var text = result.Failed == 0
                    ? string.Format(Localizer.T("Main.BackupDoneOk"), result.Copied, FormatBytes(result.CopiedBytes))
                    : string.Format(Localizer.T("Main.BackupDoneRetry"), result.Copied, result.Failed);
                Dispatcher.BeginInvoke(() => HintText.Text = text);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => HintText.Text = string.Format(Localizer.T("Main.BackupFailed"), ex.Message));
            }
        });
    }

    /// <summary>快照保留天數：app_settings["snapshots.retention_days"] → HELIVMS_SNAPSHOT_DAYS → 30。</summary>
    private int ReadSnapshotDays()
    {
        if (_store is not null)
        {
            var fromDb = new SettingsRepository(_store).GetDoubleOrDefault(RetentionCoordinator.SnapshotDaysKey, -1);
            if (fromDb > 0)
            {
                return (int)fromDb;
            }
        }

        var raw = Environment.GetEnvironmentVariable("HELIVMS_SNAPSHOT_DAYS");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            return (int)v;
        }

        return RetentionCoordinator.DefaultSnapshotDays;
    }

    /// <summary>錄影保留配額（bytes）：app_settings["recording.quota_gb"] → HELIVMS_QUOTA_GB → 10GB。</summary>
    private long ReadQuotaBytes()
    {
        if (_store is not null)
        {
            var fromDb = new SettingsRepository(_store).GetDoubleOrDefault(RetentionCoordinator.QuotaGbKey, -1);
            if (fromDb > 0)
            {
                return (long)(fromDb * 1024 * 1024 * 1024);
            }
        }

        return ParseQuotaBytes();
    }

    private static long ParseQuotaBytes()
    {
        var gb = RetentionCoordinator.DefaultQuotaGb;
        var raw = Environment.GetEnvironmentVariable("HELIVMS_QUOTA_GB");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) &&
            v > 0)
        {
            gb = v;
        }

        return (long)(gb * 1024 * 1024 * 1024);
    }

    private async void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        // 佈局下拉最底是「復原」項目，不是佈局：選到它先還原成上一個佈局索引，再執行復原，
        // 否則會被當成一種（不存在的）格數套用。
        if (ReferenceEquals(LayoutCombo.SelectedItem, LayoutItemUndo))
        {
            _restoringLayoutSelection = true;
            LayoutCombo.SelectedIndex = _lastLayoutIndex;
            _restoringLayoutSelection = false;
            UndoCellOrder();
            return;
        }

        // 上面把下拉還原時會再進來一次：忽略那一次，別重跑佈局套用。
        if (_restoringLayoutSelection)
        {
            return;
        }

        _lastLayoutIndex = LayoutCombo.SelectedIndex;

        _expandInProgress = false;

        // 切換分割時跳台序列回到開頭，避免沿用舊分割的步進位置。
        _wallSequenceIndex = 0;

        // F11 展開指定格時，要把「聚焦到哪一格」交給這裡處理；手動選格子時為 -1（從頭開始）。
        var startIndex = _pendingSingleCell;
        _pendingSingleCell = -1;

        RebuildCells();
        SaveLayoutPreference();

        // 擴大佈局（例如 1 格 → 4 格）時，先前只顯示過第一格，其餘頻道還沒連線。
        // 原本這裡完全沒處理，於是多出來的格子永遠停在「未連線」。
        // 依目前頁簽取要顯示的頻道（「全部」＝所有頻道，群組＝該組成員）。
        var wall = WallChannels();
        if (_monitoring && _manager is { } mgr && wall.Count > 0)
        {
            // 序列化：快速連按 F11 或手動切換會讓這裡重入，兩次 ReconcileAsync 並行時
            // 後到的那次會把先建立的 session 從 _sessions 覆蓋掉，該 session 的 RTSP
            // 連線與解碼行程仍在跑卻沒有任何人能關，也不會再更新畫面。
            await _reconcileGate.WaitAsync();
            try
            {
                // 佈局改變即由 ReconcileAsync 重新套用顯示碼流策略（單畫面第 0 格主流、其餘次流）。
                var created = await mgr.ReconcileAsync(wall, startIndex < 0 ? 0 : startIndex, CurrentCellCount());
                if (created > 0)
                {
                    HintText.Text = string.Format(Localizer.T("Main.LayoutApplied"), created);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }
    }

    /// <summary>目前佈局選項對應的目標格數（列×欄由監看區比例自動決定）。</summary>
    private int CurrentCellCount()
    {
        var idx = Math.Clamp(LayoutCombo.SelectedIndex, 0, MaxLayoutIndex);
        return LayoutCellCounts[idx];
    }

    /// <summary>依佈局選項重建動態監看格（每格：影像＋AI 疊加＋狀態文字＋右鍵選單）。</summary>
    private void RebuildCells()
    {
        if (CellGrid is null)
        {
            return;
        }

        var count = CurrentCellCount();
        // 只當字級依據；實際列×欄由 ApplyGridGeometry 依監看區與畫面比例決定。
        var cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count)));
        CellGrid.Children.Clear();
        _cellStatusMenuItems.Clear();
        _cellZoom = 1.0;
        _zoomPanX = 0;
        _zoomPanY = 0;
        _panning = false;
        _panMoved = false;


        for (var i = 0; i < count; i++)
        {
            // 必須把 i 收進每圈各自的區域變數再讓事件委派捕捉。C# 的 for 迴圈變數是單一
            // 變數（不同於 foreach 的逐次複製），委派直接捕捉共用變數 i 會讓所有格子
            // 共用同一個 i；迴圈跑完 i==count，於是點任何一格都放大成同一路（count % 頻道數，
            // 常見為 CH01）——就是「每一格點下去都放大 CH01」的根因。
            var cellIndex = i;
            // Linear（雙線性）而非 WPF 預設的 HighQuality：預設含 MIP-map 的 Fant 重採樣，
            // 在 16 格同時重繪時是純額外成本，而監看是縮小顯示，線性已足夠。
            // 播放／事件中心／抹除預覽都已經是 Linear，只有這裡漏掉。
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            var overlay = new Canvas
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrOffline,
                FontSize = Math.Max(9, 16 - cols),
                Text = Localizer.T("Main.CellOffline"),
            };
            var badge = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = BrBadgeBg,
                Foreground = Brushes.White,
                FontSize = Math.Max(9, 15 - cols),
                Text = string.Empty,
                Visibility = Visibility.Collapsed,
            };
            // 數位縮放的套用層：影像與 AI 疊加一起變形，狀態文字與頻道徽章留在外層不跟著放大。
            // 疊加框用「顯示中影像」的比例繪製，兩者必須共用同一個 RenderTransform 才會貼在一起。
            var zoomLayer = new Grid();
            zoomLayer.Children.Add(img);
            zoomLayer.Children.Add(overlay);
            if (i == 0)
            {
                _zoomLayer = zoomLayer;
            }

            var inner = new Grid();
            inner.Children.Add(zoomLayer);
            inner.Children.Add(text);
            inner.Children.Add(badge);

            var border = new Border
            {
                Margin = new Thickness(1),
                Background = BrCellBg,
                BorderBrush = BrCellEdge,
                BorderThickness = new Thickness(1),
                Child = inner,
                // 影像改為 UniformToFill（填滿、必要時裁切）後，AI 疊加畫布可能比格子大，
                // 這裡裁掉溢出部分，避免框線畫到隔壁格。
                ClipToBounds = true,
            };
            // 左鍵：單純點擊走 OnCellClicked（選取／放大）；按住拖曳到別格則調整攝影機順序。
            // 兩個手勢共用同一個左鍵，靠拖曳門檻與放開位置區分（見 OnCellMouseUp）。
            border.MouseLeftButtonDown += (s, e) => OnCellMouseDown(cellIndex, (UIElement)s!, e);
            border.MouseMove += (s, e) => OnCellMouseMove(cellIndex, (UIElement)s!, e);
            border.MouseLeftButtonUp += (s, e) => OnCellMouseUp(cellIndex, (UIElement)s!, e);
            // 單分割時滾輪＝數位縮放；多分割不處理（也不吃掉事件）。
            border.MouseWheel += (_, e) => OnCellMouseWheel(cellIndex, zoomLayer, e);
            var menu = new ContextMenu();
            AddMenuItem(menu, Localizer.T("Main.CtxCellExpand"), OnCtxFullScreen, cellIndex);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, Localizer.T("Main.CtxRecord"), OnCtxRecord, i);
            AddMenuItem(menu, Localizer.T("Snapshot.Capture"), OnCtxSnapshot, i);
            AddMenuItem(menu, Localizer.T("Diag.Menu"), OnCtxDiagnostics, i);
            AddMenuItem(menu, Localizer.T("Main.CtxPtz"), OnCtxPtz, i);
            AddMenuItem(menu, Localizer.T("Main.CtxPlayback"), OnCtxPlaybackChannel, i);
            AddMenuItem(menu, Localizer.T("Main.CtxExport"), OnCtxExportChannel, i);
            AddMenuItem(menu, Localizer.T("Main.CtxEvents"), OnCtxEventsChannel, i);
            AddMenuItem(menu, Localizer.T("Talk.Menu"), OnCtxTalk, i);
            menu.Items.Add(new Separator());
            var statusItem = AddMenuItem(menu, Localizer.T("Monitor.ShowStatus"), OnToggleCellStatus, i);
            statusItem.IsCheckable = true;
            statusItem.IsChecked = _showCellStatus;
            _cellStatusMenuItems.Add(statusItem);
            border.ContextMenu = menu;

            _cellImages[i] = img;
            _cellOverlays[i] = overlay;
            _cellTexts[i] = text;
            _cellBadges[i] = badge;
            _cellBorders[i] = border;

            CellGrid.Children.Add(border);
        }

        ApplyGridGeometry();
        ApplyCellStateAfterRebuild();
    }

    /// <summary>依目標格數與監看區大小，決定格線的列×欄與外框尺寸（每格維持畫面比例、置中）。</summary>
    private void ApplyGridGeometry()
    {
        if (CellGrid is null || MonitorArea is null)
        {
            return;
        }

        var count = CurrentCellCount();
        var areaW = MonitorArea.ActualWidth;
        var areaH = MonitorArea.ActualHeight;

        var fit = MonitorGrid.Fit(count, areaW, areaH, CurrentCellAspect());

        CellGrid.Columns = fit.Columns;
        CellGrid.Rows = fit.Rows;

        // 外框尺寸為 0 表示監看區還沒量到（版面首次載入），交還自動尺寸，
        // 等 SizeChanged 再算一次。
        if (fit.Width > 0 && fit.Height > 0)
        {
            CellGrid.Width = fit.Width;
            CellGrid.Height = fit.Height;
        }
        else
        {
            CellGrid.Width = double.NaN;
            CellGrid.Height = double.NaN;
        }
    }

    private void OnMonitorAreaSizeChanged(object sender, SizeChangedEventArgs e) => ApplyGridGeometry();

    /// <summary>格子要維持的畫面比例：取第一個已知的來源解析度，未知時用 16:9。</summary>
    /// <remarks>
    /// 用偵測到的比例（例如 4:3 的舊攝影機）當格子比例，UniformToFill 才不會白白裁掉畫面；
    /// 只有來源比例與格子比例真的不同（例如混接 4:3 與 16:9）時才會裁切。
    /// </remarks>
    private double CurrentCellAspect()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellResolution[i] is { Width: > 0, Height: > 0 } res)
            {
                return (double)res.Width / res.Height;
            }
        }

        return 16.0 / 9.0;
    }

    private static MenuItem AddMenuItem(ContextMenu menu, string header, RoutedEventHandler handler, int cell)
    {
        var item = new MenuItem { Header = header, Tag = cell };
        item.Click += handler;
        menu.Items.Add(item);
        return item;
    }

    /// <summary>
    /// 切換「即時監看狀態文字」的顯示並記入 app_settings（預設關閉）。
    /// </summary>
    /// <remarks>
    /// WPF 會在 Click 之前就翻好 <see cref="MenuItem.IsChecked"/>，所以這裡讀 sender 的現值，
    /// 不自己再做一次 <c>!_showCellStatus</c>——否則會與畫面顯示的勾選狀態不一致。
    /// 勾選狀態是全域共用，切換後同步每一格的選單項目，否則切到別格再開選單會看到舊值。
    /// </remarks>
    private void OnToggleCellStatus(object sender, RoutedEventArgs e)
    {
        _showCellStatus = (sender as MenuItem)?.IsChecked ?? !_showCellStatus;

        foreach (var item in _cellStatusMenuItems)
        {
            item.IsChecked = _showCellStatus;
        }

        if (_store is not null)
        {
            new SettingsRepository(_store).Set("monitor.show_status", _showCellStatus ? "1" : "0");
        }

        RefreshCellStatusText();
    }

    /// <summary>把目前所有處於連線中的格，依 <see cref="_showCellStatus"/> 重畫或收掉狀態文字。</summary>
    private void RefreshCellStatusText()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellStates[i] != RtspState.Streaming)
            {
                continue;
            }

            RenderCellStatusText(i);
        }
    }

    private void ApplyCellStateAfterRebuild()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellImages[i] is null)
            {
                continue;
            }

            // 還原真實狀態。原本這裡一律寫「未連線」，而幀路徑只在文字是
            // 「連線中／重連中」時才更新成「即時監看」——兩者一撞，
            // 任何佈局切換（含 F11 往返）之後，正在串流的格子狀態文字就永遠停在
            // 「未連線」，即使影像還在跑。操作員看到的就是「有畫面但說沒連線」。
            // 影像也走同一個投影途徑（ApplyCellState → ShowCellPlaceholder），
            // 才不會在重建後漏掉佔位圖或用了錯的縮放模式。
            ApplyCellState(i, _cellStates[i]);

            // AI 疊加與選取高亮同樣在此遺失，一併還原。
            CellOverlay(i)?.Children.Clear();
            _overlayGate.Reset();
            DrawLiveOverlay(i);
        }

        SelectCell(_selectedCell);
    }

    /// <summary>把格數對到最接近的佈局選項索引。佈局記憶以此還原，選項清單增減（含中間插入）都不會改變已存的佈局。</summary>
    private static int LayoutIndexForCells(int cells)
    {
        var best = 0;
        var bestDelta = int.MaxValue;
        for (var i = 0; i < LayoutCellCounts.Length; i++)
        {
            var delta = Math.Abs(LayoutCellCounts[i] - cells);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }

        return best;
    }

    /// <summary>佈局記憶：重開沿用上次分割（LIVEVIEW §1.1）；每個頁簽各自記住自己的格數。</summary>
    private void ApplyLayoutPreference()
    {
        var pref = 0;
        try
        {
            var path = Path.Combine(_dataRoot, UiSettingsFile);
            if (File.Exists(path))
            {
                _layoutPrefs = MonitorLayoutPrefs.Parse(File.ReadAllText(path));

                // 最後停留的頁簽（「全部」為 null）。此時 _groups 尚未建立，實際是否存在交由
                // 稍後的 ReloadGroupTabs 驗證；不存在的群組會退回「全部」。
                _activeGroupId = _layoutPrefs.ActiveGroupId;

                // 以格數為準：選項清單日後增減（尤其在中間插入 6／12／20… 這類路數）都不會讓
                // 操作員記下的分割走樣——存索引的話，同一個索引會因為前面多插了選項而變成別的分割。
                if (_layoutPrefs.CellsFor(_activeGroupId) is int cells)
                {
                    pref = LayoutIndexForCells(cells);
                }
                else if (_layoutPrefs.LayoutIndex is int layout)
                {
                    // 舊格式只有索引，只能以索引解讀；超出目前選項數就夾到最接近的合法佈局。
                    pref = Math.Clamp(layout, 0, MaxLayoutIndex);
                }
            }
        }
        catch (Exception)
        {
            pref = 0;
        }

        if (LayoutCombo.SelectedIndex != pref)
        {
            LayoutCombo.SelectedIndex = pref;
        }
        else
        {
            RebuildCells();
        }
    }

    private void SaveLayoutPreference()
    {
        try
        {
            // 把目前頁簽的格數記進它的鍵，再寫回；切到別的頁簽才還原得回來。
            _layoutPrefs.ActiveTab = MonitorLayoutPrefs.TabKey(_activeGroupId);
            _layoutPrefs.Remember(_activeGroupId, CurrentCellCount());
            File.WriteAllText(
                Path.Combine(_dataRoot, UiSettingsFile),
                _layoutPrefs.ToJson(LayoutCombo.SelectedIndex));
        }
        catch (Exception)
        {
            // 佈局記憶失敗不影響即時監看
        }
    }

    /// <summary>指定頁簽要套用的格數；該頁簽沒有記錄時退回「全部」頁簽的格數，再退回目前格數。</summary>
    private int TabCellCount(long? groupId)
        => _layoutPrefs.CellsFor(groupId)
            ?? _layoutPrefs.CellsFor(null)
            ?? CurrentCellCount();

    /// <summary>M14 每格 AI 策略：單格取樣 200ms；多格僅選中格啟動、小格降頻（≤4路 400ms／多路 800ms）。</summary>
    private void UpdateAiPolicy()
    {
        if (_manager is null)
        {
            return;
        }

        var count = CurrentCellCount();
        for (var cell = 0; cell < count; cell++)
        {
            if (_cellChannel[cell] is not int channelId)
            {
                continue;
            }

            var enabled = count == 1 || cell == _selectedCell;
            var interval = count == 1 ? 200 : (count <= 4 ? 400 : 800);
            if (_aiPolicyCache.TryGetValue(channelId, out var cur) && cur.Enabled == enabled && cur.Interval == interval)
            {
                continue;
            }

            _manager.SetAiPolicy(channelId, enabled, interval);
            _aiPolicyCache[channelId] = (enabled, interval);
        }
    }

    private void OnCellState(int cell, int channelId, RtspState state)
    {
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        // 用 BeginInvoke 而非 Invoke：Invoke 會同步阻塞呼叫執行緒，而本事件同時由
        // RtspClient 讀取執行緒與 ChannelManager 的健康檢查 timer 執行緒觸發。
        // UI 執行緒忙著畫 16 格時，那些執行緒會被卡住（async void 的健康檢查還會跨幀持有）。
        // _cellChannel／_cellStates／_channelStates 也只在這裡（UI 執行緒）寫入：
        // 原本它們在背景執行緒寫、UI 執行緒讀 Dictionary，是無保護的跨執行緒存取。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _cellChannel[cell] = channelId;
            _channelStates[channelId] = state;
            _cellStates[cell] = state;
            ApplyCellState(cell, state);
            UpdateFooter();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// 格位改派後同步 UI 的 cell→頻道對應與該格狀態。
    /// </summary>
    /// <remarks>
    /// 這條路徑取代不了 <c>OnCellState</c>：改派時既有 session 不會再發狀態變化，
    /// 而徽章、狀態文字、錄影指示與每格計量全都讀 <c>_cellChannel</c>。
    /// 少了它，按 F11 之後畫面與文字會指向不同頻道，而且沒有任何錯誤訊息。
    /// </remarks>
    private void OnCellMappingChanged(int cell, int channelId)
    {
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_cellChannel[cell] == channelId)
            {
                return;
            }

            var previous = _cellChannel[cell];
            _cellChannel[cell] = channelId;

            // 格位換了頻道，沿用舊解析度／fps 只會顯示別人的數字，必須一起清掉。
            _cellResolution[cell] = null;
            _cellMeters[cell].Clear();

            // 也要清掉上一台留下的畫面：新頻道的第一張影格可能數百毫秒後才到（甚至連不上），
            // 不清的話監看牆會停在被刪除／被換掉那台的畫面，看起來像它還在連。
            if (previous != channelId)
            {
                _bitmap[cell] = null;
                SetCellSource(cell, null);
                _cellStates[cell] = RtspState.Stopped;
            }

            ApplyCellState(cell, _cellStates[cell]);
            UpdateBadges();
            UpdateFooter();
        }), DispatcherPriority.Background);
    }

    /// <summary>錄影停滯（ffmpeg 連續錄不成，但 <c>IsRecording</c> 仍是 true）。</summary>
    /// <remarks>
    /// 事件來自錄影執行緒。警示必須「持續存在」到復原為止，不能只丟一次浮動警報：
    /// 錄影停滯的典型現場就是無人值守，操作員不會正好在那幾秒鐘看著畫面。
    /// 復原事件（<see cref="RecordingScheduler.RecordingResumed"/>）是唯一能可靠清除它的訊號。
    /// </remarks>
    private void OnRecordingStalled(object? sender, RecordingStallInfo stall)
    {
        var isNew = !_recordingStalls.ContainsKey(stall.ChannelId);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _recordingStalls[stall.ChannelId] = stall;
            RefreshCellForChannel(stall.ChannelId);
            UpdateCellStatus();
            UpdateBadges();

            // 只在「首次」停滯時推警報：RecordingStalled 每個頻道只發一次，
            // 但重連退避重試期間若改為每次都推，會把警報洗成雜訊。
            if (isNew)
            {
                PushAlert(string.Format(Localizer.T("Main.CellStalledAlert"), stall.ChannelId, stall.ConsecutiveFailures), GetCell(stall.ChannelId));
            }
        }), DispatcherPriority.Background);
    }

    private void OnRecordingResumed(object? sender, int channelId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_recordingStalls.Remove(channelId))
            {
                return;
            }

            RefreshCellForChannel(channelId);
            UpdateCellStatus();
            UpdateBadges();
        }), DispatcherPriority.Background);
    }

    /// <summary>某頻道對應的格位重畫狀態文字（事件以頻道 ID 進來，UI 一切以格位為準）。</summary>
    private void RefreshCellForChannel(int channelId)
    {
        var cell = GetCell(channelId);
        if (cell >= 0)
        {
            ApplyCellState(cell, _cellStates[cell]);
        }
    }

    private int GetCell(int channelId)
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellChannel[i] == channelId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>把權威狀態投影成格內文字與顏色（唯一寫入途徑，佈局重建後也走這裡）。</summary>
    private void ApplyCellState(int cell, RtspState state)
    {
        // 非即時狀態不保留上一段／上一台的影格，否則斷線後畫面會凍在最後一幀、看起來還在連。
        // 影像交由 ShowCellPlaceholder 顯示「未指派」(Disable) 或「已指派但斷線」(Invisible) 佔位圖。
        if (state != RtspState.Streaming)
        {
            _bitmap[cell] = null;
        }

        ShowCellPlaceholder(cell);

        // 沒有指派頻道的空格不顯示「未連線」文字：Disable 佔位圖已說明這格還沒配頻道，
        // 再寫「未連線」會被誤讀成「配了卻連不上」，也讓空格看起來像有問題。
        if (_cellChannel[cell] is null)
        {
            SetCellStatus(cell, string.Empty, BrOffline);
            return;
        }

        // 錄影停滯是比串流狀態更嚴重的問題：畫面照常、IsRecording 也是 true，
        // 但磁碟上沒有檔案。所以它在這裡壓過狀態文字，並且必須一直留到復原為止。
        if (_cellChannel[cell] is int owner && _recordingStalls.TryGetValue(owner, out var stall))
        {
            SetCellStatus(cell, string.Format(Localizer.T("Main.CellStalled"), stall.ConsecutiveFailures), BrStall);
            return;
        }

        switch (state)
        {
            case RtspState.Streaming:
                // 解析度／fps／延遲由幀路徑與每秒巡檢補齊。
                RenderCellStatusText(cell);
                break;
            case RtspState.Connecting:
                SetCellStatus(cell, Localizer.T("Main.CellConnecting"), BrConnecting);
                break;
            case RtspState.Reconnecting:
                // 帶上原因：401、逾時、位址錯誤的處置完全不同，只寫「重連中」等於沒有資訊。
                var reason = _cellChannel[cell] is int rc ? _manager?.ReconnectReason(rc) : null;
                SetCellStatus(cell, reason is null ? Localizer.T("Main.CellReconnecting") : $"{Localizer.T("Main.CellReconnecting")} · {reason}", BrConnecting);
                PushAlert(string.Format(Localizer.T("Main.CellReconnectingAlert"), cell + 1, reason is null ? string.Empty : $"：{reason}"), cell);
                break;
            default:
                SetCellStatus(cell, Localizer.T("Main.CellOffline"), BrOffline);
                break;
}
    }

    private void UpdateFooter()
    {
        var live = _manager?.CountStreaming() ?? 0;
        StatusText.Text = _footerBase.Length > 0 ? string.Format(Localizer.T("Main.FooterConnected"), _footerBase, live) : string.Empty;
    }

    /// <summary>
    /// 以資料庫授權列（單一來源）重算頁尾授權摘要。
    /// </summary>
    /// <remarks>
    /// 頁尾原本直接讀 <c>license.lic</c> 檔，而錄影閘門與功能隱藏讀 <c>license</c> 列；
    /// 匯入後兩者可能不同步（例如列被標記失效、或檔案被換掉），畫面就會出現
    /// 「頁尾說已授權、閘門卻拒絕」的矛盾。改走與閘門相同的 <see cref="LicenseService"/>。
    /// </remarks>
    private void RefreshLicenseFooter()
    {
        if (_licenseService is null)
        {
            return;
        }

        var license = _licenseService.Evaluate(DateTime.UtcNow);
        _footerBase = license.Decision switch
        {
            LicenseDecision.Valid => license.ExpiresUtc is { } expires
                ? string.Format(Localizer.T("Main.FooterAuthorizedExpiring"), license.MaxCameras, expires)
                : string.Format(Localizer.T("Main.FooterAuthorized"), license.MaxCameras),
            _ => string.Format(Localizer.T("Main.FooterNotLicensed"), license.Message ?? license.Decision.ToString()),
        };
        UpdateFooter();
    }

    /// <summary>
    /// 所有已寫入資料庫的警報事件外送前的唯一入口。
    /// </summary>
    /// <remarks>
    /// 三個來源（解碼／IO／分析引擎）必須走同一個判斷，否則聚合規則只對其中一種
    /// 事件生效：現場看到的會是「移動事件被聚合了、門磁事件照樣洗版」，
    /// 而規則頁寫的是同一條規則。
    /// </remarks>
    private void RouteAlarm(AlarmEventRecord record)
    {
        // 聚合判定要讀資料庫；這些事件可能來自解碼執行緒。
        // SmartAlertHost 內部讀不到資料時會退化成「照常通知」，所以這裡不會丟事件。
        SmartAlertOutcome outcome;
        try
        {
            outcome = _smartAlerts?.Observe(record) ?? SmartAlertOutcome.Unaggregated;
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(
                new Action(() => HintText.Text = string.Format(Localizer.T("Main.SmartAggregateFail"), ex.Message)),
                DispatcherPriority.Background);
            _notify?.Enqueue(record);
            return;
        }

        // 沒有任何聚合規則命中 → 照常通知這一筆原始事件。
        if (!outcome.Aggregated)
        {
            _notify?.Enqueue(record);
            return;
        }

        // 命中但門檻還沒到（或這個窗已經告過警）→ 不外送原始事件。
        foreach (var trigger in outcome.Triggers)
        {
            _notify?.Enqueue(new AlarmEventRecord
            {
                ChannelId = record.ChannelId,
                EventType = SmartAlertHost.SmartAlertEventType,
                StartUtc = record.StartUtc,
                SnapshotPath = record.SnapshotPath,
                Detail = string.Format(Localizer.T("Main.SmartAggregateFired"), trigger.RuleName, trigger.WindowCount),
            });
        }
    }

    private void OnCellFrame(int cell, VideoFrame frame)
    {
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        // 只把「第一張」影格送進 Dispatcher；繪製期間抵達的後續影格直接覆蓋該格的待繪製項目。
        // 原本每張影格都 InvokeAsync，而 Dispatcher 佇列沒有容量上限——UI 執行緒一旦跟不上
        // 16 路 × 15fps，佇列就會無限膨脹且畫面愈拖愈舊（症狀：CPU 沒滿載但畫面愈LAG）。
        if (!_pendingFrames.TrySchedule(cell, frame))
        {
            return;
        }

        // 用 Render 優先權：影格是「此刻」的樣子，排在輸入事件之後，
        // 避免滑鼠操作被每分鐘數百張的繪圖工作壓住。
        Dispatcher.BeginInvoke(new Action(() => RenderCellFrame(cell)), DispatcherPriority.Render);
    }

    /// <summary>在 UI 執行緒把該格最新一張影格畫上去（每格最多一張在途）。</summary>
    private void RenderCellFrame(int cell)
    {
        var frame = _pendingFrames.Take(cell);
        if (frame is null)
        {
            return;
        }

        if (_cellChannel[cell] is int frCh)
        {
            _channelLastFrame[frCh] = frame.TimestampUtc;
        }

        var firstResolution = _cellResolution[cell] is null;
        _cellResolution[cell] = (frame.Width, frame.Height);
        if (firstResolution)
        {
            // 有首張影格才知道來源畫面比例，據此重排格線（所有格子鎖定同一比例）。
            ApplyGridGeometry();
        }
        _cellMeters[cell].Record(frame.TimestampUtc);

        var pending = _bitmap[cell];
        if (pending is null ||
            pending.PixelWidth != frame.Width ||
            pending.PixelHeight != frame.Height)
        {
            pending = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            _bitmap[cell] = pending;
            SetCellSource(cell, pending);
        }

        pending.Lock();
        try
        {
            pending.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Stride,
                0);
        }
        finally
        {
            pending.Unlock();
        }

        // 收到影格＝「此刻真的有畫面」，是狀態的地面真相。改派格位時管理器重用既有 session
        // 且不再補送狀態事件（見 OnCellMappingChanged），_cellStates 會停在 Stopped；而狀態文字
        // 只在 Streaming 時更新，於是畫面在動、文字卻永遠寫「未連線」。用影格到達把狀態補回，
        // 才不會出現「明明有畫面卻說沒連線」。
        if (_cellStates[cell] != RtspState.Streaming)
        {
            _cellStates[cell] = RtspState.Streaming;
            if (_cellChannel[cell] is int liveChannel)
            {
                _channelStates[liveChannel] = RtspState.Streaming;
            }
        }

        RenderCellStatusText(cell);

        DrawLiveOverlay(cell);
    }

    /// <summary>
    /// 組合該格的狀態文字：狀態 ＋ 解析度 ＋ 實際更新率 ＋ 畫面年齡。
    /// </summary>
    /// <remarks>
    /// 「畫面年齡」是使用者此刻看到的畫面距來源多舊——「愈拖愈舊」這個問題在沒有數字時
    /// 很難和機器效能不足區分開，而誤判的後果是去買更快的 CPU，實際瓶頸在 UI 執行緒。
    /// 這行平時預設不顯示（<see cref="_showCellStatus"/>），切換後由右鍵選單立即生效。
    /// </remarks>
    private void RenderCellStatusText(int cell)
    {
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        if (!_showCellStatus)
        {
            SetCellStatus(cell, string.Empty, null);
            return;
        }

        var parts = new List<string>(4) { Localizer.T("Main.CellLive") };
        if (_cellResolution[cell] is { } res)
        {
            parts.Add($"{res.Width}×{res.Height}");
        }

        var meter = _cellMeters[cell];
        var fps = meter.Fps;
        if (fps > 0)
        {
            parts.Add($"{fps:0.#} fps");
        }

        var age = meter.Age(DateTime.UtcNow);
        if (age > TimeSpan.Zero)
        {
            parts.Add(age.TotalSeconds < 10
                ? string.Format(Localizer.T("Main.CellLatency"), age.TotalMilliseconds.ToString("0"))
                : string.Format(Localizer.T("Main.CellStale"), age.TotalSeconds.ToString("0.#")));
        }

        SetCellStatus(cell, string.Join(" · ", parts), BrLive);
    }

    private async Task DisconnectAllAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisconnectAllAsync();
        }

        for (var i = 0; i < MaxCells; i++)
        {
            _bitmap[i] = null;
            _cellChannel[i] = null;
            _aiBoxes[i] = [];
            _cellStates[i] = RtspState.Stopped;
            _cellResolution[i] = null;
            _cellMeters[i].Clear();
            ApplyCellState(i, RtspState.Stopped);
            CellOverlay(i)?.Children.Clear();
        }

        // 丟掉尚未繪製的影格：否則中斷之後才排到 UI 佇列的舊影格會在新連線建立後被畫出來，
        // 操作員會看到上一段串流的殘影閃一下。
        _pendingFrames.Clear();
        Array.Clear(_motionFlashUntil);
        _motionTimer?.Stop();
        BumpAllOverlayGenerations();
        _overlayGate.Reset();

        _channelStates.Clear();
        _channelLastFrame.Clear();
        _aiPolicyCache.Clear();
        _selectedCell = -1;

        _monitoring = false;
        RecBadge.Visibility = Visibility.Collapsed;
        HintText.Text = Localizer.T("Main.Disconnected");
        UpdateFooter();
    }

    /// <summary>
    /// 依授權隱藏未授權功能的入口（M209／§19.4）。
    ///
    /// 隱藏只是體面，事件處理器與 CLI 另有 <see cref="RequireFeature"/> 硬擋；
    /// 此處同時把 <c>AiToggle</c> 的 <c>_aiVisible</c> 關掉——只藏控制項而不清狀態，
    /// 疊加仍會照繪，會出現「沒有開關卻一直在畫 AI 方框」的幽靈行為。
    /// </summary>
    private void ApplyLicenseVisibility()
    {
        if (_licenseGate is null)
        {
            return;
        }

        // 受旗標控制的功能入口（排程／偵測／音訊／感測 IO／巡航）已移入設定中心，
        // 只在「地圖」留在監控中心頂欄（見 LicenseEntryPointContractTests ★SettingsWindow 契約）。
        _licenseGate.Apply(MapButton, LicenseFeatures.Gis, Localizer.T("Main.MapTip"));

        // AI 疊加開關對應 ai；偵測設定／分析視窗才是 ai.l1（§19.4 入口對應）。
        // 先前把開關綁在 ai.l1，只帶 ai 的授權會出現「看得到開關、一切就失敗」。
        _licenseGate.Apply(AiToggle, LicenseFeatures.Ai, Localizer.T("Main.AiOverlayTip"));
        if (!_licenseGate.Allows(LicenseFeatures.Ai))
        {
            AiToggle.IsChecked = false;
            _aiVisible = false;
        }

        ApplyLicenseExpiryBanner(_licenseGate);
    }

    /// <summary>
    /// 到期提醒浮條（M211／§19.4「到期前 14 天 UI 浮條提醒」）。
    ///
    /// 只在 <c>LicenseExpiry.ShouldWarn</c> 時顯示。未匯入／已作廢／時鐘回流不在此提示——
    /// 那三種是「授權無效」，設定中心與各閘門已經說明，不必再混一條黃色浮條。
    /// </summary>
    private void ApplyLicenseExpiryBanner(LicenseUiGate gate)
    {
        var notice = gate.Current.Expiry(DateTime.UtcNow);
        if (notice.ShouldWarn && notice.Message is { } message)
        {
            LicenseBanner.Visibility = Visibility.Visible;
            LicenseBannerText.Text = message;

            // 已到期是「現在就沒在錄」的事態，底色加深；即將到期仍有機會走流程。
            LicenseBanner.Background = notice.Stage == LicenseExpiryStage.Expired
                ? (Brush)FindResource("Brand.BgDeep")
                : (Brush)new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x10));
            LicenseBannerText.Foreground = notice.Stage == LicenseExpiryStage.Expired
                ? (Brush)new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x9A))
                : (Brush)new SolidColorBrush(Color.FromRgb(0xF5, 0xD7, 0x8E));
            return;
        }

        LicenseBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>浮條上的「前往設定中心」：續期動作就在那裡，不該讓使用者自己去猜。</summary>
    private void OnLicenseBannerActionClicked(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    /// <summary>
    /// 未授權功能的第二道擋：事件處理器與 CLI 都必須呼叫（鍵盤與命令列繞得過隱藏）。
    /// 回傳 false 時已寫入 <see cref="HintText"/>，呼叫端直接 return。
    /// </summary>
    private bool RequireFeature(string feature)
    {
        if (_licenseGate?.Allows(feature) != false)
        {
            return true;
        }

        var reason = _licenseGate.DenialMessage(feature) ?? Localizer.T("Main.FeatureNotLicensed");
        HintText.Text = reason;
        PushAlert(reason);
        return false;
    }

    /// <summary>
    /// 錄影被授權閘門拒絕時的使用者回饋（§19.4「於事件中心提示『已達授權上限』」）。
    /// 狀態列與即時警報列給即時訊息；通道上限另寫一筆事件到事件中心，使用者事後仍查得到。
    /// </summary>
    private void ReportRecordingBlocked(RecordingGateResult blocked)
    {
        var reason = blocked.Reason ?? Localizer.T("Main.RecordingNotLicensed");
        HintText.Text = reason;
        PushAlert(reason);
        UpdateFooter();

        _ = _licenseLimits?.Report(blocked, DateTime.UtcNow);
    }

    /// <summary>開啟攝影機管理（devices：ONVIF 探索、憑證與主／次碼流）。僅 admin。</summary>
    private void OnCameraManageClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        // 傳入 _manager 讓「狀態」欄顯示底下頻道的即時連線狀態。
        OpenChild(new CameraManagerWindow(
            _store!, _manager, channelsChanged: () => _ = ReloadChannelsIfChangedAsync()));
    }

    /// <summary>
    /// 開啟頻道管理（channels：由攝影機碼流建立／編輯／刪除）。僅 admin。
    /// 管理視窗關閉後重新載入清單，因為期間可能新增或刪除了頻道。
    /// </summary>
    private void OnChannelManageClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        // 頻道異動後的清單重載與補連線統一由 OpenChild 的關閉處理負責（ReloadChannelsIfChangedAsync），
        // 這裡不再自行刷新，避免兩處都改清單導致簽章相同而漏掉重新連線。
        OpenChild(new ChannelManagerWindow(
            _store!, channelsChanged: () => _ = ReloadChannelsIfChangedAsync()));
    }

    private void OnPlaybackClicked(object sender, RoutedEventArgs e)
    {
        OpenChild(new PlaybackWindow(_store!));
    }

    private void OnEventClicked(object sender, RoutedEventArgs e)
    {
        RefreshUnackBadge();
        OpenEventCenter();
    }

    /// <summary>開啟事件中心（M38 供 harness 以 --events 自動開啟）。</summary>
    private void OpenEventCenter()
    {
        OpenChild(new EventCenterWindow(_store!, alerts: _manager?.Alerts));
    }

    private void OnScheduleClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || !RequireFeature(LicenseFeatures.Schedule))
        {
            return;
        }

        OpenChild(new SchedulingWindow(_store!));
    }

    private void ExportEventsCsv(string path)
    {
        if (!RequireAdmin())
        {
            return;
        }

        var source = new ChannelRepository(_store!);
        var events = new AlarmEventRepository(_store!);
        var q = new AlarmEventRepository.QueryArgs
        {
            FromUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = DateTime.UtcNow.AddDays(30),
            Limit = 100000,
            Offset = 0,
        };
        var list = events.ListByQuery(q);
        EventCenterWindow.WriteCsv(path, list, source.List());
    }

    /// <summary>依現況語言重設主視窗所有文字（M57 設定中心即時切換用）。</summary>
    public void RefreshTitle()
    {
        ApplyI18n();
    }

    private void OnDetectionClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireFeature(LicenseFeatures.AiL1))
        {
            return;
        }

        OpenChild(new DetectionWindow(_store!));
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    /// <summary>頂欄「全螢幕／離開全螢幕」按鈕（F11 已被「單格展開」佔用，故用按鈕＋Esc 進出）。</summary>
    private void OnToggleFullScreenClicked(object sender, RoutedEventArgs e) => SetWindowFullScreen(!_isWindowFullScreen);

    /// <summary>頂欄「離開」按鈕：交給 <see cref="OnClosing"/> 跳「最小化／離開」選擇。</summary>
    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        switch (ExitChoiceWindow.Ask(this, _isWindowFullScreen))
        {
            case ExitChoice.Minimize:
                Hide();
                WriteTrayProbeIfRequested();
                break;
            case ExitChoice.Quit:
                _exiting = true;
                _tray?.Dispose();
                _tray = null;
                Dispatcher.BeginInvoke(Close);
                break;
            case ExitChoice.ExitFullScreen:
                SetWindowFullScreen(!_isWindowFullScreen);
                break;
        }
    }

    /// <summary>
    /// 切換主視窗全螢幕 ↔ 可調整大小的視窗。
    /// </summary>
    /// <remarks>
    /// 全螢幕＝<see cref="WindowStyle.None"/> ＋ <see cref="ResizeMode.NoResize"/> ＋
    /// <see cref="WindowState.Maximized"/>：只設 Maximized 會留著標題列，工作列也跟著露出來，
    /// 看起來是「最大化」而不是操作員要的滿版；必須一併去掉視窗框。
    /// 先設 <see cref="WindowState.Normal"/> 再設 Maximized，否則有些情況下直接切 Maximized 不會
    /// 覆蓋整個螢幕。還原值在「進入」當下才捕捉，所以從一般視窗按進全螢幕再按出來會回到原尺寸。
    /// XAML 以全螢幕啟動（<c>_isWindowFullScreen</c> 初始為 true），此時的還原預設值是一般視窗。
    /// 關閉鍵的行為不變：<see cref="OnClosing"/> 仍收進系統匣，故無標題列也不會無從結束。
    /// </remarks>
    private void SetWindowFullScreen(bool fullScreen)
    {
        if (_isWindowFullScreen == fullScreen)
        {
            return;
        }

        _isWindowFullScreen = fullScreen;
        if (fullScreen)
        {
            _restoreWindowStyle = WindowStyle;
            _restoreResizeMode = ResizeMode;
            _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _restoreWindowStyle;
            ResizeMode = _restoreResizeMode;
            WindowState = _restoreWindowState;
        }

        UpdateFullScreenButton();
    }

    private void UpdateFullScreenButton()
    {
        ExitFullScreenMenuItem.Header = _isWindowFullScreen
            ? Localizer.T("Main.ExitFullScreen")
            : Localizer.T("Main.FullScreen");
    }

    private void OnExportClicked(object sender, RoutedEventArgs e) => OpenExportWindow();

    private void OnExportCenterClicked(object sender, RoutedEventArgs e) => OpenExportCenterWindow();

    private void OnRedactionClicked(object sender, RoutedEventArgs e) => OpenRedactionWindow();

    private void OnDewarpClicked(object sender, RoutedEventArgs e) => OpenDewarpWindow();

    private void OnAlarmManagerClicked(object sender, RoutedEventArgs e) => OpenAlarmManagerWindow();

    /// <summary>開啟警報管理器（M47，§14.7 #3）：分診面板，與事件中心同權限（不另限 admin）。</summary>
    private void OpenAlarmManagerWindow()
    {
        OpenChild(new AlarmManagerWindow(_store!, _manager?.Alerts));
    }

    /// <summary>開啟錄影遮蔽窗（M46，§14.7 #5）。viewer 與匯出同權限限制。</summary>
    private void OpenRedactionWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new RedactionWindow(_store!, _dataRoot));
    }

    /// <summary>開啟魚眼矯正窗（M48，§14.7 #2）。viewer 與匯出同權限限制。</summary>
    private void OpenDewarpWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new DewarpWindow(_store!, _dataRoot));
    }

    /// <summary>開啟外部安全共享視窗（M51，§14.7 #4）。viewer 與匯出同權限限制；remote 旗標限定（M220）。</summary>
    private void OpenShareWindow()
    {
        if (!SessionContext.IsAdmin || !RequireFeature(LicenseFeatures.Remote))
        {
            return;
        }

        OpenChild(new ShareWindow(_store!, _dataRoot));
    }

    /// <summary>開啟分析情境視窗（M52，§14.7 #6）。viewer 與匯出同權限限制。</summary>
    private void OpenAnalyticsWindow()
    {
        if (!SessionContext.IsAdmin || !RequireFeature(LicenseFeatures.AiL1))
        {
            return;
        }

        OpenChild(new AnalyticsWindow(_store!));
    }

    /// <summary>開啟證據完整性視窗（M53，§14.7 #5）。</summary>
    private void OpenEvidenceWindow(string? directory)
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new EvidenceWindow(_store!, directory));
    }

    /// <summary>開啟匯出中心（M44，§14.3(2)）。viewer 與匯出精靈同權限限制。</summary>
    private void OpenExportCenterWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new ExportCenterWindow(_store!, _dataRoot));
    }

    /// <summary>
    /// 依登入角色限制管理功能（M42）。
    ///
    /// 監控中心的手動入口（連線、錄影、匯出、裝置、排程、PTZ…）已全部移除，改為開機自動連線
    /// 與全時錄影，因此目前沒有需要對 viewer 停用的控制項；其餘管理入口在設定中心各自的 handler
    /// 內擋 <see cref="SessionContext.IsAdmin"/>。保留此呼叫點是為了讓 RBAC 契約
    /// （RbacEntryPointContractTests）仍能在 OnLoaded 對帳，日後新增入口時才有落點。
    /// </summary>
    private void ApplyRoleRestrictions()
    {
    }

    /// <summary>viewer 身分嘗試執行管理動作時擋下（M42 RBAC，§18.6）。</summary>
    private bool RequireAdmin()
    {
        if (SessionContext.IsAdmin)
        {
            return true;
        }

        HintText.Text = Localizer.T("Main.AdminOnly");
        return false;
    }

    /// <summary>開啟通知送達紀錄（M23，§16.3）；雙擊列＝回事件中心分診該頻道未確認。</summary>
    private void OnNotificationClicked(object sender, RoutedEventArgs e)
    {
        OpenChild(new NotificationLogWindow(_store!, ch =>
            OpenChild(new EventCenterWindow(_store!, alerts: _manager?.Alerts), w => ((EventCenterWindow)w).FocusUnacknowledged(ch))));
    }

    /// <summary>開啟管理設定中心（M19，§9）。僅 admin（M42）。</summary>
    private void RunOffsiteDueJobs()
    {
        if (_offsite is null || _exiting)
        {
            return;
        }

        try
        {
            Task.Run(() =>
            {
                var service = new OffsiteReplicationService(_offsite);
                var now = DateTime.UtcNow;
                foreach (var job in service.DueJobs(now))
                {
                    service.RunOnce(job);
                }
            });
        }
        catch
        {
            // 背景定時掃描失敗不應中斷主程式。
        }
    }

    /// <summary>排程報表寄送（§14.1 #9）：每 5 分鐘檢查一次是否到達設定時點並寄出。</summary>
    private async void RunReportMailDue()
    {
        if (_store is null || _exiting)
        {
            return;
        }

        try
        {
            await new ReportMailer(_store, _dataRoot).RunIfDueAsync(DateTime.UtcNow);
        }
        catch
        {
            // 背景排程失敗（含 SMTP 例外）不應中斷主程式。
        }
    }

    /// <summary>單一子視窗重用：同型視窗已開啟時僅置前並可重定向（focus），避免重複開啟/
    /// 連按快捷鍵堆疊多份視窗；關閉時自動釋放參考，下次開啟即重新建立。
    /// 特殊鍵（如 PTZ）可依 key 分隔多份實例。
    /// <para>
    /// 新建視窗在 <see cref="Window.Show"/>（Loaded 已跑完）後同樣套用一次 focus：多數呼叫端的
    /// 初始狀態由建構子帶入，focus 對它們是多餘的一次刷新；但事件中心未確認徽章／通知紀錄
    /// 雙擊沒有對應的建構子參數，狀態只能靠 focus 帶入——先前只在「已開啟」分支呼叫 focus，
    /// 導致第一次點徽章時 focus 被丟掉，畫面停在預設時間範圍，徽章指的未確認事件看不到也清不掉。
    /// </para>
    /// </summary>
    private void OpenChild(Window window, Action<Window>? focus = null, string? key = null)
    {
        var k = key ?? window.GetType().Name;
        if (_children.TryGetValue(k, out var existing) && existing is { IsVisible: true })
        {
            existing.Activate();
            focus?.Invoke(existing);
            return;
        }

        _children[k] = window;
        window.Owner = this;
        window.Closed += (_, _) =>
        {
            _children.Remove(k);

            // 子視窗可能剛匯入／升級授權（設定中心），關閉後重跑一次隱藏判斷與頁尾摘要。
            ApplyLicenseVisibility();
            RefreshLicenseFooter();

            // 子視窗（設定中心→頻道／攝影機管理）期間可能新增、刪除或修改了頻道；關閉後重載清單，
            // 有變動才重新連線，否則新頻道要重啟整個程式才會出現在監看區。
            _ = ReloadChannelsIfChangedAsync();

            // 子視窗（含設定中心）關閉後把主視窗帶回前景，否則全螢幕的監控中心可能退回其他視窗之後，
            // 操作員會找不到。離開程序時不動，避免干擾關閉流程。
            if (!_exiting)
            {
                Dispatcher.BeginInvoke(new Action(BringToFront), DispatcherPriority.Background);
            }
        };
        window.Show();
        focus?.Invoke(window);
    }

    private void OpenSettingsWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new SettingsWindow(
            _store!, _dataRoot, _ioHost, _shareHost, _manager, _patrolHost,
            channelsChanged: () => _ = ReloadChannelsIfChangedAsync(),
            recordingsRootChanged: root => _ = OnRecordingsRootChangedAsync(root),
            webHost: _webHost));
    }

    /// <summary>
    /// M305：NTP 對時。只有在設定中心「網路」頁勾選啟用時才會啟動；啟動時先對一次，
    /// 之後每 6 小時對一次（NTP 伺服器本身通常也是這樣建議的頻率）。
    /// </summary>
    private void StartNtpSync()
    {
        if (_store is null)
        {
            return;
        }

        var settings = new SettingsRepository(_store);
        if (settings.Get(NtpSync.EnabledKey) != "1")
        {
            return;
        }

        _ntpTimer = new DispatcherTimer(
            TimeSpan.FromHours(6),
            DispatcherPriority.Background,
            (_, _) => _ = RunNtpSyncAsync(),
            Dispatcher);
        _ntpTimer.Start();
        _ = RunNtpSyncAsync();
    }

    /// <summary>背景對時；失敗（網路不通等）只留給下次週期重試，不干擾 UI。</summary>
    private async Task RunNtpSyncAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await NtpSync.SyncAsync(new SettingsRepository(_store));
        }
        catch (Exception)
        {
            // 對時是盡力而為：例外不該讓主視窗的計時器執行緒出事。
        }
    }

    /// <summary>開啟匯出精靈（M21，§8.5/§14）。僅 admin（M42）。</summary>
    private void OpenExportWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new ExportWindow(_store!, _dataRoot));
    }

    private Canvas? CellOverlay(int cell) => cell is >= 0 and < MaxCells ? _cellOverlays[cell] : null;

    /// <summary>AI 偵測推播：更新各格疊加暫存，並將最佳目標推入警報列（規格 §1.4）。</summary>
    private void OnManagerAiDetections(object? _, (int Cell, int ChannelId, DetectionsFrame Frame) e)
    {
        var (cell, channelId, frame) = e;
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        // ChannelId 由事件直接帶上，訂閱者不必回頭讀 _cellChannel——
        // 那張表屬於 UI 執行緒，而本事件來自 AI 推論執行緒，讀它原本就是跨執行緒競爭。
        // 後續需要碰 UI 的部分（疊加重繪）才 marshal 過去。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _aiBoxes[cell] = frame.Items;

            // 疊加只在偵測結果改變時重畫：每幀重建整棵視覺樹（清 Children ＋ 每框新
            // Rectangle/TextBlock）在 16 格下是純浪費，而 AI 結果本身只每 200–800ms 變一次。
            BumpOverlayGeneration(cell);
            DrawLiveOverlay(cell);
        }), DispatcherPriority.Background);

        // M11：全量偵測 metadata 入佇列，由 DetectionWriter 批次寫入 detections 表
        if (_detWriter is { } writer && frame.Items.Count > 0)
        {
            writer.EnqueueRange(frame.Items.Select(d => new DetectionRecord
            {
                ChannelId = channelId,
                Class = d.Class,
                Confidence = d.Confidence,
                X = d.X,
                Y = d.Y,
                W = d.W,
                H = d.H,
                DetectedUtc = frame.SnapshotUtc,
            }));
        }

        // M52：分析情境（跨線／侵入／聚集）以中心點評估並寫入事件
        _analytics?.OnDetections(channelId, frame);

        Detection? best = null;
        foreach (var d in frame.Items)
        {
            if (!IsTargetClass(d.Class))
            {
                continue;
            }

            if (best is null || d.Confidence > best.Confidence)
            {
                best = d;
            }
        }

        if (best is not null)
        {
            Dispatcher.BeginInvoke(
                new Action(() => PushAlert(string.Format(Localizer.T("Main.AiAlert"), cell + 1, best.Class, best.Confidence), cell)),
                DispatcherPriority.Background);
        }
    }

    private void OnAiToggleChanged(object sender, RoutedEventArgs e)
    {
        // ai 旗標限定（M209）。手動切回關閉在未授權時是合法的降級行為，不需擋。
        if (AiToggle.IsChecked == true && !RequireFeature(LicenseFeatures.Ai))
        {
            AiToggle.IsChecked = false;
            return;
        }

        _aiVisible = AiToggle.IsChecked == true;

        // 原本只翻旗標就結束，要等下一幀才重畫——切回「關」時畫面會殘留偵測框最多一幀。
        // 而且疊加有世代快取，沒人遞增世代的話 DrawLiveOverlay 會直接跳過、不會重畫。
        BumpAllOverlayGenerations();
        for (var i = 0; i < MaxCells; i++)
        {
            DrawLiveOverlay(i);
        }
    }

    private void BumpAllOverlayGenerations()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            _overlayGen[i]++;
        }
    }

    /// <summary>
    /// 於該格畫面上繪製最後一次 AI 偵測框（單格附標籤；多格只畫框，規格 §2）。
    /// </summary>
    /// <remarks>
    /// 只有在<em>偵測結果或顯示縮放真的變了</em>時才重建視覺樹。原本每幀都
    /// <c>Children.Clear()</c> 再為每個框 new 一個 Rectangle/TextBlock，但 AI 結果
    /// 本身每 200–800ms 才更新一次——於是 15fps 下有 90% 以上的重畫完全浪費，
    /// 而且每次都讓 WPF 丟棄重建整棵疊加視覺樹。
    /// </remarks>
    private void DrawLiveOverlay(int cell)
    {
        var canvas = CellOverlay(cell);
        if (canvas is null)
        {
            return;
        }

        if (!_aiVisible)
        {
            if (_overlayGate.ShouldClear(cell, _overlayGen[cell]))
            {
                canvas.Children.Clear();
                canvas.Width = 0;
                canvas.Height = 0;
            }

            return;
        }

        var src = _bitmap[cell];
        var dets = _aiBoxes[cell];
        if (src is null || dets is null || dets.Count == 0)
        {
            if (_overlayGate.ShouldClear(cell, _overlayGen[cell]))
            {
                canvas.Children.Clear();
                canvas.Width = 0;
                canvas.Height = 0;
            }

            return;
        }

        var availW = Math.Max(0, src.PixelWidth);
        var availH = Math.Max(0, src.PixelHeight);
        var host = canvas.Parent is FrameworkElement f ? f : null;
        var w = host?.ActualWidth ?? availW;
        var h = host?.ActualHeight ?? availH;
        // 影像現在是 UniformToFill（填滿格子、必要時裁切），疊加框必須用同一套比例
        // （取較大者）才會貼在實際顯示的影像上；用 Min 會讓 4:3 來源的框整個偏掉。
        var scale = Math.Max(w / Math.Max(1, availW), h / Math.Max(1, availH));
        if (scale <= 0)
        {
            return;
        }

        // 內容與縮放都沒變就不重畫。縮放納入判斷是因為視窗大小改變時框線位置必須跟著縮放。
        if (!_overlayGate.ShouldRender(cell, _overlayGen[cell], scale))
        {
            return;
        }

        canvas.Children.Clear();
        canvas.Width = availW * scale;
        canvas.Height = availH * scale;

        var withLabels = WithOverlayLabels(LayoutCombo.SelectedIndex);
        foreach (var d in dets)
        {
            if (!IsTargetClass(d.Class))
            {
                continue;
            }

            var x = d.X * canvas.Width;
            var y = d.Y * canvas.Height;
            var bw = d.W * canvas.Width;
            var bh = d.H * canvas.Height;
            if (bw <= 0 || bh <= 0)
            {
                continue;
            }

            var brush = d.Class == "person" ? BrPerson : BrVehicle;
            var rect = new Rectangle
            {
                Width = bw,
                Height = bh,
                Stroke = brush,
                StrokeThickness = Math.Max(1.0, 2.0 / scale),
                Fill = BrBoxFill,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);

            if (withLabels)
            {
                var label = new TextBlock
                {
                    Text = $"{d.Class} {d.Confidence:0.00}",
                    FontSize = 11,
                    Foreground = Brushes.White,
                    Background = BrLabelBg,
                    Padding = new Thickness(3, 0, 3, 0),
                };
                var ly = y - 16 >= 0 ? y - 16 : y;
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, ly);
                canvas.Children.Add(label);
            }
        }
    }

    /// <summary>即時警報列：最多保留最近 3 筆；點擊跳至最後一筆對應格。</summary>
    private void PushAlert(string message, int? cell = null)
    {
        _alerts.Add($"{DateTime.Now:HH:mm:ss} {message}");
        _alertCells.Add(cell);
        while (_alerts.Count > 3)
        {
            _alerts.RemoveAt(0);
            _alertCells.RemoveAt(0);
        }

        AlertText.Text = string.Join("　·　", _alerts);
    }

    private void OnWallSequenceToggled(object sender, RoutedEventArgs e)
    {
        _wallSequenceEnabled = WallSequenceToggle.IsChecked == true;
        if (_wallSequenceTimer is not null)
        {
            if (_wallSequenceEnabled)
            {
                _wallSequenceIndex = 0;
                _wallSequenceTimer.Start();
            }
            else
            {
                _wallSequenceTimer.Stop();
            }
        }
    }

    private void OnAlertTextClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int? cell = null;
        for (var i = _alertCells.Count - 1; i >= 0; i--)
        {
            if (_alertCells[i] is int c)
            {
                cell = c;
                break;
            }
        }

        if (cell is int target && target < CurrentCellCount())
        {
            SelectCell(target);
            HintText.Text = string.Format(Localizer.T("Main.JumpedToCell"), target + 1);
        }
    }

    /// <summary>未確認警報數：查詢在背景執行緒跑，只把結果帶回 UI（<c>alarm_events</c> 是不分頁大表）。</summary>
    private void RefreshUnackBadge()
    {
        var count = _store is null ? 0 : new AlarmEventRepository(_store).CountUnacknowledged();
        Dispatcher.BeginInvoke(new Action(() => ApplyUnackCount(count)), DispatcherPriority.Background);
    }

    private void ApplyUnackCount(int count)
    {
        UnackBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnackText.Text = count > 0 ? string.Format(Localizer.T("Main.UnackCount"), count) : "";
    }

    /// <summary>
    /// 顯示通知待送筆數，並把「送不出去」變成畫面上看得見的事。
    /// </summary>
    /// <remarks>
    /// 通知失敗在現場的症狀是「保全沒有收到 alarms」——而 NVR 畫面一切正常。
    /// 沒有這個指標，唯一能發現的途徑是去翻 log 檔，等於沒有。
    /// 計數一律從 Background 執行緒讀，不碰 WPF 物件。
    /// </remarks>
    private void RefreshNotifyBacklogBadge()
    {
        var notify = _notify;
        if (notify is null)
        {
            return;
        }

        var pending = notify.PendingCount;
        var failed = notify.FailedCount;
        var planeProblem = NotificationPlaneProblem();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotifyBacklogBadge.Visibility = pending > 0 || failed > 0 || planeProblem is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            NotifyBacklogBadge.ToolTip =
                string.Format(Localizer.T("Main.NotifyTooltipFull"), pending, notify.DeliveredCount, failed, notify.SkippedDuringQuietCount);
            NotifyBacklogText.Text = planeProblem ?? (pending > 0
                ? string.Format(Localizer.T("Main.NotifyPending"), pending)
                : failed > 0 ? string.Format(Localizer.T("Main.NotifyFailed"), failed) : string.Empty);
        }), DispatcherPriority.Background);
    }

    /// <summary>手動催送：操作員按了不該再等排程的週期。</summary>
    private void OnNotifyBacklogClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _notify?.FlushNow();

        // FlushNow 只觸發非同步處理，這裡不假裝立刻送完；下一個 Activity 或
        // 排程週期會把真實結果反映到徽章上。
        RefreshNotifyBacklogBadge();
    }

    /// <summary>
    /// 該頻道最近一小時的實際錄影碼率；讀不到時回 null。
    /// </summary>
    /// <remarks>
    /// 診斷視窗是開在背景執行緒上的（選單來自解碼執行緒的續串），
    /// 這裡做同步 SQLite 查詢會阻塞該執行緒，所以用 null 讓視窗省略這一節，
    /// 而不是把查詢搬到 UI 執行緒上——那會讓點選選單時卡住。
    /// </remarks>
    private ObservedBitrate? TryObservedBitrate(int channelId)
    {
        if (_store is null)
        {
            return null;
        }

        try
        {
            var fromUtc = DateTime.UtcNow.AddHours(-1);
            var segments = new SegmentRepository(_store).ListByRange(channelId, "main", fromUtc, DateTime.UtcNow);
            var result = ObservedBitrateEstimator.ForChannel(channelId, segments);
            return result.SampleCount > 0 ? result : null;
        }
        catch
        {
            // 診斷資訊拿不到不該讓整個視窗開不起來。
            return null;
        }
    }

    /// <summary>
    /// 通知平面是否真的有外送通道。
    /// </summary>
    /// <remarks>
    /// 這是最安靜的一種失效：沒有 webhook、沒有 SMTP、沒有 MQTT，事件照樣
    /// 被寫進事件中心，畫面上警報正常跳動，而保全端永遠收不到任何東西。
    /// 系統不會報錯、log 也乾淨，操作員只有在一通沒接到的報警之後才會發現。
    /// </remarks>
    private string? NotificationPlaneProblem()
    {
        if (_store is null)
        {
            return null;
        }

        NotificationSettings cfg;
        try
        {
            cfg = NotificationSettings.Load(new SettingsRepository(_store));
        }
        catch
        {
            return null;
        }

        if (!cfg.Enabled)
        {
            return Localizer.T("Main.NotifyDisabled");
        }

        return cfg.AnyChannelConfigured ? null : Localizer.T("Main.NotifyNoChannel");
    }

    /// <summary>未確認徽章點擊＝直達事件中心並只顯示未確認事件（分診入口）。</summary>
    private void OnUnackBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenChild(new EventCenterWindow(_store!, alerts: _manager?.Alerts), w => ((EventCenterWindow)w).FocusUnacknowledged());
    }

    private async void OnCtxFullScreen(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            return;
        }

        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell >= MaxCells)
        {
            return;
        }

        ToggleExpandCell(cell);
    }

    /// <summary>
/// 單格展開 ↔ 原佈局往返（F11／右鍵共用）。<b>不再重連任何串流。</b>
/// </summary>
    /// <remarks>
    /// 原本這裡走 <c>_manager.ConnectAsync(...)</c>，而它第一行就是
    /// <c>DisconnectAllAsync()</c>：按一次 F11 會拆掉全部 RTSP 連線、逐一 ffprobe
    /// 再重啟 ffmpeg，16 路時是數十秒全黑——正是「按了 F11 畫面變全黑好幾十秒再回來」
    /// 這個抱怨的來源。現在只改派格位（<see cref="ChannelManager.ReconcileAsync"/>），
    /// RTSP 連線與解碼行程完全保留。
    /// </remarks>
    private void ToggleExpandCell(int cell)
    {
        if (_manager is null)
        {
            return;
        }

        var goSingle = LayoutCombo.SelectedIndex != 0;
        if (goSingle)
        {
            _preFullscreenLayout = LayoutCombo.SelectedIndex;
            _pendingSingleCell = cell;
            LayoutCombo.SelectedIndex = 0;
            _expandInProgress = true;
        }
        else
        {
            _pendingSingleCell = 0;
            LayoutCombo.SelectedIndex = _preFullscreenLayout is > 0 and <= MaxLayoutIndex ? _preFullscreenLayout : 1;
            _expandInProgress = false;
        }

        // 這裡刻意不自己呼叫 ReconcileAsync：設定 SelectedIndex 會同步觸發 OnLayoutChanged，
        // 由那裡統一對齊格位。兩邊都呼叫會讓兩個 reconcile 疊在一起 racing。
        UpdateFooter();
    }

    private static readonly System.Windows.Input.Key[] HandledPreviewKeys =
        [System.Windows.Input.Key.F11, System.Windows.Input.Key.Escape,
         System.Windows.Input.Key.F2, System.Windows.Input.Key.F4,
         System.Windows.Input.Key.F5, System.Windows.Input.Key.F6];

    /// <summary>F11＝單格展開/還原（ESC 退出展開，非展開時 ESC 退出全螢幕）；F2＝設定、F4＝回放、
    /// F5＝事件中心、F6＝警報管理器（配合 M194 單例重用，連按不重複開窗）。</summary>
    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!HandledPreviewKeys.Contains(e.Key))
        {
            return;
        }

        // 原本無條件 e.Handled = true：即使 _manager 為 null、F11 什麼也沒做，
        // 按鍵仍被吞掉，該快捷鍵對上層（子視窗、輸入框）也失效。
        // 只有真的會執行動作才標記已處理。
        if (e.Key == System.Windows.Input.Key.F11)
        {
            if (_manager is null)
            {
                return;
            }

            e.Handled = true;
            ToggleExpandCell(_selectedCell >= 0 ? _selectedCell : 0);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            // ESC 先退出「單格展開」；若不在展開狀態則退出全螢幕（滿版）回到可調整大小的視窗。
            if (_expandInProgress &&
                LayoutCombo.SelectedIndex == 0 &&
                _preFullscreenLayout is (> 0 and <= MaxLayoutIndex))
            {
                e.Handled = true;
                ToggleExpandCell(0);
            }
            else if (_isWindowFullScreen)
            {
                e.Handled = true;
                SetWindowFullScreen(false);
            }
        }
        else
        {
            e.Handled = true;
            if (e.Key == System.Windows.Input.Key.F2)
            {
                OpenSettingsWindow();
            }
            else if (e.Key == System.Windows.Input.Key.F4)
            {
                OnPlaybackClicked(this, new RoutedEventArgs());
            }
            else if (e.Key == System.Windows.Input.Key.F5)
            {
                OpenEventCenter();
            }
            else if (e.Key == System.Windows.Input.Key.F6)
            {
                OpenAlarmManagerWindow();
            }
        }
    }

    private async void OnCtxRecord(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            return;
        }

        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell < 0 || cell >= MaxCells || _cellChannel[cell] is not int channelId)
        {
            return;
        }

        var next = !_manager.IsRecording(channelId);
        var blocked = await _manager.SetRecordingAsync(channelId, next);
        if (blocked is not null)
        {
            ReportRecordingBlocked(blocked);
            return;
        }

        _licenseLimits?.Clear(channelId);
        RecBadge.Visibility = _manager.AnyRecording() ? Visibility.Visible : Visibility.Collapsed;
        // 提示要指向實際寫入的目錄：M311 起資料夾以頻道名稱命名，若仍顯示 ch{id}，
        // 操作員會到一個不存在（或舊的）目錄找檔案。與 SegmentRecorder 同一套命名規則。
        var folder = _store is null
            ? RecordingFolderNaming.LegacyName(channelId)
            : RecordingFolderNaming.ResolveFor(_store, channelId) ?? RecordingFolderNaming.LegacyName(channelId);
        HintText.Text = next
            ? string.Format(Localizer.T("Main.RecordingNow"), Path.Combine(_recordingsRoot, folder))
            : Localizer.T("Main.RecordingStopped");
        UpdateFooter();
    }

    private void OnCtxOpenEvents(object sender, RoutedEventArgs e) => OnEventClicked(sender, e);

    private int CtxChannelId(object sender)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        return cell >= 0 && cell < MaxCells && _cellChannel[cell] is int channelId ? channelId : -1;
    }

    private void OnCtxPlaybackChannel(object sender, RoutedEventArgs e)
    {
        var channelId = CtxChannelId(sender);
        if (channelId < 0)
        {
            return;
        }

        OpenChild(new PlaybackWindow(_store!, channelId), w => ((PlaybackWindow)w).FocusChannel(channelId));
    }

    private void OnCtxExportChannel(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            HintText.Text = Localizer.T("Main.AdminOnly");
            return;
        }

        var channelId = CtxChannelId(sender);
        if (channelId < 0)
        {
            return;
        }

        OpenChild(new ExportWindow(_store!, _dataRoot, channelId), w => ((ExportWindow)w).FocusChannel(channelId));
    }

    private void OnCtxEventsChannel(object sender, RoutedEventArgs e)
    {
        var channelId = CtxChannelId(sender);
        if (channelId < 0)
        {
            return;
        }

        OpenChild(new EventCenterWindow(_store!, channelId, _manager?.Alerts), w => ((EventCenterWindow)w).FocusChannel(channelId));
    }

    /// <summary>
    /// 對講：把一個音訊檔送到這一格攝影機的喇叭（§14.1 #11）。
    /// </summary>
    /// <remarks>
    /// 對講的實體在 <see cref="TalkService"/>：這裡只負責取得該頻道的 RTSP 位址、挑檔案、
    /// 啟動後等待播完，並把結果寫回 <see cref="HintText"/>。對講特別需要一個看得到的結果——
    /// 送出去卻沒聲音時，畫面上若毫無反應，操作員只會以為是自己按錯。
    /// </remarks>
    private async void OnCtxTalk(object sender, RoutedEventArgs e)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        var channelId = CtxChannelId(sender);
        if (channelId < 0)
        {
            return;
        }

        var url = _manager?.ChannelSessionFor(channelId)?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            HintText.Text = Localizer.T("Talk.NoStream");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Talk.PickTitle"),
            Filter = Localizer.T("Talk.FileFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var talkSession = new TalkService().Start(new TalkRequest(channelId, url, dialog.FileName));
            HintText.Text = string.Format(Localizer.T("Talk.Sending"), Path.GetFileName(dialog.FileName));
            var code = await talkSession.WaitAsync();
            HintText.Text = code == 0
                ? Localizer.T("Talk.Done")
                : string.Format(
                    Localizer.T("Talk.Failed"),
                    code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // 找不到供應商、ffmpeg 不在 PATH、裝置拒收都會走到這裡；不吞掉，讓操作員看得到原因。
            PushAlert(string.Format(Localizer.T("Talk.Failed"), ex.Message), cell);
        }
    }

    private void OnCtxPtz(object sender, RoutedEventArgs e)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell < 0 || cell >= MaxCells || _cellChannel[cell] is not int channelId)
        {
            return;
        }

        OpenPtz(channelId);
    }

    /// <summary>
    /// 把目前這一格的畫面存成 PNG。
    /// </summary>
    /// <remarks>
    /// 整個方案原本沒有任何「把畫面存成圖片」的程式路徑——事件快照只會在
    /// 運動／AI／防破壞真的觸發時產生，操作員想留一張「當時就是這樣」的證據
    /// 只能靠截圖程式。存的是 <see cref="WriteableBitmap"/>，也就是使用者在畫面上
    /// 實際看到的畫面，而不是原始解碼幀：兩者在下拉／裁切時並不相同，
    /// 而爭議時要的是前者。
    /// </remarks>
    private void OnCtxSnapshot(object sender, RoutedEventArgs e)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        if (_cellImages[cell]?.Source is not WriteableBitmap bmp)
        {
            PushAlert(Localizer.T("Snapshot.NoFrame"), cell);
            return;
        }

        try
        {
            var dir = Path.Combine(_dataRoot, "snapshots");
            Directory.CreateDirectory(dir);

            var channelId = _cellChannel[cell];
            var stem = $"ch{(channelId ?? cell + 1):000}-{DateTime.Now:yyyyMMdd-HHmmss}";
            var path = Path.Combine(dir, stem + ".png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(path);
            encoder.Save(stream);

            HintText.Text = string.Format(Localizer.T("Snapshot.Saved"), path);
            PushAlert(string.Format(Localizer.T("Snapshot.Saved"), Path.GetFileName(path)), cell);
        }
        catch (Exception ex)
        {
            // 磁碟滿、權限不足、快照資料夾被防毒鎖住都是現場會遇到的情況。
            PushAlert(string.Format(Localizer.T("Snapshot.SaveFailed"), ex.Message), cell);
        }
    }

    private void OnCtxDiagnostics(object sender, RoutedEventArgs e)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell is < 0 or >= MaxCells || _cellChannel[cell] is not int channelId)
        {
            return;
        }

        RecordingStallInfo? stall = _recordingStalls.TryGetValue(channelId, out var st) ? st : null;
        var session = _manager?.ChannelSessionFor(channelId);

        OpenChild(new ChannelDiagnosticsWindow(
            ChannelName(channelId),
            channelId,
            _cellStates[cell],
            session?.Url ?? string.Empty,
            session?.LastFrameUtc ?? DateTime.MinValue,
            _manager?.Diagnostic(channelId) ?? string.Empty,
            _manager?.ReconnectReason(channelId),
            stall,
            TryObservedBitrate(channelId)));
    }

    private void OnMapClicked(object sender, RoutedEventArgs e) => OpenMapWindow();

    /// <summary>開啟電子地圖視窗。gis 旗標限定（M209）。</summary>
    private void OpenMapWindow()
    {
        if (!RequireFeature(LicenseFeatures.Gis))
        {
            return;
        }

        OpenChild(new MapWindow(_store!));
    }

    /// <summary>開啟統圖報表視窗（M60，§14.7 #9 統計報表）。admin 限定。</summary>
    private void OpenReportsWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new ReportsWindow(_store!, _dataRoot));
    }

    private void OnReportsClicked(object sender, RoutedEventArgs e) => OpenReportsWindow();

    /// <summary>開啟稽核日誌視窗（M109，§14.1 資安治理）。admin 限定。</summary>
    private void OpenAuditWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new AuditLogWindow(_store!, _dataRoot));
    }

    private void OnAuditClicked(object sender, RoutedEventArgs e) => OpenAuditWindow();

    /// <summary>開啟跨來源法證檢索視窗（M97，§14.7 #7）。唯讀檢索，不限 admin。</summary>
    private void OpenSearchWindow() => OpenChild(new SearchWindow(_store!));

    private void OnSearchClicked(object sender, RoutedEventArgs e) => OpenSearchWindow();

    /// <summary>開啟系統健康視窗（M147，§14.7 #2）。唯讀量測，不限 admin。</summary>
    private void OpenSystemHealthWindow() => OpenChild(new SystemHealthWindow());

    private void OnSystemHealthClicked(object sender, RoutedEventArgs e) => OpenSystemHealthWindow();

    /// <summary>開啟智慧牆視窗（M105/M106，§14.7 #14）：版面編輯與警報牆快照。</summary>
    private void OpenSmartwallWindow() => OpenChild(new SmartwallWindow(_store!));

    private void OnSmartwallClicked(object sender, RoutedEventArgs e) => OpenSmartwallWindow();

    /// <summary>開啟 POS 交易視窗（M93/M104/M113，§14.7 #8）：匯入、查詢與對帳。</summary>
    private void OpenPosWindow() => OpenChild(new PosWindow(_store!));

    private void OnPosClicked(object sender, RoutedEventArgs e) => OpenPosWindow();

    /// <summary>開啟邊緣補抓視窗（M94，§14.7 #10）：SD 網址、建立與執行補抓、回灌為區段。</summary>
    private void OpenEdgeBackfillWindow() => OpenChild(new EdgeBackfillWindow(_store!, ResolveDataRoot()));

    private void OnEdgeBackfillClicked(object sender, RoutedEventArgs e) => OpenEdgeBackfillWindow();

    /// <summary>開啟複合事件規則視窗（M62，§5.10）。admin 限定。</summary>
    private void OpenRulesWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new RulesWindow(_store!));
    }

    private void OnRulesClicked(object sender, RoutedEventArgs e) => OpenRulesWindow();

    /// <summary>開啟影片摘要視窗（M63，§5.9）。admin 限定。</summary>
    private void OpenSynopsisWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new SynopsisWindow(_store!, _dataRoot));
    }

    private void OnSynopsisClicked(object sender, RoutedEventArgs e) => OpenSynopsisWindow();

    /// <summary>開啟保存鎖定視窗（M66，§14.1 #13）。admin 限定。</summary>
    private void OpenLegalHoldWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        OpenChild(new LegalHoldWindow(_store!, allowRevoke: SessionContext.IsAdmin));
    }

    private void OnLegalHoldClicked(object sender, RoutedEventArgs e) => OpenLegalHoldWindow();

    /// <summary>開啟巡航排程視窗（M72，§47）。schedule 旗標限定（M218，與 /api/patrols 閘門一致）。</summary>
    private void OpenPatrolWindow()
    {
        if (!RequireAdmin() || !RequireFeature(LicenseFeatures.Schedule))
        {
            return;
        }

        OpenChild(new PatrolWindow(_store!, _patrolHost));
    }

    private void OnPatrolClicked(object sender, RoutedEventArgs e) => OpenPatrolWindow();

    /// <summary>巡邏失敗：走同一套警示路徑，操作員不必開啟巡航視窗才知道巡邏沒在動。</summary>
    private void OnPatrolFailed(object? sender, int channelId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var cell = GetCell(channelId);
            if (cell >= 0)
            {
                PushAlert(string.Format(Localizer.T("Main.PatrolFailed"), ChannelName(channelId)), cell);
            }
        }));
    }

    /// <summary>開啟感測器 IO 測試視窗（M75，§14.1 #16）。</summary>
    /// <summary>開啟感測 IO 視窗。gis 旗標限定（M209）。</summary>
    private void OpenIoWindow()
    {
        if (!RequireFeature(LicenseFeatures.Gis))
        {
            return;
        }

        OpenChild(new IoWindow(_store!));
    }

    private void OnIoClicked(object sender, RoutedEventArgs e) => OpenIoWindow();

    /// <summary>開啟雙碼流切流調整視窗（M77，§15.2）。</summary>
    private void OpenStreamSwitchWindow()
    {
        OpenChild(new StreamSwitchWindow(_store!, _manager));
    }

    private void OnStreamClicked(object sender, RoutedEventArgs e) => OpenStreamSwitchWindow();

    /// <summary>
    /// 開啟音訊感測測試視窗（M79，§5.8 L1）。
    ///
    /// 音訊事件屬事件 AI（§19.4 入口對應），遠端 <c>/api/audio</c> 由閘門擋 <c>ai</c>；
    /// 只擋管理員會讓遠端被擋、本機可繞，等於沒有合併檢查。
    /// </summary>
    private void OpenAudioWindow()
    {
        if (!RequireAdmin() || !RequireFeature(LicenseFeatures.Ai))
        {
            return;
        }

        OpenChild(new AudioWindow(_store!));
    }

    private void OnAudioClicked(object sender, RoutedEventArgs e) => OpenAudioWindow();

    /// <summary>開啟 Failover 容錯測試/監控視窗（M88，§14.7 #9）。</summary>
    private void OpenFailoverWindow()
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenChild(new FailoverWindow(_store!));
    }

    private void OnFailoverClicked(object sender, RoutedEventArgs e) => OpenFailoverWindow();

    private void OpenPtz(int channelId)
    {
        if (!RequireAdmin())
        {
            return;
        }

        if (_store is not SqliteStore store)
        {
            return;
        }

        var channel = _channelList.FirstOrDefault(c => c.Id == channelId);
        if (channel is null)
        {
            return;
        }

        if (channel.DeviceId is not int deviceId)
        {
            MessageBox.Show(this, Localizer.T("Main.PtzNotBound"), Localizer.T("Main.PtzTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = new DeviceRepository(store, new AuditLogRepository(store)).Get(deviceId);
        if (device is null)
        {
            MessageBox.Show(this, Localizer.T("Main.PtzDeviceMissing"), Localizer.T("Main.PtzTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenChild(new PtzWindow(store, channel, device), null, $"ptz-{channelId}");
    }

    private static bool IsTargetClass(string cls) =>
        cls is "person" or "car" or "bus" or "truck" or "motorcycle" or "bicycle";

    private Image? CellImage(int cell) => cell is >= 0 and < MaxCells ? _cellImages[cell] : null;

    private TextBlock? CellText(int cell) => cell is >= 0 and < MaxCells ? _cellTexts[cell] : null;

    /// <summary>
    /// 目前沒有即時影格時，依格位狀態顯示佔位圖：未指派頻道＝<c>Disable.png</c>，已指派但斷線＝<c>Invisible.png</c>。
    /// </summary>
    /// <remarks>
    /// 監看牆上「空格」與「斷線」必須一眼看得出來。兩者原本都是黑底加「未連線」文字，
    /// 操作員分不出「這格還沒配頻道」和「配了但攝影機掛了」，得逐格讀文字才知道。
    /// 有即時影格時（見 <see cref="_bitmap"/>）不覆蓋，交由幀路徑顯示。
    /// </remarks>
    private void ShowCellPlaceholder(int cell)
    {
        if (CellImage(cell) is not { } img)
        {
            return;
        }

        if (_bitmap[cell] is { } live)
        {
            img.Stretch = Stretch.UniformToFill;
            img.Source = live;
            return;
        }

        // 佔位圖是圖示不是畫面，用 Uniform 才不會被拉寬變形。
        img.Stretch = Stretch.Uniform;
        img.Source = _cellChannel[cell] is null ? NoChannelImage() : DisconnectedImage();
    }

    private BitmapSource? NoChannelImage() => _cellNoChannelImage ??= LoadPlaceholder("Disable.png");

    private BitmapSource? DisconnectedImage() => _cellDisconnectedImage ??= LoadPlaceholder("Invisible.png");

    /// <summary>
    /// 載入佔位圖；檔案缺失不能讓整個監看區掛掉，故缺檔時回 <c>null</c>（該格只留底色）。
    /// </summary>
    private static BitmapSource? LoadPlaceholder(string fileName)
    {
        try
        {
            return CreateBitmap(fileName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SetCellSource(int cell, BitmapSource? source)
    {
        if (CellImage(cell) is not { } img)
        {
            return;
        }

        // 即時影格要填滿格位（不變形裁切），佔位圖則維持原比例（見 ShowCellPlaceholder）。
        if (source is not null)
        {
            img.Stretch = Stretch.UniformToFill;
        }

        img.Source = source;
    }

    private void SetCellStatus(int cell, string text, SolidColorBrush? brush)
    {
        if (CellText(cell) is not { } block)
        {
            return;
        }

        block.Text = text;
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (brush is not null)
        {
            block.Foreground = brush;
        }
    }

    /// <summary>每秒刷新每格徽章、AI 策略與狀態文字（M12）。</summary>
    /// <remarks>
    /// 側欄「頻道總覽」已移除；這裡只保留每格本身的刷新。徽章顯示頻道名與錄影狀態／
    /// 未錄原因，而狀態文字不能只在幀事件時更新——串流卡住時不會再送新影格，文字會凍結在
    /// 最後一幀的數字，正好在最需要看出「畫面愈拖愈舊」時沒有變化。
    /// </remarks>
    private void UpdateCellStatus()
    {
        UpdateClock();
        UpdateBadges();
        UpdateAiPolicy();
        UpdateCellMetrics();
    }

    /// <summary>頁尾狀態列的目前日期時間時鐘（本機時間，每秒更新）。</summary>
    /// <remarks>
    /// 操作員對照即時畫面與事件時間時需要一個可信的當下時間；監看畫面本身沒有 OSD，
    /// 事件列也只有分秒，看不到日期。用本機時間（<see cref="DateTime.Now"/>）而非 UTC，
    /// 因為畫面上的事件列、錄影檔名同樣是本機時間，兩者必須一致才對得起來。
    /// </remarks>
    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 每秒更新各格的 fps／畫面年齡文字。
    /// </summary>
    /// <remarks>
    /// 必須獨立於幀事件定時刷新：串流卡住時不會再送新影格，若只在幀事件時更新，
    /// 文字就會停在「最後一張影格當下」的數字——正好在畫面開始愈拖愈舊的時候
    /// 看不到任何變化，指示器等於白做。
    /// </remarks>
    private void UpdateCellMetrics()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellStates[i] == RtspState.Streaming)
            {
                RenderCellStatusText(i);
            }
        }
    }

    /// <summary>
    /// 該頻道「不該錄」的白話原因；沒有阻擋時回 null（讓呼叫端顯示一般狀態）。
    /// </summary>
    /// <remarks>
    /// 存在的理由是那種最難查的失效：操作員設定頁勾了「全時」，
    /// 但磁碟上沒有檔案、log 也乾淨、沒有任何錯誤。沒有這個原因顯示，
    /// 唯一能確認的方法是讀程式碼。
    /// </remarks>
    private static string? BlockLabel(RecordingBlockReason? block) => block switch
    {
        null => null,
        RecordingBlockReason.ChannelDisabled => Localizer.T("Main.BlockChannelDisabled"),
        RecordingBlockReason.NoStreamUrl => Localizer.T("Main.BlockNoStreamUrl"),
        RecordingBlockReason.ManualOnly => Localizer.T("Main.BlockManualOnly"),
        RecordingBlockReason.EventOnly => Localizer.T("Main.BlockEventOnly"),
        RecordingBlockReason.OutsideSchedule => Localizer.T("Main.BlockOutsideSchedule"),
        _ => null,
    };

    /// <summary>選定監看格：高亮邊框。</summary>
    private void SelectCell(int cell)
    {
        _selectedCell = cell;
        PaintCellBorders();
    }

    /// <summary>監看格左鍵按下：記下來源格並取得滑鼠捕捉，之後才收得到放開位置。</summary>
    /// <remarks>單分割且已數位放大時改為拖曳平移畫面：此時左鍵不放開攝影機順序（見 <see cref="CanPanCell"/>）。</remarks>
    private void OnCellMouseDown(int cell, UIElement element, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (CanPanCell(cell) && _zoomLayer?.Parent is UIElement host)
        {
            _panning = true;
            _panMoved = false;
            _panOrigin = e.GetPosition(host);
            _panStart = _panOrigin;
            element.CaptureMouse();
            e.Handled = true;
            return;
        }

        _dragSourceCell = cell;
        _dragStart = e.GetPosition(CellGrid);
        _dragged = false;

        // 不捕捉的話，滑鼠移到別格放開時放開事件會送給那一格，來源格永遠收不到，拖曳形同無效。
        element.CaptureMouse();
    }

    /// <summary>是否處於「單分割且已放大」狀態；此時左鍵是平移畫面而非拖曳換位。</summary>
    private bool CanPanCell(int cell)
        => CurrentCellCount() == 1 && cell == 0 && _cellZoom > 1.0001 && _zoomLayer is not null;

    /// <summary>監看格滑鼠移動：放大時平移畫面；否則越過系統拖曳門檻才進入拖曳換位。</summary>
    private void OnCellMouseMove(int cell, UIElement element, MouseEventArgs e)
    {
        if (_panning)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _zoomLayer?.Parent is not UIElement host)
            {
                return;
            }

            var cur = e.GetPosition(host);
            // 用系統拖曳門檻濾掉手震：沒真正帶動就不算平移，放開時才會當成單純點擊。
            if (!_panMoved &&
                (Math.Abs(cur.X - _panOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(cur.Y - _panOrigin.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                _panMoved = true;
            }

            if (_panMoved)
            {
                _zoomPanX += cur.X - _panStart.X;
                _zoomPanY += cur.Y - _panStart.Y;
                ApplyCellZoom(_zoomLayer);
            }

            _panStart = cur;
            e.Handled = true;
            return;
        }

        if (_dragSourceCell != cell || e.LeftButton != MouseButtonState.Pressed || _dragged)
        {
            return;
        }

        var p = e.GetPosition(CellGrid);
        if (Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            // Ctrl+拖曳＝把這台帶去回放視窗（§8.5 拖入分屏立即回放）。
            // 不與一般拖曳共用同一手勢：換位是在放開時結算的，中途改走 OLE 拖放會把那段結算整段吃掉，
            // 等於用新功能換掉一個每天在用的舊功能。
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && _cellChannel[cell] is int dragChannelId)
            {
                _dragSourceCell = -1;
                _dragged = false;
                element.Opacity = 1.0;
                element.ReleaseMouseCapture();

                var data = new DataObject();
                data.SetData(DragFormats.ChannelId, dragChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);
                return;
            }

            _dragged = true;
            // 拖曳中把來源格調淡，操作員才看得出正在搬的是哪一格。
            element.Opacity = 0.55;
        }
    }

    /// <summary>監看格左鍵放開：單純點擊走 <see cref="OnCellClicked"/>；拖曳則按放開所在的格位調整順序。</summary>
    private void OnCellMouseUp(int cell, UIElement element, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            element.ReleaseMouseCapture();
            if (!_panMoved)
            {
                // 沒有實際拖動＝單純點擊（保留原本的選取／再點還原佈局）。
                OnCellClicked(cell);
            }

            return;
        }

        if (_dragSourceCell != cell)
        {
            return;
        }

        var wasDragged = _dragged;
        _dragSourceCell = -1;
        _dragged = false;
        element.Opacity = 1.0;
        element.ReleaseMouseCapture();

        if (!wasDragged)
        {
            OnCellClicked(cell);
            return;
        }

        var target = CellAt(e.GetPosition(CellGrid));
        if (target >= 0 && target != cell)
        {
            ReorderCells(cell, target);
        }
    }

    /// <summary>
    /// 單分割時對攝影機畫面滾輪＝數位縮放：以滑鼠位置為中心放大（1x～8x），影像與 AI 疊加一起縮放。
    /// </summary>
    /// <remarks>
    /// 只有單格佈局支援；多分割時直接放行，不宣告 handled，避免佔用事件而擋掉未來可能的用途。
    /// 縮放錨點用「該元件自身座標系」的游標位置：<see cref="UIElement.RenderTransform"/> 之前的座標。
    /// 平移量要一起修正，游標底下那一點才會固定不動（連續滾動才不會越縮越偏）。
    /// </remarks>
    private void OnCellMouseWheel(int cell, UIElement layer, MouseWheelEventArgs e)
    {
        // 單分割＝格數 1（索引 0）；其他佈局不縮放。
        if (CurrentCellCount() != 1 || cell != 0 || _cellChannel[cell] is null)
        {
            return;
        }

        const double step = 1.15;
        var desired = Math.Clamp(_cellZoom * (e.Delta > 0 ? step : 1.0 / step), 1.0, 8.0);
        e.Handled = true;
        if (Math.Abs(desired - _cellZoom) < 0.001)
        {
            return;
        }

        // 畫面座標 Q = 倍率 s × 圖層座標 p + 平移 t。要讓游標底下的 p 縮放後仍對到同一個螢幕點，
        // 必須把平移補成 t' = t + (s - s')·p，否則縮放會往圖層左上角竄。
        if (layer is FrameworkElement { Parent: UIElement })
        {
            var p = e.GetPosition(layer);
            _zoomPanX += (_cellZoom - desired) * p.X;
            _zoomPanY += (_cellZoom - desired) * p.Y;
        }

        _cellZoom = desired;
        ApplyCellZoom(layer);
    }

    /// <summary>把目前的縮放倍率與平移量套到單分割圖層；倍率回 1 時清除變形。</summary>
    /// <remarks>平移量會被限制在讓畫面仍填滿格子的範圍內，免得把影像拖出去留下黑邊。</remarks>
    private void ApplyCellZoom(UIElement layer)
    {
        if (layer is not FrameworkElement fe)
        {
            return;
        }

        if (_cellZoom <= 1.0001)
        {
            _cellZoom = 1.0;
            _zoomPanX = 0;
            _zoomPanY = 0;
            layer.RenderTransform = null;
            return;
        }

        _zoomPanX = Math.Clamp(_zoomPanX, fe.ActualWidth * (1 - _cellZoom), 0);
        _zoomPanY = Math.Clamp(_zoomPanY, fe.ActualHeight * (1 - _cellZoom), 0);

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(_cellZoom, _cellZoom));
        group.Children.Add(new TranslateTransform(_zoomPanX, _zoomPanY));
        layer.RenderTransform = group;
    }

    /// <summary>滑鼠所在位置（<see cref="CellGrid"/> 座標）對應的監看格；不在任何格子上回 -1。</summary>
    private int CellAt(Point pointInGrid)
    {
        for (var i = 0; i < CurrentCellCount(); i++)
        {
            var border = _cellBorders[i];
            if (border is null)
            {
                continue;
            }

            // 點是 CellGrid 座標，先把每格的左上角換算到同一座標系再比對，免得列×欄排列下算錯格。
            var topLeft = border.TranslatePoint(new Point(0, 0), CellGrid);
            if (pointInGrid.X >= topLeft.X && pointInGrid.Y >= topLeft.Y &&
                pointInGrid.X <= topLeft.X + border.ActualWidth &&
                pointInGrid.Y <= topLeft.Y + border.ActualHeight)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 把第 <paramref name="from"/> 格的攝影機搬到第 <paramref name="to"/> 格（其餘格遞補），並寫回資料庫。
    /// </summary>
    /// <remarks>
    /// 監看格＝頻道在清單中的索引（見 <see cref="ChannelManager.ReconcileAsync"/>），所以調順序＝重排後
    /// 把每台釘到它落腳的格位（寫入 display_cell）。全部釘住而非只改兩台，是因為未指定格位的頻道
    /// 位置是推導出來的；不釘住的話下次有頻道異動時順序會整個跑掉。
    /// 拖曳後不重連：<see cref="ChannelManager.ReconcileAsync"/> 只改派既有 session 的格位，
    /// 串流與錄影不受影響（<c>ConnectAsync</c> 會先拆再建，等於中斷錄影）。
    /// </remarks>
    private void ReorderCells(int from, int to)
    {
        var wall = WallChannels();
        if (_channels is null || wall.Count == 0)
        {
            return;
        }

        var count = CurrentCellCount();
        if (from < 0 || to < 0 || from >= count || to >= count ||
            from >= wall.Count || to >= wall.Count || from == to)
        {
            return;   // 落點在空格上、越界或沒真的動：不搬。
        }

        // 群組頁簽：拖曳改的是「群組內」的順序，不動全域的 display_cell 與頻道名稱。
        if (_activeGroupId is long groupId && _groups is not null)
        {
            _groups.Move(groupId, wall[from].Id, to - from);
            HintText.Text = Localizer.T("Main.CellOrderChanged");
            _ = ApplyWallAsync();
            return;
        }

        // 調整前先留快照，「復原」才有上一個排序可回。
        _cellOrderHistory.Push(CaptureCellOrder());
        UpdateUndoCellOrderButton();

        var order = ChannelLayout.Move(_channelList, from, to);
        for (var k = 0; k < order.Count; k++)
        {
            if (order[k].DisplayCell != k)
            {
                UpdateChannelDisplayCell(order[k], k);
            }
        }

        // 重讀清單：名稱屬於攝影機，拖曳時名稱跟著攝影機搬到新格位；
        // 這裡只會替「仍沿用裝置名稱」的頻道補格位預設名，既有名稱不動。
        RefreshChannelList();
        HintText.Text = Localizer.T("Main.CellOrderChanged");
        _ = ApplyCellOrderAsync();
    }

    /// <summary>依目前清單順序把既有工作階段改派到對應格位（不重連、不中斷錄影）。</summary>
    private async Task ApplyCellOrderAsync()
    {
        if (_manager is null || !_monitoring)
        {
            return;
        }

        await _reconcileGate.WaitAsync();
        try
        {
            await _manager.ReconcileAsync(WallChannels(), 0, CurrentCellCount());
        }
        finally
        {
            _reconcileGate.Release();
        }

        UpdateBadges();
        UpdateFooter();
    }

    private async void AdvanceWallSequence()
    {
        if (!_wallSequenceEnabled || _manager is null)
        {
            return;
        }

        var wall = WallChannels();
        if (wall.Count == 0)
        {
            return;
        }

        var cellCount = Math.Max(1, CurrentCellCount());
        _wallSequenceIndex = (_wallSequenceIndex + cellCount) % wall.Count;

        await _reconcileGate.WaitAsync();
        try
        {
            await _manager.ReconcileAsync(wall, _wallSequenceIndex, cellCount);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>目前每台頻道的格位對應（依監看順序），供「復原」回到調整前的排序。</summary>
    /// <remarks>
    /// 取 <c>_channelList</c>（已依格位排好）而非資料庫原順序：復原要還原的是「操作員看到的排序」，
    /// 連同 null（未指定格位）一起記下，還原時才能精確回到調整前的格位指派。
    /// </remarks>
    private List<(int Id, int? Cell)> CaptureCellOrder()
        => _channelList.Select(c => (c.Id, c.DisplayCell)).ToList();

    /// <summary>依堆疊是否有紀錄決定「復原」項目可否選取；沒有可還原的排序時該項目停用。</summary>
    private void UpdateUndoCellOrderButton()
        => LayoutItemUndo.IsEnabled = _cellOrderHistory.Count > 0;

    /// <summary>「復原」：退回上一次拖曳調整前的攝影機排序（可連續選，依序退到最初的排序）。</summary>
    /// <remarks>
    /// 把快照裡的格位寫回資料庫、重讀清單，再以 <see cref="ApplyCellOrderAsync"/> 改派既有工作階段
    /// （不重連、不中斷錄影）。已被刪除的頻道直接略過；快照後才新增的頻道維持現狀。
    /// 由佈局下拉選到「復原」項目時呼叫（見 <see cref="OnLayoutChanged"/>）。
    /// </remarks>
    private void UndoCellOrder()
    {
        if (_channels is null || _cellOrderHistory.Count == 0)
        {
            return;
        }

        // 「復原」還原的是全域格位排序；若目前在某個群組頁簽，先切回「全部」再還原，
        // 否則改派的會是群組成員、與快照（全體）不一致。
        if (_activeGroupId is not null)
        {
            _layoutPrefs.Remember(_activeGroupId, CurrentCellCount());
            _activeGroupId = null;
            ReloadGroupTabs();

            // 切回「全部」也要換回該頁簽的格數，否則會用著群組的格數還原全域排序。
            var allIndex = LayoutIndexForCells(TabCellCount(null));
            if (LayoutCombo.SelectedIndex != allIndex)
            {
                LayoutCombo.SelectedIndex = allIndex;
            }
        }

        var snapshot = _cellOrderHistory.Pop();

        // 以 Id 對回現在的頻道實例：清單可能已被 RefreshChannelList 換成新的物件。
        var current = _channelList.ToDictionary(c => c.Id);
        foreach (var (id, cell) in snapshot)
        {
            if (current.TryGetValue(id, out var channel) && channel.DisplayCell != cell)
            {
                UpdateChannelDisplayCell(channel, cell);
            }
        }

        RefreshChannelList();
        UpdateUndoCellOrderButton();
        HintText.Text = Localizer.T("Main.CellOrderUndone");
        _ = ApplyCellOrderAsync();
    }


    /// <remarks>
    /// 只在「左鍵展開中」（<see cref="_expandInProgress"/>）才把再點一次當成還原：
    /// 若單分割是操作員自己用佈局選單選的（本來就只想看 1×1），點畫面不該跳回多分割。
    /// </remarks>
    private void OnCellClicked(int cell)
    {
        SelectCell(cell);

        if (_manager is null)
        {
            return;
        }

        if (LayoutCombo.SelectedIndex == 0 && _expandInProgress)
        {
            ToggleExpandCell(0);
        }
        else if (CurrentCellCount() > 1)
        {
            ToggleExpandCell(cell);
        }
    }

    /// <summary>
    /// 依「選取」與「運動閃框」兩種狀態上色各格邊框。
    /// </summary>
    /// <remarks>
    /// 原本這段邏輯寫在 <c>SelectCell</c> 裡，而運動閃框需要的正是同一段上色。
    /// 分開會有兩份容易不一致的邊框繪製；集中在這裡後，選取與運動可以疊加顯示。
    /// </remarks>
    private void PaintCellBorders()
    {
        var now = DateTime.UtcNow;
        var count = CurrentCellCount();
        var anyFlash = false;

        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellBorders[i] is not { } border)
            {
                continue;
            }

            var inLayout = i < count;
            var selected = inLayout && i == _selectedCell;
            var flashing = inLayout && now < _motionFlashUntil[i];
            anyFlash |= flashing;

            border.BorderBrush = selected ? BrHighlight : flashing ? BrMotion : BrCellEdge;
            border.BorderThickness = new Thickness(selected || flashing ? 2 : 1);
        }

        // 沒有任何閃框就停掉計時器，不要為了可能在幾百毫秒後才發生的運動而空轉。
        if (!anyFlash)
        {
            _motionTimer?.Stop();
        }
    }

    /// <summary>運動觸發：該格邊框閃一下提醒操作員（運動偵測原本只有事件寫檔，畫面上無任何回饋）。</summary>
    private void OnMotionSignal(int cell, double ratio)
    {
        // 引擎端已限定只有啟用運動偵測的頻道會發這個事件，這裡不必再擋。
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        // 事件來自解碼執行緒，邊框必須在 UI 執行緒改。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _motionFlashUntil[cell] = DateTime.UtcNow.AddMilliseconds(700);

            if (_motionTimer is null)
            {
                _motionTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(150),
                    DispatcherPriority.Render,
                    (_, _) => PaintCellBorders(),
                    Dispatcher);
            }

            _motionTimer.Start();
            PaintCellBorders();
        }), DispatcherPriority.Render);
    }

    /// <summary>每格左下角頻道名徽章（含錄影指示）。</summary>
    private void UpdateBadges()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            var badge = _cellBadges[i];
            if (badge is null)
            {
                continue;
            }

            var ch = _cellChannel[i];
            var text = string.Empty;
            if (ch is int id)
            {
                var recording = _manager?.IsRecording(id) == true;
                text = ChannelName(id) + (recording ? " ●REC" : string.Empty);

                // 非錄影狀態時把「為什麼沒錄」直接印在格框徽章上。設定頁勾了全時
                // 卻沒有錄影，是那種沒有任何錯誤訊息的失效；讓原因長在畫面上，
                // 比讓操作員去猜省下的是一整趟現場查線。
                if (!recording)
                {
                    var why = BlockLabel(_scheduler?.BlockReasonFor(id));
                    if (why is not null)
                    {
                        text += $"（{why}）";
                    }
                }
            }
            badge.Text = text;
            badge.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string ChannelName(int channelId)
    {
        if (_channelNames is null)
        {
            // 快取隨頻道清單重建而失效（RefreshChannelList 設 _channelNames = null）。
            // ChannelName 每秒被呼叫 32 次（UpdateCellStatus 16 次＋ UpdateBadges 16 次），
            // 原本每次都線性掃描整個 _channelList。
            _channelNames = _channelList.ToDictionary(c => c.Id, c => c.Name);
        }

        return _channelNames.TryGetValue(channelId, out var name) ? name : $"#{channelId}";
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _unackTimer?.Dispose();
        _notifyBacklogTimer?.Dispose();
        _uiTimer?.Dispose();
        _motionTimer?.Stop();
        _reportMailTimer?.Stop();
        _patrolHost?.Dispose();
        _patrolHost = null;
        var scheduler = _scheduler;
        _scheduler = null;
        if (scheduler is not null)
        {
            await scheduler.StopAllAsync();
            scheduler.Dispose();
        }

        _recordingOwnership?.Release(DateTime.UtcNow);
        _recordingOwnership = null;

        await DisconnectAllAsync();
        _bgCts?.Cancel();
        _bgCts?.Dispose();
        _detWriter?.Dispose();
        _ioHost?.Dispose();
        _shareHost?.Dispose();
        _webHost?.Dispose();
        _ntpTimer?.Stop();
        _notify?.Dispose();
        _manager?.Dispose();
        _store?.Dispose();
    }
}