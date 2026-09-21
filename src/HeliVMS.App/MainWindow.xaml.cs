using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Alarms;
using HeliVMS.App.Services;
using HeliVMS.Licensing;
using HeliVMS.Media;
using HeliVMS.Recording;
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
    internal const string DefaultDataRoot = @"C:\HeliVMSData";
    private const int MaxCells = 16;
    private const string UiSettingsFile = "ui.json";

    private readonly string _dataRoot;
    private readonly string _legacyDir;
    private SqliteStore? _store;
    private ChannelRepository? _channels;
    private SegmentRepository? _segRepo;
    private ChannelManager? _manager;
    private CancellationTokenSource? _bgCts;
    private WriteableBitmap?[] _bitmap = new WriteableBitmap?[MaxCells];
    private readonly int?[] _cellChannel = new int?[MaxCells];
    private readonly Image[] _cellImages = new Image[MaxCells];
    private readonly Canvas[] _cellOverlays = new Canvas[MaxCells];
    private readonly TextBlock[] _cellTexts = new TextBlock[MaxCells];
    private readonly bool[] _cellHighlights = new bool[MaxCells];
    private readonly TextBlock[] _cellBadges = new TextBlock[MaxCells];
    private readonly Border[] _cellBorders = new Border[MaxCells];
    private readonly Dictionary<int, RtspState> _channelStates = new();
    private readonly Dictionary<int, DateTime> _channelLastAi = new();
    private readonly Dictionary<int, DateTime> _channelLastFrame = new();
    private readonly List<OverviewRow> _overviewRows = [];
    private readonly Dictionary<int, (bool Enabled, int Interval)> _aiPolicyCache = new();
    private int _selectedCell = -1;
    private IReadOnlyList<ChannelInfo> _channelList = [];
    private string _footerBase = string.Empty;
    private int _preFullscreenLayout = 1;
    private RecordingScheduler? _scheduler;
    private DetectionWriter? _detWriter;
    private NotificationService? _notify;
    private TrayIconHost? _tray;
    private IoMonitorHost? _ioHost;
    private ShareHost? _shareHost;
    private AnalyticsEventEngine? _analytics;
    private AlertRuleRepository? _alertRules;
    private AlarmEventRepository? _alarmEvents;
    private int _smartAlertSuppressed;
    private OffsiteReplicationRepository? _offsite;
    private DispatcherTimer? _offsiteTimer;
    private bool _exiting;

    private static readonly SolidColorBrush BrOffline = new(Color.FromRgb(0x6B, 0x7B, 0x90));
    private static readonly SolidColorBrush BrConnecting = new(Color.FromRgb(0xD8, 0xA1, 0x2C));
    private static readonly SolidColorBrush BrLive = new(Color.FromRgb(0x56, 0xC8, 0x86));
    private static readonly SolidColorBrush BrPerson = new(Color.FromRgb(0xFF, 0x63, 0x47));
    private static readonly SolidColorBrush BrVehicle = new(Color.FromRgb(0x00, 0xB6, 0xFF));
    private static readonly SolidColorBrush BrCellEdge = new(Color.FromRgb(0x1F, 0x3A, 0x5F));
    private static readonly SolidColorBrush BrHighlight = new(Color.FromRgb(0x4F, 0xC3, 0xF7));

    private bool _aiVisible;
    private readonly IReadOnlyList<Detection>[] _aiBoxes = new IReadOnlyList<Detection>[MaxCells];
    private readonly List<string> _alerts = [];
    private readonly List<int?> _alertCells = [];
    private bool _expandInProgress;
    private System.Threading.Timer? _unackTimer;
    private System.Threading.Timer? _uiTimer;

    /// <summary>側欄每一列對應一個監看格。</summary>
    private sealed record OverviewRow(
        int CellIndex,
        string CellLabel,
        string StateLabel,
        string RecLabel,
        string AiLabel,
        SolidColorBrush StateBrush);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeliVMS");
        _dataRoot = ResolveDataRoot();
        _tray = new TrayIconHost(ShowFromTray);
        _tray.ExitRequested += OnTrayExit;
    }

    /// <summary>M26：關閉按鍵預設收進系統匣（真正結束請用匣選單「結束 HeliVMS」）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            WriteTrayProbeIfRequested();
            base.OnClosing(e);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnTrayExit()
    {
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

    /// <summary>資料根目錄：優先 C:\HeliVMSData（§2.1）；不可寫時降級至本機資料夾。</summary>
    internal static string ResolveDataRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HELIVMS_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        try
        {
            Directory.CreateDirectory(DefaultDataRoot);
            return DefaultDataRoot;
        }
        catch (UnauthorizedAccessException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeliVMS");
        }
        catch (IOException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeliVMS");
        }
    }

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
        Localizer.Init(new SettingsRepository(_store));
        Title = $"{Localizer.T("Brand.Title")} | db={Path.Combine(_dataRoot, "index.db")}";
        _channels = new ChannelRepository(_store);
        _segRepo = new SegmentRepository(_store);
        _channels.EnsureSeedChannels();

        if (Environment.GetCommandLineArgs().Contains("--playback", StringComparer.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => new PlaybackWindow(_store) { Owner = this }.Show());
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

        _manager = new ChannelManager(_store, Path.Combine(_dataRoot, "recordings"), Path.Combine(_dataRoot, "snapshots"), DetectionModelResolver.TryResolve());
        _manager.FrameArrived += (_, e) => OnCellFrame(e.Cell, e.Frame);
        _manager.AiDetections += OnManagerAiDetections;
        _manager.StateChanged += (_, e) =>
        {
            _cellChannel[e.Cell] = e.Channel.Id;
            _channelStates[e.Channel.Id] = e.State;
            OnCellState(e.Cell, e.State);
        };
        _manager.HealthRestart += (_, e) =>
            HintText.Text = $"頻道「{e.Channel.Name}」畫面逾時，已自動重連。";
        _manager.AlarmEvent += (_, e) => _notify?.Enqueue(e.Record);

        _ioHost = new IoMonitorHost(_store);
        _ioHost.RefreshAndStart();
        _ioHost.EventInserted += (_, record) => _notify?.Enqueue(record);

        _shareHost = new ShareHost(_store);
        _shareHost.ApplySettings(new SettingsRepository(_store));

        _analytics = new AnalyticsEventEngine(_store);
        _alertRules = new AlertRuleRepository(_store);
        _alarmEvents = new AlarmEventRepository(_store);
        _offsite = new OffsiteReplicationRepository(_store);
        _offsiteTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => RunOffsiteDueJobs(), Dispatcher);
        _offsiteTimer.Start();
        RunOffsiteDueJobs();
        _analytics.LoadZones();
        _analytics.EventInserted += (_, record) =>
        {
            if (ShouldSuppressSmartAlert(record))
            {
                return;
            }

            _notify?.Enqueue(record);
        };

        _scheduler = new RecordingScheduler(
            _store,
            Path.Combine(_dataRoot, "recordings"),
            isCellRecording: ch => _manager.IsRecording(ch));
        _scheduler.Activity += (_, msg) => HintText.Text = msg;

        _bgCts = new CancellationTokenSource();
        _ = RunRetentionLoopAsync(_bgCts.Token);

        ChannelCombo.SelectionChanged += OnChannelSelectionChanged;
        RefreshChannelCombo();

        var state = new LicenseManager().ValidateDefault();
        _footerBase = state.IsValid
            ? $"禾秝軟體開發團隊 · 已授權（{state.Payload!.Cameras} 路）"
            : $"未授權：{state.Message ?? state.Status.ToString()}";
        UpdateFooter();

        // 未確認事件計數：即時一筆，之後每 15 秒（規格 §1.4）
        RefreshUnackBadge();
        _unackTimer = new System.Threading.Timer(
            _ => Dispatcher.InvokeAsync(RefreshUnackBadge),
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));

        // 頻道總覽側欄與格格徽章：每秒更新（M12）
        _uiTimer = new System.Threading.Timer(
            _ => Dispatcher.InvokeAsync(UpdateOverview),
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
        var targetRec = Path.Combine(_dataRoot, "recordings");
        if (Directory.Exists(legacyRec) && !Directory.Exists(targetRec))
        {
            Directory.Move(legacyRec, targetRec);
        }
    }

    private void RefreshChannelCombo()
    {
        _channelList = _channels!.List().OrderBy(c => c.Id).ToList();
        ChannelCombo.ItemsSource = _channelList;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        ChannelCombo.SelectedIndex = _channelList.Count > 0 ? 0 : -1;
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";

    /// <summary>每小時執行一次配額清理與 tmp 隔離、快照過期清理（設定每圈重讀）。</summary>
    private async Task RunRetentionLoopAsync(CancellationToken token)
    {
        var service = new RetentionService(_segRepo!, Path.Combine(_dataRoot, "recordings"), new LegalHoldRepository(_store!));
        while (!token.IsCancellationRequested)
        {
            try
            {
                RunScheduledBackup();
                var quota = ReadQuotaBytes();
                var report = service.Apply(quota, DateTime.UtcNow);
                var hintParts = new List<string>();
                if (report.DeletedSegments > 0)
                {
                    hintParts.Add($"移除 {report.DeletedSegments} 段（釋放 {FormatBytes(report.FreedBytes)}）");
                }

                if (report.PurgedTmp > 0)
                {
                    hintParts.Add($"清除 {report.PurgedTmp} 個錄影暫存檔");
                }

                var snapDays = ReadSnapshotDays();
                var snapRoot = Path.Combine(_dataRoot, "snapshots");
                var snapReport = service.PurgeSnapshots(snapRoot, DateTime.UtcNow.AddDays(-snapDays));
                if (snapReport.DeletedFiles > 0)
                {
                    hintParts.Add($"清除 {snapReport.DeletedFiles} 個過期快照（{FormatBytes(snapReport.FreedBytes)}）");
                }

                if (hintParts.Count > 0)
                {
                    HintText.Text = $"配額清理：{string.Join("、", hintParts)}。";
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
        var source = Path.Combine(_dataRoot, "recordings");
        _ = Task.Run(() =>
        {
            try
            {
                var result = new BackupService(_store).Run(source, target);
                var text = result.Failed == 0
                    ? $"備份：複製 {result.Copied} 段（{FormatBytes(result.CopiedBytes)}），檢查點已推進。"
                    : $"備份：複製 {result.Copied} 段、失敗 {result.Failed}，下次重試。";
                Dispatcher.BeginInvoke(() => HintText.Text = text);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => HintText.Text = $"備份失敗：{ex.Message}");
            }
        });
    }

    /// <summary>快照保留天數：app_settings["snapshots.retention_days"] → HELIVMS_SNAPSHOT_DAYS → 30。</summary>
    private int ReadSnapshotDays()
    {
        if (_store is not null)
        {
            var fromDb = new SettingsRepository(_store).GetDoubleOrDefault("snapshots.retention_days", -1);
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

        return 30;
    }

    /// <summary>錄影保留配額（bytes）：app_settings["recording.quota_gb"] → HELIVMS_QUOTA_GB → 10GB。</summary>
    private long ReadQuotaBytes()
    {
        if (_store is not null)
        {
            var fromDb = new SettingsRepository(_store).GetDoubleOrDefault("recording.quota_gb", -1);
            if (fromDb > 0)
            {
                return (long)(fromDb * 1024 * 1024 * 1024);
            }
        }

        return ParseQuotaBytes();
    }

    private static long ParseQuotaBytes()
    {
        var gb = 10.0;
        var raw = Environment.GetEnvironmentVariable("HELIVMS_QUOTA_GB");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) &&
            v > 0)
        {
            gb = v;
        }

        return (long)(gb * 1024 * 1024 * 1024);
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is ChannelInfo ch)
        {
            UrlBox.Text = ch.MainStreamUrl;
        }
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        _expandInProgress = false;
        RebuildCells();
        SaveLayoutPreference();
    }

    /// <summary>目前佈局的格子數（1／4／9／16）。</summary>
    private int CurrentCellCount()
    {
        var cols = Math.Clamp(LayoutCombo.SelectedIndex, 0, 3) + 1;
        return cols * cols;
    }

    /// <summary>依佈局選項重建動態監看格（每格：影像＋AI 疊加＋狀態文字＋右鍵選單）。</summary>
    private void RebuildCells()
    {
        if (CellGrid is null)
        {
            return;
        }

        var count = CurrentCellCount();
        var cols = (int)Math.Sqrt(count);
        CellGrid.Columns = cols;
        CellGrid.Rows = cols;
        CellGrid.Children.Clear();

        for (var i = 0; i < count; i++)
        {
            var img = new Image { Stretch = Stretch.Uniform };
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
                Text = "未連線",
            };
            var badge = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x03, 0x05, 0x08)),
                Foreground = Brushes.White,
                FontSize = Math.Max(9, 15 - cols),
                Text = string.Empty,
                Visibility = Visibility.Collapsed,
            };
            var inner = new Grid();
            inner.Children.Add(img);
            inner.Children.Add(overlay);
            inner.Children.Add(text);
            inner.Children.Add(badge);

            var border = new Border
            {
                Margin = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0x06, 0x09, 0x0F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F)),
                BorderThickness = new Thickness(1),
                Child = inner,
            };
            border.MouseLeftButtonUp += (_, _) => SelectCell(i);
            var menu = new ContextMenu();
            AddMenuItem(menu, "切換全螢幕", OnCtxFullScreen, i);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "切換錄影", OnCtxRecord, i);
            AddMenuItem(menu, "PTZ 控制", OnCtxPtz, i);
            AddMenuItem(menu, "開啟事件中心", OnCtxOpenEvents, i);
            border.ContextMenu = menu;

            _cellImages[i] = img;
            _cellOverlays[i] = overlay;
            _cellTexts[i] = text;
            _cellBadges[i] = badge;
            _cellBorders[i] = border;

            CellGrid.Children.Add(border);
        }

        ApplyCellStateAfterRebuild();
    }

    private static void AddMenuItem(ContextMenu menu, string header, RoutedEventHandler handler, int cell)
    {
        var item = new MenuItem { Header = header, Tag = cell };
        item.Click += handler;
        menu.Items.Add(item);
    }

    private void ApplyCellStateAfterRebuild()
    {
        for (var i = 0; i < MaxCells; i++)
        {
            if (_cellImages[i] is null)
            {
                continue;
            }

            _cellImages[i].Source = _bitmap[i];
            SetCellStatus(i, "未連線", BrOffline);
        }
    }

    /// <summary>佈局記憶：重開沿用上次分割（LIVEVIEW §1.1）。</summary>
    private void ApplyLayoutPreference()
    {
        var pref = 0;
        try
        {
            var path = Path.Combine(_dataRoot, UiSettingsFile);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                pref = doc.RootElement.GetProperty("layout").GetInt32();
            }
        }
        catch (Exception)
        {
            pref = 0;
        }

        pref = pref is >= 0 and <= 3 ? pref : 0;
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
            File.WriteAllText(
                Path.Combine(_dataRoot, UiSettingsFile),
                JsonSerializer.Serialize(new { layout = LayoutCombo.SelectedIndex }));
        }
        catch (Exception)
        {
            // 佈局記憶失敗不影響即時監看
        }
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (_manager is { HasActiveSessions: true })
        {
            await DisconnectAllAsync();
            return;
        }

        if (_channelList.Count == 0)
        {
            SetCellStatus(0, "無可用頻道", null);
            return;
        }

        var start = Math.Max(ChannelCombo.SelectedIndex, 0);
        var cells = CurrentCellCount();
        await _manager!.ConnectAsync(_channelList, start, cells);

        if (_manager.HasActiveSessions)
        {
            ConnectButton.Content = "中斷";
            RecordButton.IsEnabled = true;
            HintText.Text = $"連線中：{_channelList[start].MainStreamUrl}";
        }
        else
        {
            ConnectButton.Content = "連線";
            HintText.Text = "連線失敗。";
        }

        UpdateAiPolicy();
        UpdateFooter();
    }

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

    private void OnCellState(int cell, RtspState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case RtspState.Connecting:
                    SetCellStatus(cell, "連線中…", BrConnecting);
                    break;
                case RtspState.Reconnecting:
                    SetCellStatus(cell, "重連中…", BrConnecting);
                    PushAlert($"頻道 #{cell + 1} 重連中", cell);
                    break;
                case RtspState.Stopped:
                    SetCellStatus(cell, "未連線", BrOffline);
                    break;
            }

            UpdateFooter();
        });
    }

    private void UpdateFooter()
    {
        var live = _manager?.CountStreaming() ?? 0;
        StatusText.Text = _footerBase.Length > 0 ? $"{_footerBase} · 已連線 {live} 路" : string.Empty;
    }

    private void OnCellFrame(int cell, VideoFrame frame)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_cellChannel[cell] is int frCh)
            {
                _channelLastFrame[frCh] = frame.TimestampUtc;
            }
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

            if (GetCellStatusText(cell) is { } s &&
                (s.StartsWith("連線中", StringComparison.Ordinal) ||
                 s.StartsWith("重連中", StringComparison.Ordinal)))
            {
                SetCellStatus(cell, $"即時監看 · {frame.Width}×{frame.Height}", BrLive);
            }

            DrawLiveOverlay(cell);
        });
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
            SetCellSource(i, null);
            SetCellStatus(i, "未連線", BrOffline);
            CellOverlay(i)?.Children.Clear();
        }

        _channelStates.Clear();
        _channelLastFrame.Clear();
        _channelLastAi.Clear();
        _aiPolicyCache.Clear();
        _selectedCell = -1;

        ConnectButton.Content = "連線";
        RecordButton.IsEnabled = false;
        RecordButton.Content = "錄影";
        RecBadge.Visibility = Visibility.Collapsed;
        HintText.Text = "已中斷。";
        UpdateFooter();
    }

    private async void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        if (_manager is null || ChannelCombo.SelectedItem is not ChannelInfo channel)
        {
            return;
        }

        var next = !_manager.IsRecording(channel.Id);
        await _manager.SetRecordingAsync(channel.Id, next);
        RecordButton.Content = next ? "停止錄影" : "錄影";
        RecBadge.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = next
            ? $"錄影中：{Path.Combine(_dataRoot, "recordings", $"ch{channel.Id:000}")}"
            : "錄影已停止。";
    }

    private void OnOnvifClicked(object sender, RoutedEventArgs e)
    {
        var wizard = new OnvifWizardWindow
        {
            Owner = this,
        };
        if (wizard.ShowDialog() == true && wizard.StreamUrl.Length > 0)
        {
            _channels!.Add(wizard.ChannelName, wizard.StreamUrl);
            RefreshChannelCombo();
            HintText.Text = $"已經由 ONVIF 加入頻道「{wizard.ChannelName}」。";
        }
    }

    private void OnPlaybackClicked(object sender, RoutedEventArgs e)
    {
        var playback = new PlaybackWindow(_store!)
        {
            Owner = this,
        };
        playback.Show();
    }

    private void OnEventClicked(object sender, RoutedEventArgs e)
    {
        RefreshUnackBadge();
        OpenEventCenter();
    }

    /// <summary>開啟事件中心（M38 供 harness 以 --events 自動開啟）。</summary>
    private void OpenEventCenter()
    {
        var events = new EventCenterWindow(_store!)
        {
            Owner = this,
        };
        events.Show();
    }

    private void OnScheduleClicked(object sender, RoutedEventArgs e)
    {
        var sched = new SchedulingWindow(_store!)
        {
            Owner = this,
        };
        sched.Show();
    }

    private void ExportEventsCsv(string path)
    {
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

    /// <summary>依現況語言重設主視窗標題（M57 設定中心即時切換用）。</summary>
    public void RefreshTitle()
    {
        Title = $"{Localizer.T("Brand.Title")} | db={Path.Combine(_dataRoot, "index.db")}";
    }

    private void OnDetectionClicked(object sender, RoutedEventArgs e)
    {
        var det = new DetectionWindow(_store!)
        {
            Owner = this,
        };
        det.Show();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void OnExportClicked(object sender, RoutedEventArgs e) => OpenExportWindow();

    private void OnExportCenterClicked(object sender, RoutedEventArgs e) => OpenExportCenterWindow();

    private void OnRedactionClicked(object sender, RoutedEventArgs e) => OpenRedactionWindow();

    private void OnDewarpClicked(object sender, RoutedEventArgs e) => OpenDewarpWindow();

    private void OnAlarmManagerClicked(object sender, RoutedEventArgs e) => OpenAlarmManagerWindow();

    /// <summary>開啟警報管理器（M47，§14.7 #3）：分診面板，與事件中心同權限（不另限 admin）。</summary>
    private void OpenAlarmManagerWindow()
    {
        var window = new AlarmManagerWindow(_store!)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>開啟錄影遮蔽窗（M46，§14.7 #5）。viewer 與匯出同權限限制。</summary>
    private void OpenRedactionWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new RedactionWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>開啟魚眼矯正窗（M48，§14.7 #2）。viewer 與匯出同權限限制。</summary>
    private void OpenDewarpWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new DewarpWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>開啟外部安全共享視窗（M51，§14.7 #4）。viewer 與匯出同權限限制。</summary>
    private void OpenShareWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new ShareWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>開啟分析情境視窗（M52，§14.7 #6）。viewer 與匯出同權限限制。</summary>
    private void OpenAnalyticsWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new AnalyticsWindow(_store!)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>M54：智慧警報聚合抑制——命中 frame_minutes&gt;0 規則且窗內（含本筆）計數未達
    /// 《min_events_in_window》的流量先行丟棄，避免警報洪泛。</summary>
    private bool ShouldSuppressSmartAlert(AlarmEventRecord record)
    {
        var rule = AlertRuleMatcher.Match(_alertRules?.ListEnabled() ?? Array.Empty<AlertRule>(), record);
        if (rule is null || rule.FrameMinutes <= 0)
        {
            return false;
        }

        var fromUtc = record.StartUtc.AddMinutes(-rule.FrameMinutes);
        var windowEvents = _alarmEvents?.ListByRange(record.ChannelId, fromUtc, record.StartUtc)
            ?? Array.Empty<AlarmEventRecord>();
        var count = windowEvents.Count(e => SmartAlertEvaluator.Matches(rule, e));
        if (SmartAlertEvaluator.ShouldSuppress(rule, count))
        {
            _smartAlertSuppressed++;
            return true;
        }

        return false;
    }

    /// <summary>開啟證據完整性視窗（M53，§14.7 #5）。</summary>
    private void OpenEvidenceWindow(string? directory)
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new EvidenceWindow(_store!, directory)
        {
            Owner = this,
        };
        window.Show();
    }

    /// <summary>開啟匯出中心（M44，§14.3(2)）。viewer 與匯出精靈同權限限制。</summary>
    private void OpenExportCenterWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var center = new ExportCenterWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        center.Show();
    }

    /// <summary>依登入角色限制管理功能（M42）：viewer 不能開設定中心／匯出精靈。</summary>
    private void ApplyRoleRestrictions()
    {
        var isAdmin = SessionContext.IsAdmin;
        SettingsButton.IsEnabled = isAdmin;
        ExportButton.IsEnabled = isAdmin;
        ExportCenterButton.IsEnabled = isAdmin;
    }

    /// <summary>開啟通知送達紀錄（M23，§16.3）。</summary>
    private void OnNotificationClicked(object sender, RoutedEventArgs e)
    {
        var logWin = new NotificationLogWindow(_store!)
        {
            Owner = this,
        };
        logWin.Show();
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

    private void OpenSettingsWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var settings = new SettingsWindow(_store!, _dataRoot, _ioHost, _shareHost)
        {
            Owner = this,
        };
        settings.Show();
    }

    /// <summary>開啟匯出精靈（M21，§8.5/§14）。僅 admin（M42）。</summary>
    private void OpenExportWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var export = new ExportWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        export.Show();
    }

    private void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = $"頻道 {_channelList.Count + 1}";
        }

        _channels!.Add(name, url);
        RefreshChannelCombo();
        HintText.Text = $"已加入頻道「{name}」。";
    }

    private Canvas? CellOverlay(int cell) => cell is >= 0 and < MaxCells ? _cellOverlays[cell] : null;

    /// <summary>AI 偵測推播：更新各格疊加暫存，並將最佳目標推入警報列（規格 §1.4）。</summary>
    private void OnManagerAiDetections(object? _, (int Cell, DetectionsFrame Frame) e)
    {
        var (cell, frame) = e;
        if (cell is < 0 or >= MaxCells)
        {
            return;
        }

        _aiBoxes[cell] = frame.Items;

        if (_cellChannel[cell] is int aiCh)
        {
            _channelLastAi[aiCh] = frame.SnapshotUtc;
        }

        // M11：全量偵測 metadata 入佇列，由 DetectionWriter 批次寫入 detections 表
        if (_detWriter is { } writer && _cellChannel[cell] is int chId && frame.Items.Count > 0)
        {
            writer.EnqueueRange(frame.Items.Select(d => new DetectionRecord
            {
                ChannelId = chId,
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
        if (_analytics is { } analytics && _cellChannel[cell] is int analyticsCh)
        {
            analytics.OnDetections(analyticsCh, frame);
        }

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
            Dispatcher.InvokeAsync(() => PushAlert($"頻道 #{cell + 1} AI {best.Class} conf={best.Confidence:0.00}", cell));
        }
    }

    private void OnAiToggleChanged(object sender, RoutedEventArgs e) => _aiVisible = AiToggle.IsChecked == true;

    /// <summary>於該格畫面上繪製最後一次 AI 偵測框（單格附標籤；多格只畫框，規格 §2）。</summary>
    private void DrawLiveOverlay(int cell)
    {
        var canvas = CellOverlay(cell);
        if (canvas is null)
        {
            return;
        }

        canvas.Children.Clear();
        if (!_aiVisible)
        {
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        var src = _bitmap[cell];
        var dets = _aiBoxes[cell];
        if (src is null || dets is null || dets.Count == 0)
        {
            return;
        }

        var availW = Math.Max(0, src.PixelWidth);
        var availH = Math.Max(0, src.PixelHeight);
        var host = canvas.Parent is FrameworkElement f ? f : null;
        var w = host?.ActualWidth ?? availW;
        var h = host?.ActualHeight ?? availH;
        var scale = Math.Min(w / Math.Max(1, availW), h / Math.Max(1, availH));
        if (scale <= 0)
        {
            return;
        }

        canvas.Width = availW * scale;
        canvas.Height = availH * scale;

        var withLabels = LayoutCombo.SelectedIndex == 0;
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
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
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
                    Background = new SolidColorBrush(Color.FromArgb(170, 8, 14, 22)),
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
            HintText.Text = $"已由警報跳至格 {target + 1}。";
        }
    }

    private void RefreshUnackBadge()
    {
        var count = _store is null ? 0 : new AlarmEventRepository(_store).CountUnacknowledged();
        UnackBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnackText.Text = count > 0 ? $"未確認 {count}" : "";
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

        await ToggleExpandCellAsync(cell);
    }

    /// <summary>F11／右鍵共用：單格展開 ↔ 原佈局往返（保留格位與連線）。</summary>
    private async Task ToggleExpandCellAsync(int cell)
    {
        if (_manager is null)
        {
            return;
        }

        var goSingle = LayoutCombo.SelectedIndex != 0;
        if (goSingle)
        {
            _preFullscreenLayout = LayoutCombo.SelectedIndex;
            LayoutCombo.SelectedIndex = 0;
            _expandInProgress = true;
        }
        else
        {
            LayoutCombo.SelectedIndex = _preFullscreenLayout is > 0 and <= 3 ? _preFullscreenLayout : 1;
            _expandInProgress = false;
        }

        var count = goSingle ? 1 : CurrentCellCount();
        var start = goSingle ? cell : 0;
        await _manager.ConnectAsync(_channelList, start, count);
        ConnectButton.Content = "中斷";
        RecordButton.IsEnabled = true;
        UpdateFooter();
    }

    private static readonly System.Windows.Input.Key[] HandledPreviewKeys =
        [System.Windows.Input.Key.F11, System.Windows.Input.Key.Escape];

    /// <summary>F11＝單格展開/還原（ESC 退出展開）。</summary>
    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!HandledPreviewKeys.Contains(e.Key))
        {
            return;
        }

        e.Handled = true;
        if (e.Key == System.Windows.Input.Key.F11)
        {
            await ToggleExpandCellAsync(_selectedCell >= 0 ? _selectedCell : 0);
        }
        else if (e.Key == System.Windows.Input.Key.Escape &&
                 _expandInProgress &&
                 LayoutCombo.SelectedIndex == 0 &&
                 _preFullscreenLayout is > 0 and <= 3)
        {
            await ToggleExpandCellAsync(0);
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
        await _manager.SetRecordingAsync(channelId, next);
        RecordButton.Content = next ? "停止錄影" : "錄影";
        RecBadge.Visibility = _manager.AnyRecording() ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = next
            ? $"錄影中：{Path.Combine(_dataRoot, "recordings", $"ch{channelId:000}")}"
            : "錄影已停止。";
        UpdateFooter();
    }

    private void OnCtxOpenEvents(object sender, RoutedEventArgs e) => OnEventClicked(sender, e);

    private void OnCtxPtz(object sender, RoutedEventArgs e)
    {
        var cell = Convert.ToInt32(((MenuItem)sender).Tag);
        if (cell < 0 || cell >= MaxCells || _cellChannel[cell] is not int channelId)
        {
            return;
        }

        OpenPtz(channelId);
    }

    private void OnMapClicked(object sender, RoutedEventArgs e) => OpenMapWindow();

    /// <summary>開啟統圖報表視窗（M60，§14.7 #9 統計報表）。admin 限定。</summary>
    private void OpenReportsWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new ReportsWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OnReportsClicked(object sender, RoutedEventArgs e) => OpenReportsWindow();

    /// <summary>開啟複合事件規則視窗（M62，§5.10）。admin 限定。</summary>
    private void OpenRulesWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new RulesWindow(_store!)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OnRulesClicked(object sender, RoutedEventArgs e) => OpenRulesWindow();

    /// <summary>開啟影片摘要視窗（M63，§5.9）。admin 限定。</summary>
    private void OpenSynopsisWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new SynopsisWindow(_store!, _dataRoot)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OnSynopsisClicked(object sender, RoutedEventArgs e) => OpenSynopsisWindow();

    /// <summary>開啟保存鎖定視窗（M66，§14.1 #13）。admin 限定。</summary>
    private void OpenLegalHoldWindow()
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        var window = new LegalHoldWindow(_store!, allowRevoke: SessionContext.IsAdmin)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OnLegalHoldClicked(object sender, RoutedEventArgs e) => OpenLegalHoldWindow();

    /// <summary>開啟巡航排程視窗（M72，§47）。</summary>
    private void OpenPatrolWindow()
    {
        var window = new PatrolWindow(_store!)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OnPatrolClicked(object sender, RoutedEventArgs e) => OpenPatrolWindow();

    /// <summary>開啟電子地圖（M41；M49 補比例尺與 FOV 深度）。</summary>
    private void OpenMapWindow()
        => new MapWindow(_store!) { Owner = this }.Show();

    private void OnPtzClicked(object sender, RoutedEventArgs e)
    {
        var idx = ChannelCombo.SelectedIndex;
        if (idx is >= 0 && idx < _channelList.Count)
        {
            OpenPtz(_channelList[idx].Id);
        }
    }

    private void OpenPtz(int channelId)
    {
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
            MessageBox.Show(this, "此頻道未綁定 ONVIF 設備，無法使用 PTZ。", "PTZ 控制",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = new DeviceRepository(store).Get(deviceId);
        if (device is null)
        {
            MessageBox.Show(this, "找不到綁定的 OEM 設備記錄。", "PTZ 控制",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new PtzWindow(store, channel, device) { Owner = this }.Show();
    }

    private static bool IsTargetClass(string cls) =>
        cls is "person" or "car" or "bus" or "truck" or "motorcycle" or "bicycle";

    private Image? CellImage(int cell) => cell is >= 0 and < MaxCells ? _cellImages[cell] : null;

    private TextBlock? CellText(int cell) => cell is >= 0 and < MaxCells ? _cellTexts[cell] : null;

    private void SetCellSource(int cell, BitmapSource? source)
    {
        if (CellImage(cell) is { } img)
        {
            img.Source = source;
        }
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

    private string? GetCellStatusText(int cell) => CellText(cell)?.Text;

    /// <summary>頻道總覽（M12）：每秒重建側欄列、同步選取，並更新每格徽章。</summary>
    private void UpdateOverview()
    {
        if (OverviewList is null)
        {
            return;
        }

        var count = CurrentCellCount();
        var rows = new List<OverviewRow>(count);
        for (var i = 0; i < count; i++)
        {
            var ch = _cellChannel[i];
            var rec = ch is int rc && _manager?.IsRecording(rc) == true;
            var state = ch is int sc && _channelStates.TryGetValue(sc, out var st) ? st : RtspState.Stopped;
            rows.Add(new OverviewRow(
                i,
                ch is int cc ? ChannelName(cc) : $"格 {i + 1}（未指派）",
                StateLabel(state, rec),
                rec ? "●" : string.Empty,
                ch is int ac && _channelLastAi.TryGetValue(ac, out var ai) ? ai.ToLocalTime().ToString("HH:mm:ss") : "-",
                BrushForState(state)));
        }

        _overviewRows.Clear();
        _overviewRows.AddRange(rows);
        var prevSel = _selectedCell;
        OverviewList.ItemsSource = null;
        OverviewList.ItemsSource = rows;
        if (prevSel >= 0)
        {
            SelectCell(prevSel);
        }

        UpdateBadges();
        UpdateAiPolicy();
    }

    private static string StateLabel(RtspState state, bool rec) => state switch
    {
        RtspState.Streaming => rec ? "錄影中" : "即時",
        RtspState.Connecting => "連線中…",
        RtspState.Reconnecting => "重連中…",
        _ => "未連線",
    };

    private static SolidColorBrush BrushForState(RtspState state) => state switch
    {
        RtspState.Streaming => BrLive,
        RtspState.Connecting => BrConnecting,
        RtspState.Reconnecting => BrConnecting,
        _ => BrOffline,
    };

    /// <summary>選定監看格：高亮邊框並與側欄列互選。</summary>
    private void SelectCell(int cell)
    {
        _selectedCell = cell;
        var count = CurrentCellCount();
        for (var i = 0; i < MaxCells; i++)
        {
            var b = _cellBorders[i];
            if (b is null)
            {
                continue;
            }

            var sel = i == cell && cell >= 0 && cell < count;
            b.BorderBrush = sel ? BrHighlight : BrCellEdge;
            b.BorderThickness = sel ? new Thickness(2) : new Thickness(1);
        }

        var idx = _overviewRows.FindIndex(r => r.CellIndex == cell);
        if (idx >= 0 && OverviewList.SelectedIndex != idx)
        {
            OverviewList.SelectedIndex = idx;
        }
    }

    private void OnOverviewSelection(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewList.SelectedItem is OverviewRow row)
        {
            SelectCell(row.CellIndex);
        }
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
            var text = ch is int id ? ChannelName(id) + (_manager?.IsRecording(id) == true ? " ●REC" : "") : string.Empty;
            badge.Text = text;
            badge.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string ChannelName(int channelId)
    {
        foreach (var c in _channelList)
        {
            if (c.Id == channelId)
            {
                return c.Name;
            }
        }

        return $"#{channelId}";
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _unackTimer?.Dispose();
        _uiTimer?.Dispose();
        var scheduler = _scheduler;
        _scheduler = null;
        if (scheduler is not null)
        {
            await scheduler.StopAllAsync();
            scheduler.Dispose();
        }

        await DisconnectAllAsync();
        _bgCts?.Cancel();
        _bgCts?.Dispose();
        _detWriter?.Dispose();
        _ioHost?.Dispose();
        _shareHost?.Dispose();
        _notify?.Dispose();
        _manager?.Dispose();
        _store?.Dispose();
    }
}