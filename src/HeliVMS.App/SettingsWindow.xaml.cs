using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HeliVMS.Alarms;
using HeliVMS.App.Services;
using HeliVMS.Licensing;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Path = System.IO.Path;

namespace HeliVMS.App;

/// <summary>
/// 管理設定中心（M19，§9）：一般／儲存（配額＋用量＋立即清理）／授權（狀態＋金鑰套用）／功能入口。
/// 設定值以 app_settings 表為權威來源。
/// </summary>
public partial class SettingsWindow : Window
{
    // 配額與快照保管天數的設定鍵／預設值引用 RetentionCoordinator（桌面、Agent 共用），避免各寫一份字串而漂移。
    private const string QuotaKey = RetentionCoordinator.QuotaGbKey;
    private const double DefaultQuotaGb = RetentionCoordinator.DefaultQuotaGb;
    private const string SnapshotDaysKey = RetentionCoordinator.SnapshotDaysKey;
    private const int DefaultSnapshotDays = RetentionCoordinator.DefaultSnapshotDays;

    // 中繼資料保管期限：鍵與預設值直接引用 WebApi 也使用的同一份定義（MetadataRetentionPruner），
    // 避免桌面與 Web 各自寫一份字串而漂移（漂移的話，用 Web 設定改天數、桌面迴圈卻讀不到）。
    // 0＝永久保留。
    private const string AlarmRetentionDaysKey = MetadataRetentionPruner.AlarmDaysKey;
    private const string AuditRetentionDaysKey = MetadataRetentionPruner.AuditDaysKey;
    private const int DefaultMetadataRetentionDays = MetadataRetentionPruner.DefaultDays;
    private const int MaxMetadataRetentionDays = 36500;
    private const string TamperKey = "detect.tamper.enabled";
    private const string HwAccelKey = "decode.hwaccel";

    /// <summary>告警規則頁顯示列。</summary>
    private sealed record RuleRow(long Id, string Name, string EventType, string ChannelLabel, string Keyword, string MatchTypes, string AggregateLabel, string Channels, string EnabledLabel);

    /// <summary>IO 模組頁顯示列。</summary>
    private sealed record IoDeviceRow(int Id, string Name, string Host, string EndpointLabel, string EnabledLabel, int PollMs);

    /// <summary>IO 通道頁顯示列。</summary>
    private sealed record IoChannelRow(int Id, string Direction, int IoIndex, string Name, string EnabledLabel, string NcLabel, int DebounceMs, string CameraLabel);
    private sealed record MapRow(int Id, string Name, string SizeLabel, string EnabledLabel, int SortOrder, string ScaleLabel);
    private sealed record MapPinRow(int Id, string DeviceType, int ChannelId, string PositionLabel, string AngleLabel, string FovLabel, string DepthLabel, string BearingLabel, string EnabledLabel, string Name);
    private sealed record UserRow(int Id, string Username, string Role, string EnabledLabel, string FailedLabel, string LastLoginLabel);

    /// <summary>企業身份提供者頁顯示列（M50，§14.7 #1）。</summary>
    private sealed record EnterpriseProviderRow(int Id, string Name, string KindLabel, string EnabledLabel, string SummaryLabel);

    /// <summary>備份紀錄頁顯示列。</summary>
    private sealed record BackupRow(string TimeLabel, string TargetLabel, string CopiedLabel, string FailedLabel, string AdvancedLabel);

    /// <summary>設定版本清單顯示列（§9.5）。</summary>
    private sealed record VersionRow(long Id, string TimeLabel, string KeysLabel, string ReasonLabel, string ActorLabel);

    /// <summary>多磁碟錄影清單顯示列（角色與空間文字在語言切換時會重畫）。</summary>
    private sealed record DiskRow(string Root, string RoleLabel, string SpaceLabel);

    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly SettingsRepository _settings;

    /// <summary>設定中心正在編輯的錄影磁碟清單（順序即優先序，第 0 筆為主要碟）。</summary>
    private List<RecordingStorage.RecordingDiskDto> _diskConfig = [];
    private readonly AlertRuleRepository _rules;
    private readonly IoRepository _io;
    private readonly IoMonitorHost? _ioHost;
    private readonly MapRepository _maps;
    private readonly string _mapsRoot;
    private readonly UserRepository _users;
    private readonly EnterpriseAuthService _enterprise;
    private readonly AuthProviderRepository _providers;
    private readonly ShareHost? _shareHost;
    private readonly WebHost? _webHost;
    private readonly ChannelManager? _manager;

    /// <summary>
    /// 回放前收檔的服務；由主視窗一路傳進來。null 時回放照舊只看索引現況。
    /// </summary>
    private readonly IPlaybackRecordingFinalizer? _playbackFinalizer;
    private readonly PatrolHostService? _patrolHost;
    private readonly Action? _channelsChanged;

    /// <summary>切換錄影磁碟時通知主視窗立即套用（重建錄影工作階段）。</summary>
    private readonly Action<string>? _recordingsRootChanged;

    private bool _suppressAuthToggle;

    public SettingsWindow(
        SqliteStore store,
        string dataRoot,
        IoMonitorHost? ioHost = null,
        ShareHost? shareHost = null,
        ChannelManager? manager = null,
        PatrolHostService? patrolHost = null,
        Action? channelsChanged = null,
        Action<string>? recordingsRootChanged = null,
        WebHost? webHost = null,
        IPlaybackRecordingFinalizer? playbackFinalizer = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        _settings = new SettingsRepository(store);
        _recordingsRoot = RecordingStorage.ResolveConfiguredRoot(
            dataRoot,
            _settings.Get(RecordingStorage.RootSettingKey));
        _snapshotsRoot = Path.Combine(dataRoot, "snapshots");
        _rules = new AlertRuleRepository(store);
        _io = new IoRepository(store);
        _ioHost = ioHost;
        _maps = new MapRepository(store);
        _mapsRoot = Path.Combine(dataRoot, "maps");
        _users = new UserRepository(store);
        _enterprise = new EnterpriseAuthService(store);
        _providers = new AuthProviderRepository(store);
        _shareHost = shareHost;
        _webHost = webHost;
        _manager = manager;
        _patrolHost = patrolHost;
        _playbackFinalizer = playbackFinalizer;
        // 由主視窗傳入：攝影機／頻道管理異動後立即請主視窗重載監看區。
        _channelsChanged = channelsChanged;
        _recordingsRootChanged = recordingsRootChanged;
        _offsite = new OffsiteReplicationRepository(store);

        InitializeComponent();
        Title = Localizer.T("Settings.Title");
        MapPinCanvas.PreviewMouseLeftButtonDown += OnMapPinCanvasClick;
        MapPinList.SelectionChanged += OnMapPinSelectionChanged;

        // 編輯畫面的平面圖也要「整張看得到」：固定 210px 高的框配上 4000px 的圖，1:1 只能看到
        // 左上角一小塊，操作員根本無法把圖釘放到圖的另一半。視窗剛開時 Viewport 還是 0，
        // 所以載圖當下先算一次、版面量好後再由這個事件補算。
        MapPinScroll.SizeChanged += (_, _) => ApplyMapPinFit();

        // 裝置／頻道管理限 admin（與主視窗一致）；viewer 仍看得到入口，但按鈕停用。
        var isAdmin = SessionContext.IsAdmin;
        OpenCameraManagerButton.IsEnabled = isAdmin;
        OpenChannelManagerButton.IsEnabled = isAdmin;
        OpenMonitorGroupsButton.IsEnabled = isAdmin;

        VersionText.Text = $"HeliVMS {Assembly.GetExecutingAssembly().GetName().Version}";
        DataRootText.Text = _dataRoot;
        RecordingsRootText.Text = _recordingsRoot;
        SnapshotsRootText.Text = _snapshotsRoot;

        ReloadQuota();
        ReloadSnapshotDays();
        ReloadAlarmRetentionDays();
        ReloadAuditRetentionDays();
        ReloadUsage();
        ReloadRecordingsDisk();
        ReloadBandwidthBudget();
        ReloadSegmentSeconds();
        ReloadLicense();
        ReloadNotify();
        ReloadRules();
        ReloadTamper();
        ReloadIo();
        ReloadMaps();
        ReloadAuthPolicy();
        ReloadUsers();
        ReloadEnterpriseProviders();
        ReloadBackup();
        ReloadOffsite();
        ReloadShare();
        ReloadNetwork();

        var langIdx = Array.IndexOf(I18n.Languages, Localizer.Lang);
        LangCombo.SelectedIndex = langIdx >= 0 ? langIdx : 0;
        OskToggleCheck.IsChecked = Osk.Enabled;
        ReloadHwAccel();
        ApplyI18n();
        ReloadTools();

        FillAbout();

        SettingsNav.SelectedIndex = 0;
    }

    /// <summary>填入「關於」頁的版本與儲存路徑資訊。</summary>
    private void FillAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AboutVersionText.Text = $"HeliVMS {informational ?? version}";
        AboutDataText.Text = string.Format(Localizer.T("Settings.AboutData"), _dataRoot);
        AboutRecordingText.Text = string.Format(Localizer.T("Settings.AboutRecording"), _recordingsRoot);
        AboutSnapshotText.Text = string.Format(Localizer.T("Settings.AboutSnapshot"), _snapshotsRoot);
        AboutMapText.Text = string.Format(Localizer.T("Settings.AboutMap"), _mapsRoot);

        FillAutoStart();
    }

    /// <summary>
    /// 呈現登入自啟的目前狀態。
    ///
    /// 勾選框在讀不到登錄檔時直接停用並說明原因，而不是讓使用者勾了之後
    /// 重開機發現沒生效——那種故障完全沒有其他線索可循。
    /// </summary>
    private void FillAutoStart()
    {
        AutoStartBox.Content = Localizer.T("Settings.AutoStart");
        AutoStartBox.ToolTip = Localizer.T("Settings.AutoStartHint");

        if (!RegistryAutoStartStore.IsInteractiveSession() || RegistryAutoStartStore.IsBlockedByPolicy())
        {
            AutoStartBox.IsEnabled = false;
            AutoStartBox.IsChecked = false;
            AutoStartStatusText.Text = Localizer.T("Settings.AutoStartUnavailable");
            return;
        }

        try
        {
            AutoStartBox.IsChecked = NewAutoStart().IsEnabled();
            AutoStartStatusText.Text = AutoStartBox.IsChecked == true
                ? Localizer.T("Settings.AutoStartOn")
                : Localizer.T("Settings.AutoStartOff");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            AutoStartBox.IsEnabled = false;
            AutoStartStatusText.Text = string.Format(
                Localizer.T("Settings.AutoStartFailed"), ex.Message);
        }
    }

    private AutoStartService NewAutoStart() =>
        new(new RegistryAutoStartStore(), AutoStartService.CurrentExecutablePath());

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            NewAutoStart().SetEnabled(AutoStartBox.IsChecked == true);
            AutoStartStatusText.Text = AutoStartBox.IsChecked == true
                ? Localizer.T("Settings.AutoStartOn")
                : Localizer.T("Settings.AutoStartOff");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 回復勾選狀態：畫面顯示的必須是實際生效的設定，否則使用者會以為已啟用。
            AutoStartBox.IsChecked = !AutoStartBox.IsChecked;
            AutoStartStatusText.Text = string.Format(
                Localizer.T("Settings.AutoStartFailed"), ex.Message);
        }
    }

    /// <summary>依現況語言套用設定視窗的全部靜態文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Settings.Title");

        // 導航
        NavHeading.Text = Localizer.T("Settings.NavHeading");
        NavGeneral.Content = Localizer.T("Settings.NavGeneral");
        NavDevices.Content = Localizer.T("Settings.NavDevices");
        NavStorage.Content = Localizer.T("Settings.NavStorage");
        NavLicense.Content = Localizer.T("Settings.NavLicense");
        NavLaunch.Content = Localizer.T("Settings.NavLaunch");
        NavNotify.Content = Localizer.T("Settings.NavNotify");
        NavRules.Content = Localizer.T("Settings.NavRules");
        NavIo.Content = Localizer.T("Settings.NavIo");
        NavMap.Content = Localizer.T("Settings.NavMap");
        NavUsers.Content = Localizer.T("Settings.NavUsers");
        NavBackup.Content = Localizer.T("Settings.NavBackup");
        NavNetwork.Content = Localizer.T("Settings.NavNetwork");
        NavAbout.Content = Localizer.T("Settings.NavAbout");

        // 一般
        GeneralTitle.Text = Localizer.T("Settings.GeneralTitle");
        VersionLabel.Text = Localizer.T("Settings.VersionLabel");
        DataRootLabel.Text = Localizer.T("Settings.DataRootLabel");
        RecordingsRootLabel.Text = Localizer.T("Settings.RecordingsRootLabel");
        SnapshotsRootLabel.Text = Localizer.T("Settings.SnapshotsRootLabel");
        DataRootHintText.Text = Localizer.T("Settings.DataRootHint");
        ShareIntroText.Text = Localizer.T("Settings.ShareIntroText");
        ShareEnabledBox.Content = Localizer.T("Settings.ShareEnabled");
        SharePortLabel.Text = Localizer.T("Settings.SharePortLabel");
        ShareBaseUrlLabel.Text = Localizer.T("Settings.ShareBaseUrlLabel");
        ShareApplyButton.Content = Localizer.T("Settings.Apply");
        ShareApplyButton.ToolTip = Localizer.T("Settings.ShareApplyTip");
        OskToggleCheck.Content = Localizer.T("Settings.OskToggle");
        OskToggleCheck.ToolTip = Localizer.T("Settings.OskToggleTip");
        LangLabel.Text = Localizer.T("Settings.Language");
        LangHintText.Text = Localizer.T("Settings.LangHint");
        ((ComboBoxItem)LangCombo.Items[0]).Content = Localizer.T("Settings.LangZhHant");
        ((ComboBoxItem)LangCombo.Items[1]).Content = Localizer.T("Settings.LangZhHans");
        ((ComboBoxItem)LangCombo.Items[2]).Content = Localizer.T("Settings.LangEn");
        HwAccelLabel.Text = Localizer.T("Settings.HwAccel");
        HwAccelLabel.Text = Localizer.T("Settings.HwAccel");
        HwAccelHintText.Text = Localizer.T("Settings.HwAccelHint");

        // 網路
        NetworkTitle.Text = Localizer.T("Settings.NetworkTitle");
        WebIntroText.Text = Localizer.T("Settings.WebIntroText");
        WebEnabledBox.Content = Localizer.T("Settings.WebEnabled");
        WebPortLabel.Text = Localizer.T("Settings.WebPortLabel");
        WebAllowNoKeyBox.Content = Localizer.T("Settings.WebAllowNoKey");
        WebAllowNoKeyHintText.Text = Localizer.T("Settings.WebAllowNoKeyHint");
        WebApplyButton.Content = Localizer.T("Settings.Apply");
        WebApplyButton.ToolTip = Localizer.T("Settings.WebApplyTip");
        NtpSectionTitle.Text = Localizer.T("Settings.NtpSectionTitle");
        NtpIntroText.Text = Localizer.T("Settings.NtpIntroText");
        NtpEnabledBox.Content = Localizer.T("Settings.NtpEnabled");
        NtpServerLabel.Text = Localizer.T("Settings.NtpServerLabel");
        NtpSyncButton.Content = Localizer.T("Settings.NtpSyncNow");
        NtpSyncButton.ToolTip = Localizer.T("Settings.NtpSyncTip");
        NtpHintText.Text = Localizer.T("Settings.NtpHint");

        // 儲存
        StorageTitle.Text = Localizer.T("Settings.StorageTitle");
        RecordingsDiskLabel.Text = Localizer.T("Settings.RecordingsDiskLabel");
        ApplyRecordingsDiskButton.Content = Localizer.T("Settings.Apply");
        ApplyRecordingsDiskButton.ToolTip = Localizer.T("Settings.RecordingsDiskApplyTip");
        AddRecordingsDiskButton.Content = Localizer.T("Settings.AddDisk");
        AddRecordingsDiskButton.ToolTip = Localizer.T("Settings.AddDiskTip");
        SetPrimaryRecordingsDiskButton.Content = Localizer.T("Settings.SetPrimaryDisk");
        SetPrimaryRecordingsDiskButton.ToolTip = Localizer.T("Settings.SetPrimaryDiskTip");
        RemoveRecordingsDiskButton.Content = Localizer.T("Settings.RemoveDisk");
        RemoveRecordingsDiskButton.ToolTip = Localizer.T("Settings.RemoveDiskTip");
        RecordingsDiskHintText.Text = Localizer.T("Settings.RecordingsDiskHint");
        RefreshRecordingsDiskList();
        BandwidthBudgetLabel.Text = Localizer.T("Settings.BandwidthBudgetLabel");
        ApplyBandwidthBudgetButton.Content = Localizer.T("Settings.Apply");
        ApplyBandwidthBudgetButton.ToolTip = Localizer.T("Settings.ApplyBandwidthBudgetTip");
        BandwidthBudgetHintText.Text = Localizer.T("Settings.BandwidthBudgetHint");
        SegmentSecondsLabel.Text = Localizer.T("Settings.SegmentSecondsLabel");
        ApplySegmentSecondsButton.Content = Localizer.T("Settings.Apply");
        ApplySegmentSecondsButton.ToolTip = Localizer.T("Settings.ApplySegmentSecondsTip");
        SegmentSecondsHintText.Text = Localizer.T("Settings.SegmentSecondsHint");
        QuotaLabel.Text = Localizer.T("Settings.QuotaLabel");
        ApplyQuotaButton.Content = Localizer.T("Settings.Apply");
        ApplyQuotaButton.ToolTip = Localizer.T("Settings.ApplyQuotaTip");
        UsageLabel.Text = Localizer.T("Settings.UsageLabel");
        SnapshotDaysLabel.Text = Localizer.T("Settings.SnapshotDaysLabel");
        ApplySnapshotDaysButton.Content = Localizer.T("Settings.Apply");
        ApplySnapshotDaysButton.ToolTip = Localizer.T("Settings.ApplySnapshotDaysTip");
        AlarmRetentionDaysLabel.Text = Localizer.T("Settings.AlarmRetentionDaysLabel");
        ApplyAlarmRetentionDaysButton.Content = Localizer.T("Settings.Apply");
        ApplyAlarmRetentionDaysButton.ToolTip = Localizer.T("Settings.ApplyAlarmRetentionDaysTip");
        AlarmRetentionDaysHintText.Text = Localizer.T("Settings.AlarmRetentionDaysHint");
        AuditRetentionDaysLabel.Text = Localizer.T("Settings.AuditRetentionDaysLabel");
        ApplyAuditRetentionDaysButton.Content = Localizer.T("Settings.Apply");
        ApplyAuditRetentionDaysButton.ToolTip = Localizer.T("Settings.ApplyAuditRetentionDaysTip");
        AuditRetentionDaysHintText.Text = Localizer.T("Settings.AuditRetentionDaysHint");
        CleanupButton.Content = Localizer.T("Settings.Cleanup");
        CleanupButton.ToolTip = Localizer.T("Settings.CleanupTip");
        ImportRecordingsHintText.Text = Localizer.T("Settings.ImportRecordingsHint");
        ImportRecordingsButton.Content = Localizer.T("Settings.ImportRecordings");
        ImportRecordingsButton.ToolTip = Localizer.T("Settings.ImportRecordingsTip");

        // 裝置與頻道
        DevicesTitle.Text = Localizer.T("Settings.DevicesTitle");
        DevicesDesc.Text = Localizer.T("Settings.DevicesDesc");
        OpenCameraManagerButton.Content = Localizer.T("Settings.OpenCameras");
        OpenCameraManagerButton.ToolTip = Localizer.T("Settings.OpenCameraTip");
        OpenChannelManagerButton.Content = Localizer.T("Settings.OpenChannels");
        OpenChannelManagerButton.ToolTip = Localizer.T("Settings.OpenChannelTip");
        OpenMonitorGroupsButton.Content = Localizer.T("Settings.OpenMonitorGroups");
        OpenMonitorGroupsButton.ToolTip = Localizer.T("Settings.OpenMonitorGroupsTip");

        // 授權
        LicenseTitle.Text = Localizer.T("Settings.LicenseTitle");
        LicenseStatusLabel.Text = Localizer.T("Settings.LicenseStatusLabel");
        LicenseStep1.Text = Localizer.T("Settings.LicenseStep1");
        CopyMachineButton.Content = Localizer.T("Settings.CopyMachine");
        CopyMachineButton.ToolTip = Localizer.T("Settings.CopyMachineTip");
        LicenseFeaturesLabel.Text = Localizer.T("Settings.LicenseFeaturesLabel");
        LicenseStep2.Text = Localizer.T("Settings.LicenseStep2");
        LicenseStep3.Text = Localizer.T("Settings.LicenseStep3");
        ApplyLicenseButton.Content = Localizer.T("Settings.ApplyLicense");
        ApplyLicenseButton.ToolTip = Localizer.T("Settings.ApplyLicenseTip");

        // 功能
        LaunchTitle.Text = Localizer.T("Settings.LaunchTitle");
        LaunchDesc.Text = Localizer.T("Settings.LaunchDesc");
        LaunchPlaybackButton.Content = Localizer.T("Settings.LaunchPlayback");
        LaunchPlaybackButton.ToolTip = Localizer.T("Settings.LaunchPlaybackTip");
        LaunchEventsButton.Content = Localizer.T("Settings.LaunchEvents");
        LaunchEventsButton.ToolTip = Localizer.T("Settings.LaunchEventsTip");
        LaunchScheduleButton.Content = Localizer.T("Settings.LaunchSchedule");
        LaunchScheduleButton.ToolTip = Localizer.T("Settings.LaunchScheduleTip");
        LaunchDetectionButton.Content = Localizer.T("Settings.LaunchDetection");
        LaunchDetectionButton.ToolTip = Localizer.T("Settings.LaunchDetectionTip");
        TamperHeading.Text = Localizer.T("Settings.TamperHeading");
        TamperEnabledBox.Content = Localizer.T("Settings.TamperEnabled");
        TamperDesc.Text = Localizer.T("Settings.TamperDesc");

        // 工具（原監控中心「更多工具」入口，整合進「功能」頁）
        ToolsRecordingHeader.Text = Localizer.T("Main.SecRecordingNotify");
        ToolsImageHeader.Text = Localizer.T("Main.SecImagePtz");
        ToolsAnalyticsHeader.Text = Localizer.T("Main.SecAnalytics");
        ToolsSystemHeader.Text = Localizer.T("Main.SecSystem");
        ExportButton.Content = Localizer.T("Main.Export");
        ExportButton.ToolTip = Localizer.T("Main.ExportTip");
        ExportCenterButton.Content = Localizer.T("Main.ExportCenter");
        ExportCenterButton.ToolTip = Localizer.T("Main.ExportCenterTip");
        NotificationButton.Content = Localizer.T("Main.Notification");
        NotificationButton.ToolTip = Localizer.T("Main.NotificationTip");
        RedactionButton.Content = Localizer.T("Main.Redaction");
        RedactionButton.ToolTip = Localizer.T("Main.RedactionTip");
        DewarpButton.Content = Localizer.T("Main.Dewarp");
        DewarpButton.ToolTip = Localizer.T("Main.DewarpTip");
        PtzButton.Content = Localizer.T("Main.Ptz");
        PtzButton.ToolTip = Localizer.T("Main.PtzTip");
        PtzChannelCombo.ToolTip = Localizer.T("Main.PtzTip");
        StreamButton.Content = Localizer.T("Main.Stream");
        StreamButton.ToolTip = Localizer.T("Main.StreamTip");
        AudioButton.Content = Localizer.T("Main.Audio");
        AudioButton.ToolTip = Localizer.T("Main.AudioTip");
        PatrolButton.Content = Localizer.T("Main.Patrol");
        PatrolButton.ToolTip = Localizer.T("Main.PatrolTip");
        ReportsButton.Content = Localizer.T("Main.Reports");
        ReportsButton.ToolTip = Localizer.T("Main.ReportsTip");
        SynopsisButton.Content = Localizer.T("Main.Synopsis");
        SynopsisButton.ToolTip = Localizer.T("Main.SynopsisTip");
        SearchButton.Content = Localizer.T("Main.Search");
        SearchButton.ToolTip = Localizer.T("Main.SearchTip");
        SystemHealthButton.Content = Localizer.T("Main.SystemHealth");
        SystemHealthButton.ToolTip = Localizer.T("Main.SystemHealthTip");
        AlarmButton.Content = Localizer.T("Main.Alarm");
        AlarmButton.ToolTip = Localizer.T("Main.AlarmTip");
        RulesButton.Content = Localizer.T("Main.Rules");
        RulesButton.ToolTip = Localizer.T("Main.RulesTip");
        IoButton.Content = Localizer.T("Main.Io");
        IoButton.ToolTip = Localizer.T("Main.IoTip");
        AuditButton.Content = Localizer.T("Main.Audit");
        AuditButton.ToolTip = Localizer.T("Main.AuditTip");
        LegalHoldButton.Content = Localizer.T("Main.LegalHold");
        LegalHoldButton.ToolTip = Localizer.T("Main.LegalHoldTip");
        SmartwallButton.Content = Localizer.T("Main.Smartwall");
        SmartwallButton.ToolTip = Localizer.T("Main.SmartwallTip");
        PosButton.Content = Localizer.T("Main.Pos");
        PosButton.ToolTip = Localizer.T("Main.PosTip");
        EdgeBackfillButton.Content = Localizer.T("Main.EdgeBackfill");
        EdgeBackfillButton.ToolTip = Localizer.T("Main.EdgeBackfillTip");
        FailoverButton.Content = Localizer.T("Main.Failover");
        FailoverButton.ToolTip = Localizer.T("Main.FailoverTip");
        TalkButton.Content = Localizer.T("Main.Talk");
        TalkButton.ToolTip = Localizer.T("Main.TalkTip");

        // 通知
        NotifyTitle.Text = Localizer.T("Settings.NotifyTitle");
        NotifyDesc.Text = Localizer.T("Settings.NotifyDesc");
        NotifyEnabledBox.Content = Localizer.T("Settings.NotifyEnabled");
        NotifyWebhookLabel.Text = Localizer.T("Settings.NotifyWebhookLabel");
        NotifySmtpEnabledBox.Content = Localizer.T("Settings.NotifySmtpEnabled");
        NotifySmtpHostLabel.Text = Localizer.T("Settings.NotifySmtpHostLabel");
        NotifySmtpPortLabel.Text = Localizer.T("Settings.NotifySmtpPortLabel");
        NotifySmtpFromLabel.Text = Localizer.T("Settings.NotifySmtpFromLabel");
        NotifySmtpToLabel.Text = Localizer.T("Settings.NotifySmtpToLabel");
        NotifySmtpUserLabel.Text = Localizer.T("Settings.NotifySmtpUserLabel");
        NotifySmtpPasswordLabel.Text = Localizer.T("Settings.NotifySmtpPasswordLabel");
        NotifyQuietLabel.Text = Localizer.T("Settings.NotifyQuietLabel");
        NotifyQuietStartLabel.Text = Localizer.T("Settings.NotifyQuietStartLabel");
        NotifyQuietEndLabel.Text = Localizer.T("Settings.NotifyQuietEndLabel");
        NotifyQuietRetransmitBox.Content = Localizer.T("Settings.NotifyQuietRetransmit");
        NotifyMqttEnabledBox.Content = Localizer.T("Settings.Mqtt");
        NotifyMqttHostLabel.Text = Localizer.T("Settings.HostLabel");
        NotifyMqttPortLabel.Text = Localizer.T("Settings.PortLabel");
        NotifyMqttTopicLabel.Text = Localizer.T("Settings.NotifyMqttTopicLabel");
        NotifyMqttUserLabel.Text = Localizer.T("Settings.UserLabel");
        NotifyMqttPasswordLabel.Text = Localizer.T("Settings.PasswordLabel");
        NotifyPushHeading.Text = Localizer.T("Settings.NotifyPushHeading");
        NotifyPushEnabledBox.Content = Localizer.T("Settings.NotifyPushSend");
        NotifyPushEndpointLabel.Text = Localizer.T("Settings.NotifyPushEndpointLabel");
        NotifyPushVapidHint.Text = Localizer.T("Settings.NotifyPushVapidHint");
        NotifySnmpHeading.Text = Localizer.T("Settings.NotifySnmpHeading");
        NotifySnmpEnabledBox.Content = Localizer.T("Settings.NotifySnmpSend");
        NotifySnmpHostLabel.Text = Localizer.T("Settings.HostLabel");
        NotifySnmpPortLabel.Text = Localizer.T("Settings.PortLabel");
        NotifyTeamPlusHeading.Text = Localizer.T("Settings.NotifyTeamPlusHeading");
        NotifyTeamPlusEnabledBox.Content = Localizer.T("Settings.NotifyTeamPlusSend");
        NotifyTeamPlusApiUrlLabel.Text = Localizer.T("Settings.NotifyTeamPlusApiUrlLabel");
        NotifyTeamPlusAccountLabel.Text = Localizer.T("Settings.NotifyTeamPlusAccountLabel");
        NotifyTeamPlusApiKeyLabel.Text = Localizer.T("Settings.NotifyTeamPlusApiKeyLabel");
        NotifyTeamPlusChatSnLabel.Text = Localizer.T("Settings.NotifyTeamPlusChatSnLabel");
        NotifyApplyButton.Content = Localizer.T("Settings.ApplyNotify");
        NotifyApplyButton.ToolTip = Localizer.T("Settings.ApplyNotifyTip");

        // 規則
        RulesTitle.Text = Localizer.T("Settings.RulesTitle");
        RulesDesc.Text = Localizer.T("Settings.RulesDesc");
        RuleNameBox.ToolTip = Localizer.T("Settings.RuleNameTip");
        RuleEventTypeBox.ToolTip = Localizer.T("Settings.RuleEventTypeTip");
        RuleKeywordBox.ToolTip = Localizer.T("Settings.RuleKeywordTip");
        AddRuleButton.Content = Localizer.T("Settings.AddRule");
        AddRuleButton.ToolTip = Localizer.T("Settings.AddRuleTip");
        RuleMatchTypesBox.ToolTip = Localizer.T("Settings.RuleMatchTypesTip");
        RuleFrameMinutesBox.ToolTip = Localizer.T("Settings.RuleFrameMinutesTip");
        RuleMinEventsBox.ToolTip = Localizer.T("Settings.RuleMinEventsTip");
        RuleSmartLabel.Text = Localizer.T("Settings.RuleSmartLabel");
        RuleChannelLabel.Text = Localizer.T("Settings.RuleChannelLabel");
        RuleChannelWebhookBox.Content = Localizer.T("Settings.Webhook");
        RuleChannelWebhookBox.ToolTip = Localizer.T("Settings.WebhookTip");
        RuleChannelSmtpBox.Content = Localizer.T("Settings.Smtp");
        RuleChannelSmtpBox.ToolTip = Localizer.T("Settings.SmtpTip");
        RuleChannelMqttBox.Content = Localizer.T("Settings.Mqtt");
        RuleChannelMqttBox.ToolTip = Localizer.T("Settings.MqttTip");
        RuleChannelPushBox.Content = Localizer.T("Settings.Push");
        RuleChannelPushBox.ToolTip = Localizer.T("Settings.PushTip");
        RuleChannelSnmpBox.Content = Localizer.T("Settings.Snmp");
        RuleChannelSnmpBox.ToolTip = Localizer.T("Settings.SnmpTip");
        RuleChannelTeamPlusBox.Content = Localizer.T("Settings.TeamPlus");
        RuleChannelTeamPlusBox.ToolTip = Localizer.T("Settings.TeamPlusTip");
        ToggleRuleButton.Content = Localizer.T("Settings.Toggle");
        ToggleRuleButton.ToolTip = Localizer.T("Settings.ToggleRuleTip");
        DeleteRuleButton.Content = Localizer.T("Settings.DeleteRule");
        DeleteRuleButton.ToolTip = Localizer.T("Settings.DeleteRuleTip");
        RuleColName.Header = Localizer.T("Settings.RuleColName");
        RuleColEventType.Header = Localizer.T("Settings.RuleColEventType");
        RuleColChannel.Header = Localizer.T("Settings.RuleColChannel");
        RuleColKeyword.Header = Localizer.T("Settings.RuleColKeyword");
        RuleColMatchTypes.Header = Localizer.T("Settings.RuleColMatchTypes");
        RuleColAggregate.Header = Localizer.T("Settings.RuleColAggregate");
        RuleColChannels.Header = Localizer.T("Settings.RuleColChannels");
        RuleColEnabled.Header = Localizer.T("Settings.RuleColEnabled");

        // IO
        IoTitles.Text = Localizer.T("Settings.IoTitles");
        IoDesc.Text = Localizer.T("Settings.IoDesc");
        IoDesc2.Text = Localizer.T("Settings.IoDesc2");
        IoModuleHeading.Text = Localizer.T("Settings.IoModuleHeading");
        IoNameBox.ToolTip = Localizer.T("Settings.IoNameTip");
        IoHostBox.ToolTip = Localizer.T("Settings.IoHostTip");
        IoPortBox.ToolTip = Localizer.T("Settings.IoPortTip");
        IoPollBox.ToolTip = Localizer.T("Settings.IoPollTip");
        IoAddDeviceButton.Content = Localizer.T("Settings.IoAddDevice");
        IoAddDeviceButton.ToolTip = Localizer.T("Settings.IoAddDeviceTip");
        IoRefreshButton.Content = Localizer.T("Settings.Refresh");
        IoRefreshButton.ToolTip = Localizer.T("Settings.IoRefreshTip");
        IoColName.Header = Localizer.T("Settings.IoColName");
        IoColHost.Header = Localizer.T("Settings.IoColHost");
        IoColEndpoint.Header = Localizer.T("Settings.IoColEndpoint");
        IoColEnabled.Header = Localizer.T("Settings.IoColEnabled");
        IoColPoll.Header = Localizer.T("Settings.IoColPoll");
        IoToggleDeviceButton.Content = Localizer.T("Settings.Toggle");
        IoToggleDeviceButton.ToolTip = Localizer.T("Settings.IoToggleDeviceTip");
        IoDeleteDeviceButton.Content = Localizer.T("Settings.IoDeleteModule");
        IoDeleteDeviceButton.ToolTip = Localizer.T("Settings.IoDeleteModuleTip");
        IoChannelHeading.Text = Localizer.T("Settings.IoChannelHeading");
        IoIndexBox.ToolTip = Localizer.T("Settings.IoIndexTip");
        IoChannelNameBox.ToolTip = Localizer.T("Settings.IoChannelNameTip");
        IoDebounceBox.ToolTip = Localizer.T("Settings.IoDebounceTip");
        IoNcBox.Content = Localizer.T("Settings.IoNc");
        IoCameraCombo.ToolTip = Localizer.T("Settings.IoCameraTip");
        IoAddChannelButton.Content = Localizer.T("Settings.IoAddChannel");
        IoAddChannelButton.ToolTip = Localizer.T("Settings.IoAddChannelTip");
        IoColDirection.Header = Localizer.T("Settings.IoColDirection");
        IoColChannelName.Header = Localizer.T("Settings.IoColChannelName");
        IoColChannelEnabled.Header = Localizer.T("Settings.IoColChannelEnabled");
        IoColDebounce.Header = Localizer.T("Settings.IoColDebounce");
        IoColCamera.Header = Localizer.T("Settings.IoColCamera");
        IoToggleChannelButton.Content = Localizer.T("Settings.IoToggleChannel");
        IoToggleChannelButton.ToolTip = Localizer.T("Settings.IoToggleChannelTip");
        IoDeleteChannelButton.Content = Localizer.T("Settings.IoDeleteChannel");
        IoDeleteChannelButton.ToolTip = Localizer.T("Settings.IoDeleteChannelTip");
        IoDoTestHeading.Text = Localizer.T("Settings.IoDoTestHeading");
        DoOnButton.ToolTip = Localizer.T("Settings.DoOnTip");
        DoOffButton.ToolTip = Localizer.T("Settings.DoOffTip");
        IoDoHint.Text = Localizer.T("Settings.IoDoHint");

        // 地圖
        MapTitle.Text = Localizer.T("Settings.MapTitle");
        MapDesc.Text = Localizer.T("Settings.MapDesc");
        MapDesc2.Text = Localizer.T("Settings.MapDesc2");
        MapSectionHeading.Text = Localizer.T("Settings.MapSectionHeading");
        MapNameBox.ToolTip = Localizer.T("Settings.MapNameTip");
        MapNameLabel.Text = Localizer.T("Settings.MapNameLabel");
        MapImageBox.ToolTip = Localizer.T("Settings.MapImageTip");
        MapBrowseButton.Content = Localizer.T("Settings.Browse");
        MapBrowseButton.ToolTip = Localizer.T("Settings.MapBrowseTip");
        MapAddButton.Content = Localizer.T("Settings.MapAdd");
        MapAddButton.ToolTip = Localizer.T("Settings.MapAddTip");
        MapRefreshButton.Content = Localizer.T("Settings.Refresh");
        MapRefreshButton.ToolTip = Localizer.T("Settings.MapRefreshTip");
        MapColName.Header = Localizer.T("Settings.MapColName");
        MapColSize.Header = Localizer.T("Settings.MapColSize");
        MapColEnabled.Header = Localizer.T("Settings.MapColEnabled");
        MapColOrder.Header = Localizer.T("Settings.MapColOrder");
        MapColScale.Header = Localizer.T("Settings.MapColScale");
        MapToggleButton.Content = Localizer.T("Settings.Toggle");
        MapToggleButton.ToolTip = Localizer.T("Settings.MapToggleTip");
        MapAutoLayoutButton.Content = Localizer.T("Settings.MapAutoLayout");
        MapAutoLayoutButton.ToolTip = Localizer.T("Settings.MapAutoLayoutTip");
        MapDeleteButton.Content = Localizer.T("Settings.MapDelete");
        MapDeleteButton.ToolTip = Localizer.T("Settings.MapDeleteTip");
        MapScaleLabel.Text = Localizer.T("Settings.MapScaleLabel");
        MapScaleBox.ToolTip = Localizer.T("Settings.MapScaleTip");
        MapApplyScaleButton.Content = Localizer.T("Settings.MapApplyScale");
        MapApplyScaleButton.ToolTip = Localizer.T("Settings.MapApplyScaleTip");
        MapScaleHint.Text = Localizer.T("Settings.MapScaleHint");
        MapPinHeading.Text = Localizer.T("Settings.MapPinHeading");
        MapPinChannelCombo.ToolTip = Localizer.T("Settings.MapPinChannelTip");
        MapPinKindLabel.Text = Localizer.T("Settings.MapPinKindLabel");
        MapPinDeviceLabel.Text = Localizer.T("Settings.MapPinDeviceLabel");
        MapPlacePinButton.Content = Localizer.T("Settings.MapPlacePin");
        MapPinHelpText.Text = Localizer.T("Settings.MapPinHelp");
        MapPinHint.Text = Localizer.T("Settings.MapPinEditHint");

        // 圖層選單以 Tag（camera／di／do）為型別權威；Content 只是顯示文字，切語言時跟著換。
        foreach (var item in MapPinKindCombo.Items.OfType<ComboBoxItem>())
        {
            item.Content = (item.Tag as string) switch
            {
                "di" => Localizer.T("Settings.MapPinKindDi"),
                "do" => Localizer.T("Settings.MapPinKindDo"),
                _ => Localizer.T("Settings.MapPinKindCamera"),
            };
        }
        MapPinAngleLabel.Text = Localizer.T("Settings.MapPinAngleLabel");
        MapPinAngleBox.ToolTip = Localizer.T("Settings.MapPinAngleTip");
        MapPinFovBox.ToolTip = Localizer.T("Settings.MapPinFovTip");
        MapPinDepthLabel.Text = Localizer.T("Settings.MapPinDepthLabel");
        MapPinDepthBox.ToolTip = Localizer.T("Settings.MapPinDepthTip");
        MapApplyGeometryButton.Content = Localizer.T("Settings.MapApplyGeometry");
        MapApplyGeometryButton.ToolTip = Localizer.T("Settings.MapApplyGeometryTip");
        MapApplyGeometryHint.Text = Localizer.T("Settings.MapApplyGeometryHint");
        MapPinColType.Header = Localizer.T("Settings.MapPinColType");
        MapPinColChannel.Header = Localizer.T("Settings.MapPinColChannel");
        MapPinColPosition.Header = Localizer.T("Settings.MapPinColPosition");
        MapPinColAngle.Header = Localizer.T("Settings.MapPinColAngle");
        MapPinColDepth.Header = Localizer.T("Settings.MapPinColDepth");
        MapPinColBearing.Header = Localizer.T("Settings.MapPinColBearing");
        MapPinColEnabled.Header = Localizer.T("Settings.MapPinColEnabled");
        MapPinColName.Header = Localizer.T("Settings.MapPinColName");
        MapDeletePinButton.Content = Localizer.T("Settings.MapDeletePin");
        MapDeletePinButton.ToolTip = Localizer.T("Settings.MapDeletePinTip");

        // 身份與權限
        UsersTitle.Text = Localizer.T("Settings.UsersTitle");
        UsersDesc.Text = Localizer.T("Settings.UsersDesc");
        AuthEnabledBox.Content = Localizer.T("Settings.AuthEnabled");
        AuthThresholdLabel.Text = Localizer.T("Settings.AuthThresholdLabel");
        AuthLockLabel.Text = Localizer.T("Settings.AuthLockLabel");
        AuthMinutesLabel.Text = Localizer.T("Settings.AuthMinutesLabel");
        AuthApplyButton.Content = Localizer.T("Settings.AuthApply");
        AuthApplyButton.ToolTip = Localizer.T("Settings.AuthApplyTip");
        UsersHeading.Text = Localizer.T("Settings.UsersHeading");
        UserAddNameBox.ToolTip = Localizer.T("Settings.UserNameTip");
        UserAddPasswordBox.ToolTip = Localizer.T("Settings.PasswordLabel");
        UserAddButton.Content = Localizer.T("Settings.UserAdd");
        UserAddButton.ToolTip = Localizer.T("Settings.UserAddTip");
        UserColName.Header = Localizer.T("Settings.UserColName");
        UserColRole.Header = Localizer.T("Settings.UserColRole");
        UserColEnabled.Header = Localizer.T("Settings.UserColEnabled");
        UserColFailed.Header = Localizer.T("Settings.UserColFailed");
        UserColLastLogin.Header = Localizer.T("Settings.UserColLastLogin");
        UserToggleButton.Content = Localizer.T("Settings.Toggle");
        UserToggleButton.ToolTip = Localizer.T("Settings.UserToggleTip");
        UserUnlockButton.Content = Localizer.T("Settings.UserUnlock");
        UserUnlockButton.ToolTip = Localizer.T("Settings.UserUnlockTip");
        UserDeleteButton.Content = Localizer.T("Settings.UserDelete");
        UserDeleteButton.ToolTip = Localizer.T("Settings.UserDeleteTip");
        EntHeading.Text = Localizer.T("Settings.EntHeading");
        EntDesc.Text = Localizer.T("Settings.EntDesc");
        EntNameBox.ToolTip = Localizer.T("Settings.EntNameTip");
        EntIssuerBox.ToolTip = Localizer.T("Settings.EntIssuerTip");
        EntAudienceBox.ToolTip = Localizer.T("Settings.EntAudienceTip");
        EntRoleClaimLabel.Text = Localizer.T("Settings.EntRoleClaimLabel");
        EntAdminGroupsLabel.Text = Localizer.T("Settings.EntAdminGroupsLabel");
        EntJwksLabel.Text = Localizer.T("Settings.EntJwksLabel");
        EntAddButton.Content = Localizer.T("Settings.EntAdd");
        EntAddButton.ToolTip = Localizer.T("Settings.EntAddTip");
        EntToggleButton.Content = Localizer.T("Settings.Toggle");
        EntToggleButton.ToolTip = Localizer.T("Settings.EntToggleTip");
        EntDeleteButton.Content = Localizer.T("Settings.EntDelete");
        EntDeleteButton.ToolTip = Localizer.T("Settings.EntDeleteTip");
        EntColName.Header = Localizer.T("Settings.EntColName");
        EntColKind.Header = Localizer.T("Settings.EntColKind");
        EntColEnabled.Header = Localizer.T("Settings.EntColEnabled");
        EntColSummary.Header = Localizer.T("Settings.EntColSummary");

        // 備份與異地
        BackupTitle.Text = Localizer.T("Settings.BackupTitle");
        BackupDesc.Text = Localizer.T("Settings.BackupDesc");
        BackupEnabledBox.Content = Localizer.T("Settings.BackupEnabled");
        BackupEnabledBox.ToolTip = Localizer.T("Settings.BackupEnabledTip");
        BackupTargetLabel.Text = Localizer.T("Settings.BackupTargetLabel");
        BackupBrowseButton.Content = Localizer.T("Settings.BrowseEllipsis");
        BackupBrowseButton.ToolTip = Localizer.T("Settings.BackupBrowseTip");
        BackupIntervalLabel.Text = Localizer.T("Settings.BackupIntervalLabel");
        ApplyBackupButton.Content = Localizer.T("Settings.SaveConfig");
        ApplyBackupButton.ToolTip = Localizer.T("Settings.BackupSaveTip");
        BackupNowButton.Content = Localizer.T("Settings.BackupNow");
        BackupNowButton.ToolTip = Localizer.T("Settings.BackupNowTip");
        OffsiteHeading.Text = Localizer.T("Settings.OffsiteHeading");
        OffsiteSourceBrowseButton.Content = Localizer.T("Settings.SourceEllipsis");
        OffsiteSourceBrowseButton.ToolTip = Localizer.T("Settings.OffsiteSourceTip");
        OffsiteDestBrowseButton.Content = Localizer.T("Settings.TargetEllipsis");
        OffsiteDestBrowseButton.ToolTip = Localizer.T("Settings.OffsiteDestTip");
        OffsiteApplyButton.Content = Localizer.T("Settings.SaveConfig");
        OffsiteApplyButton.ToolTip = Localizer.T("Settings.OffsiteSaveTip");
        OffsiteEnabledBox.Content = Localizer.T("Settings.OffsiteEnabled");
        OffsiteEnabledBox.ToolTip = Localizer.T("Settings.OffsiteEnabledTip");
        OffsiteRunNowButton.Content = Localizer.T("Settings.OffsiteRunNow");
        OffsiteRunNowButton.ToolTip = Localizer.T("Settings.OffsiteRunNowTip");
        BackupLogLabel.Text = Localizer.T("Settings.BackupLogLabel");
        BackupColTime.Header = Localizer.T("Settings.BackupColTime");
        BackupColTarget.Header = Localizer.T("Settings.BackupColTarget");
        BackupColCopied.Header = Localizer.T("Settings.BackupColCopied");
        BackupColFailed.Header = Localizer.T("Settings.BackupColFailed");
        BackupColAdvanced.Header = Localizer.T("Settings.BackupColAdvanced");
        VersionHeading.Text = Localizer.T("Settings.VersionHeading");
        SnapshotSettingsButton.Content = Localizer.T("Settings.VersionSnapshot");
        SnapshotSettingsButton.ToolTip = Localizer.T("Settings.VersionSnapshotTip");
        ExportSettingsButton.Content = Localizer.T("Settings.VersionExport");
        ExportSettingsButton.ToolTip = Localizer.T("Settings.VersionExportTip");
        ImportSettingsButton.Content = Localizer.T("Settings.VersionImport");
        ImportSettingsButton.ToolTip = Localizer.T("Settings.VersionImportTip");
        RestoreSettingsButton.Content = Localizer.T("Settings.VersionRestore");
        RestoreSettingsButton.ToolTip = Localizer.T("Settings.VersionRestoreTip");
        VersionColTime.Header = Localizer.T("Settings.VersionColTime");
        VersionColKeys.Header = Localizer.T("Settings.VersionColKeys");
        VersionColReason.Header = Localizer.T("Settings.VersionColReason");
        VersionColActor.Header = Localizer.T("Settings.VersionColActor");

        // 關於
        AboutTitle.Text = Localizer.T("Settings.AboutTitle");
        AboutStorageLabel.Text = Localizer.T("Settings.AboutStorageLabel");
        AboutVersionNote.Text = Localizer.T("Settings.AboutVersionNote");

        // 授權閘門的說明文字會依語言改寫，須最後重套（否則停留在上一種語言）。
        ApplyLicenseVisibility(new LicenseUiGate(_store));
    }

    /// <summary>載入監看硬體解碼設定（<c>decode.hwaccel</c>）：空／off 選「off」項。</summary>
    private void ReloadHwAccel()
    {
        var tag = RtspClient.NormalizeHwAccel(_settings.Get(HwAccelKey)) ?? "off";
        foreach (var obj in HwAccelCombo.Items)
        {
            if (obj is ComboBoxItem item && (item.Tag as string) == tag)
            {
                HwAccelCombo.SelectedItem = item;
                return;
            }
        }

        HwAccelCombo.SelectedIndex = 0;
    }

    private void OnHwAccelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HwAccelCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.Set(HwAccelKey, tag);
        }
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 期間 SelectedIndex 變更也會走到這裡，此時具名欄位尚未
        // 全部指派；建構子稍後會自己呼叫一次 ApplyI18n，所以載入前直接略過。
        if (!IsLoaded)
        {
            return;
        }

        if (LangCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
        {
            Localizer.SetLang(_settings, tag);
            ApplyI18n();
            // 設備下拉的項目文字是自行組的（「攝影機 X（#n）」），不在 ApplyI18n 範圍內，
            // 切語言後要重建一次，否則清單會停在上一種語言。
            try { ReloadMapPinChannelCombo(); } catch { }
            // 「關於」頁的文字不屬於 ApplyI18n 的範圍，切換語言時要自己重畫，
            // 否則自啟勾選框與狀態會留在上一種語言。
            FillAbout();
            if (Application.Current?.MainWindow is MainWindow mw)
            {
                mw.RefreshTitle();
            }
        }
    }

    private void OnOskToggleChanged(object sender, RoutedEventArgs e)
    {
        if (OskToggleCheck is null)
        {
            return;
        }
        Osk.Enabled = OskToggleCheck.IsChecked == true;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav is null || SettingsNav.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var visible = item.Content as string ?? string.Empty;
        PageGeneral.Visibility = visible == Localizer.T("Settings.NavGeneral") ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = visible == Localizer.T("Settings.NavDevices") ? Visibility.Visible : Visibility.Collapsed;
        PageStorage.Visibility = visible == Localizer.T("Settings.NavStorage") ? Visibility.Visible : Visibility.Collapsed;
        PageLicense.Visibility = visible == Localizer.T("Settings.NavLicense") ? Visibility.Visible : Visibility.Collapsed;
        PageLaunch.Visibility = visible == Localizer.T("Settings.NavLaunch") ? Visibility.Visible : Visibility.Collapsed;
        PageNotify.Visibility = visible == Localizer.T("Settings.NavNotify") ? Visibility.Visible : Visibility.Collapsed;
        PageRules.Visibility = visible == Localizer.T("Settings.NavRules") ? Visibility.Visible : Visibility.Collapsed;
        PageIo.Visibility = visible == Localizer.T("Settings.NavIo") ? Visibility.Visible : Visibility.Collapsed;
        PageMap.Visibility = visible == Localizer.T("Settings.NavMap") ? Visibility.Visible : Visibility.Collapsed;
        PageUsers.Visibility = visible == Localizer.T("Settings.NavUsers") ? Visibility.Visible : Visibility.Collapsed;
        PageBackup.Visibility = visible == Localizer.T("Settings.NavBackup") ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = visible == Localizer.T("Settings.NavNetwork") ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = visible == Localizer.T("Settings.NavAbout") ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>從設定中心開啟攝影機管理（與主視窗共用同一顆視窗；僅 admin）。</summary>
    private void OnOpenCameraManagerClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        // 傳入 ChannelManager 讓「狀態」欄能顯示各攝影機底下頻道的即時連線狀態；
        // 刪除設備會 CASCADE 刪掉其頻道，故一併把頻道異動通知帶進去。
        new CameraManagerWindow(_store, _manager, _channelsChanged) { Owner = this }.Show();
    }

    /// <summary>從設定中心開啟頻道管理（與主視窗共用同一顆視窗；僅 admin）。</summary>
    private void OnOpenChannelManagerClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        new ChannelManagerWindow(_store, _channelsChanged) { Owner = this }.Show();
    }

    /// <summary>從設定中心開啟監看群組（獨立功能；僅 admin）。</summary>
    private void OnOpenMonitorGroupsClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            return;
        }

        new MonitorGroupWindow(_store, _channelsChanged) { Owner = this }.Show();
    }

    private void ReloadBackup()
    {
        BackupEnabledBox.IsChecked = _settings.Get("backup.enabled") == "1";
        var target = _settings.Get("backup.target");
        if (!string.IsNullOrWhiteSpace(target))
        {
            BackupTargetBox.Text = target;
        }

        var interval = _settings.GetDoubleOrDefault("backup.interval_hours", 24);
        BackupIntervalBox.Text = interval > 0 ? interval.ToString(CultureInfo.InvariantCulture) : "24";
        ReloadBackupLog();
        ReloadSettingsVersions();
    }

    /// <summary>將備份設定（啟用/目標/間隔）寫入 app_settings；參數不合法時回傳 false。</summary>
    private bool SaveBackupSettings()
    {
        _settings.Set("backup.enabled", BackupEnabledBox.IsChecked == true ? "1" : "0");
        _settings.Set("backup.target", BackupTargetBox.Text.Trim());
        if (!double.TryParse(BackupIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) ||
            interval <= 0)
        {
            BackupStatusText.Text = Localizer.T("Settings.BackupIntervalInvalid");
            return false;
        }

        _settings.Set("backup.interval_hours", interval.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private void OnSaveBackupClicked(object sender, RoutedEventArgs e)
    {
        if (SaveBackupSettings())
        {
            BackupStatusText.Text = Localizer.T("Settings.SettingsSaved");
        }
    }

    /// <summary>載入分享服務設定（M51，§14.7 #4）。</summary>
    private void ReloadShare()
    {
        ShareEnabledBox.IsChecked = _settings.Get(ShareHost.EnabledKey) == "1";
        SharePortBox.Text = _settings.Get(ShareHost.PortKey) ?? ShareHost.DefaultPort.ToString(CultureInfo.InvariantCulture);
        ShareBaseUrlBox.Text = _settings.Get(ShareHost.BaseUrlKey) ?? string.Empty;
        ShareServiceStatusText.Text = _shareHost?.IsRunning == true
            ? string.Format(Localizer.T("Settings.ShareRunning"), _shareHost.Port)
            : Localizer.T("Settings.ShareStopped");
    }

    /// <summary>儲存並即時套用分享服務設定（M51，§14.7 #4）。</summary>
    private void OnApplyShareClicked(object sender, RoutedEventArgs e)
    {
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Remote))
        {
            ShareServiceStatusText.Text = gate.DenialMessage(LicenseFeatures.Remote);
            return;
        }

        if (!int.TryParse(SharePortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            ShareServiceStatusText.Text = Localizer.T("Settings.SharePortInvalid");
            return;
        }

        _settings.Set(ShareHost.EnabledKey, ShareEnabledBox.IsChecked == true ? "1" : "0");
        _settings.Set(ShareHost.PortKey, port.ToString(CultureInfo.InvariantCulture));
        _settings.Set(ShareHost.BaseUrlKey, ShareBaseUrlBox.Text.Trim());

        ShareServiceStatusText.Text = _shareHost is null
            ? Localizer.T("Settings.ShareSavedNextStart")
            : _shareHost.ApplySettings(_settings);
    }

    /// <summary>載入「網路」頁設定：內嵌 Web 服務（M305）與 NTP 對時（M305／引擎 M68）。</summary>
    private void ReloadNetwork()
    {
        WebEnabledBox.IsChecked = _settings.Get(WebHost.EnabledKey) == "1";
        WebPortBox.Text = _settings.Get(WebHost.PortKey) ?? WebHost.DefaultPort.ToString(CultureInfo.InvariantCulture);
        WebAllowNoKeyBox.IsChecked = _settings.Get(WebHost.AllowNoKeyKey) == "1";
        WebServiceStatusText.Text = _webHost?.IsRunning == true
            ? string.Format(Localizer.T("Settings.WebRunning"), _webHost.Port)
            : Localizer.T("Settings.WebStopped");

        NtpEnabledBox.IsChecked = _settings.Get(NtpSync.EnabledKey) == "1";
        NtpServerBox.Text = NtpSync.ReadServer(_settings);
        var last = NtpSync.ReadLast(_settings);
        NtpStatusText.Text = last is { } value
            ? string.Format(Localizer.T("Settings.NtpLastSync"), value.ServerUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), value.OffsetMs)
            : Localizer.T("Settings.NtpNeverSynced");
    }

    /// <summary>儲存並即時套用內嵌 Web 服務設定（M305）。</summary>
    private void OnApplyWebClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WebPortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            WebServiceStatusText.Text = Localizer.T("Settings.WebPortInvalid");
            return;
        }

        _settings.Set(WebHost.EnabledKey, WebEnabledBox.IsChecked == true ? "1" : "0");
        _settings.Set(WebHost.PortKey, port.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WebHost.AllowNoKeyKey, WebAllowNoKeyBox.IsChecked == true ? "1" : "0");

        WebServiceStatusText.Text = _webHost is null
            ? Localizer.T("Settings.WebSavedNextStart")
            : _webHost.ApplySettings(_settings);
    }

    /// <summary>立即向 NTP 伺服器對時：查詢、記錄，並盡力校正系統時鐘（M305）。</summary>
    private async void OnNtpSyncClicked(object sender, RoutedEventArgs e)
    {
        var server = NtpServerBox.Text.Trim();
        _settings.Set(NtpSync.EnabledKey, NtpEnabledBox.IsChecked == true ? "1" : "0");
        _settings.Set(NtpSync.ServerKey, server);

        NtpSyncButton.IsEnabled = false;
        NtpStatusText.Text = Localizer.T("Settings.NtpSyncing");
        try
        {
            var outcome = await NtpSync.SyncAsync(_settings, server);
            if (!outcome.Ok)
            {
                NtpStatusText.Text = Localizer.T("Settings.NtpSyncFailed");
                return;
            }

            // 無權限就地調整系統時鐘不算失敗：偏移量仍然有用，且已改請 Agent 服務代為校時；
            // 明確告訴操作員目前是哪一種結果，而不是丟一句看不懂的 Win32 訊息。
            var clock = outcome.ClockSet
                ? Localizer.T("Settings.NtpClockSet")
                : outcome.ServiceRequested
                    ? Localizer.T("Settings.NtpClockSetDelegated")
                    : string.Format(Localizer.T("Settings.NtpClockSetDenied"), outcome.Error ?? string.Empty);
            NtpStatusText.Text = string.Format(
                Localizer.T("Settings.NtpSynced"),
                (long)outcome.Offset.TotalMilliseconds,
                outcome.RoundTrip.TotalMilliseconds,
                outcome.Stratum,
                clock);
        }
        finally
        {
            NtpSyncButton.IsEnabled = true;
        }
    }

    private void OnBackupBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("Settings.ChooseBackupTarget") };
        if (dlg.ShowDialog() == true)
        {
            BackupTargetBox.Text = dlg.FolderName;
            BackupStatusText.Text = "";
        }
    }

    private async void OnBackupNowClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveBackupSettings())
        {
            return;
        }

        var target = BackupTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            BackupStatusText.Text = Localizer.T("Settings.BackupTargetRequired");
            return;
        }

        BackupNowButton.IsEnabled = false;
        BackupStatusText.Text = Localizer.T("Settings.BackupRunning");
        try
        {
            var result = await Task.Run(() => new BackupService(_store).Run(_recordingsRoot, target));
            BackupStatusText.Text = result.Failed == 0
                ? string.Format(Localizer.T("Settings.BackupDoneOk"), result.Scanned, result.Copied, FormatBytes(result.CopiedBytes))
                : string.Format(Localizer.T("Settings.BackupDoneFail"), result.Scanned, result.Copied, result.Failed);
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = string.Format(Localizer.T("Settings.BackupFailed"), ex.Message);
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
            ReloadBackupLog();
        }
    }

    private void ReloadBackupLog()
    {
        var runs = new BackupRepository(_store).ListRuns(targetRoot: null, take: 20);
        BackupLogList.ItemsSource = runs.Select(r => new BackupRow(
            r.RunAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            r.TargetRoot,
            r.CopiedCount.ToString(CultureInfo.InvariantCulture),
            r.FailedCount.ToString(CultureInfo.InvariantCulture),
            r.CheckpointUtc.HasValue ? Localizer.T("Settings.Yes") : Localizer.T("Settings.No"))).ToList();
    }

    // ---------- 設定版本快照與回滾（M308，§9.5） ----------

    /// <summary>重新載入設定版本清單（新到舊，最多 20 筆）。</summary>
    private void ReloadSettingsVersions()
    {
        SettingsVersionList.ItemsSource = _settings.ListVersions(20)
            .Select(v => new VersionRow(
                v.Id,
                v.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                v.KeyCount.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(v.Reason) ? Localizer.T("Settings.VersionNoReason") : v.Reason,
                v.Actor))
            .ToList();
    }

    /// <summary>設定版本的破壞性操作需管理員；非管理員時在版本區狀態列說明。</summary>
    private bool VersionRequireAdmin()
    {
        if (SessionContext.IsAdmin)
        {
            return true;
        }

        SettingsVersionStatusText.Text = Localizer.T("Settings.AdminOnly");
        return false;
    }

    private long? SelectedVersionId()
        => SettingsVersionList.SelectedItem is VersionRow row ? row.Id : null;

    private void OnSnapshotSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!VersionRequireAdmin())
        {
            return;
        }

        _settings.Snapshot(Localizer.T("Settings.VersionManualReason"));
        ReloadSettingsVersions();
        SettingsVersionStatusText.Text = Localizer.T("Settings.VersionSnapshotDone");
    }

    private void OnExportSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!VersionRequireAdmin())
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localizer.T("Settings.VersionExportTitle"),
            Filter = Localizer.T("Settings.VersionFileFilter"),
            FileName = "helivms-settings.json",
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, _settings.ExportJson());
            SettingsVersionStatusText.Text = Localizer.T("Settings.VersionExportDone");
        }
        catch (Exception ex)
        {
            SettingsVersionStatusText.Text = string.Format(Localizer.T("Settings.VersionFailed"), ex.Message);
        }
    }

    private void OnImportSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!VersionRequireAdmin())
        {
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Settings.VersionExportTitle"),
            Filter = Localizer.T("Settings.VersionFileFilter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(dlg.FileName);
        }
        catch (Exception ex)
        {
            SettingsVersionStatusText.Text = string.Format(Localizer.T("Settings.VersionFailed"), ex.Message);
            return;
        }

        if (MessageBox.Show(this, Localizer.T("Settings.VersionImportConfirm"),
                Localizer.T("Settings.VersionImport"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // 匯入前先自動快照：匯錯檔也能一鍵回到匯入前的狀態。
            _settings.Snapshot(Localizer.T("Settings.VersionPreImportReason"));
            var count = _settings.ImportJson(json);
            ReloadBackup();
            SettingsVersionStatusText.Text = string.Format(Localizer.T("Settings.VersionImportDone"), count);
        }
        catch (Exception ex)
        {
            SettingsVersionStatusText.Text = string.Format(Localizer.T("Settings.VersionFailed"), ex.Message);
        }
    }

    private void OnRestoreSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!VersionRequireAdmin())
        {
            return;
        }

        var id = SelectedVersionId();
        if (id is null)
        {
            SettingsVersionStatusText.Text = Localizer.T("Settings.VersionSelectFirst");
            return;
        }

        if (MessageBox.Show(this, Localizer.T("Settings.VersionRestoreConfirm"),
                Localizer.T("Settings.VersionRestore"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // 還原前先自動快照：還原到錯的版本時仍能退回。
            _settings.Snapshot(Localizer.T("Settings.VersionPreRestoreReason"));
            _settings.Restore(id.Value);
            ReloadBackup();
            SettingsVersionStatusText.Text = Localizer.T("Settings.VersionRestoreDone");
        }
        catch (Exception ex)
        {
            SettingsVersionStatusText.Text = string.Format(Localizer.T("Settings.VersionFailed"), ex.Message);
        }
    }

    // ---------- 異地備援複製（M55，§4.4 異地） ----------

    private const string OffsiteEnabledKey = "offsite.enabled";
    private const string OffsiteSourceKey = "offsite.source";
    private const string OffsiteDestKey = "offsite.destination";
    private const string OffsiteIntervalKey = "offsite.interval_minutes";
    private const int DefaultOffsiteIntervalMinutes = 1440;
    private readonly OffsiteReplicationRepository _offsite = null!;

    /// <summary>載入異地複製設定（app_settings＋offsite_jobs）。</summary>
    private void ReloadOffsite()
    {
        OffsiteEnabledBox.IsChecked = _settings.Get(OffsiteEnabledKey) == "1";
        var source = _settings.Get(OffsiteSourceKey);
        if (!string.IsNullOrWhiteSpace(source))
        {
            OffsiteSourceBox.Text = source;
        }

        var dest = _settings.Get(OffsiteDestKey);
        if (!string.IsNullOrWhiteSpace(dest))
        {
            OffsiteDestBox.Text = dest;
        }

        var interval = (int)_settings.GetDoubleOrDefault(OffsiteIntervalKey, DefaultOffsiteIntervalMinutes);
        OffsiteIntervalBox.Text = interval > 0 ? interval.ToString(CultureInfo.InvariantCulture) : DefaultOffsiteIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        ReloadOffsiteStatus();
    }

    /// <summary>刷新異地複製狀態列（最後執行／結果／連續失敗）。</summary>
    private void ReloadOffsiteStatus()
    {
        var job = _offsite.List().FirstOrDefault();
        if (job is null)
        {
            OffsiteStatusText.Text = Localizer.T("Settings.OffsiteNone");
            return;
        }

        var runAt = job.LastRunUtc.HasValue
            ? job.LastRunUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : Localizer.T("Settings.OffsiteNever");
        var result = job.LastResult ?? "—";
        var error = job.LastError is null ? "" : string.Format(Localizer.T("Settings.OffsiteErrorSuffix"), job.LastError);
        OffsiteStatusText.Text = string.Format(
            Localizer.T("Settings.OffsiteStatus"),
            result,
            runAt,
            job.ConsecutiveFailures,
            error,
            job.SourcePath,
            job.DestinationPath);
    }

    private void OnOffsiteBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = (sender as Button)?.Tag as string == "destination" ? Localizer.T("Settings.ChooseOffsiteTarget") : Localizer.T("Settings.ChooseOffsiteSource")
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        if ((sender as Button)?.Tag as string == "destination")
        {
            OffsiteDestBox.Text = dlg.FolderName;
        }
        else
        {
            OffsiteSourceBox.Text = dlg.FolderName;
        }

        OffsiteStatusText.Text = "";
    }

    private bool SaveOffsiteSettings()
    {
        if (!int.TryParse(OffsiteIntervalBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) ||
            interval <= 0)
        {
            OffsiteStatusText.Text = Localizer.T("Settings.OffsiteIntervalInvalid");
            return false;
        }

        var source = OffsiteSourceBox.Text.Trim();
        var dest = OffsiteDestBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
        {
            OffsiteStatusText.Text = Localizer.T("Settings.OffsiteBothRequired");
            return false;
        }

        var enabled = OffsiteEnabledBox.IsChecked == true;
        _settings.Set(OffsiteEnabledKey, enabled ? "1" : "0");
        _settings.Set(OffsiteSourceKey, source);
        _settings.Set(OffsiteDestKey, dest);
        _settings.Set(OffsiteIntervalKey, interval.ToString(CultureInfo.InvariantCulture));
        _offsite.Upsert(source, dest, interval, enabled);
        ReloadOffsiteStatus();
        return true;
    }

    private void OnSaveOffsiteClicked(object sender, RoutedEventArgs e)
    {
        if (SaveOffsiteSettings())
        {
            OffsiteStatusText.Text += " " + Localizer.T("Settings.SettingsSaved");
        }
        else
        {
            ReloadOffsiteStatus();
        }
    }

    private async void OnOffsiteRunNowClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveOffsiteSettings())
        {
            return;
        }

        var job = _offsite.List().FirstOrDefault();
        if (job is null)
        {
            OffsiteStatusText.Text = Localizer.T("Settings.OffsiteJobMissing");
            return;
        }

        OffsiteRunNowButton.IsEnabled = false;
        OffsiteStatusText.Text = Localizer.T("Settings.OffsiteRunning");
        try
        {
            var ok = await Task.Run(() => new OffsiteReplicationService(_offsite).RunOnce(job.Id));
            OffsiteStatusText.Text = ok
                ? Localizer.T("Settings.OffsiteDone")
                : string.Format(Localizer.T("Settings.OffsiteFailed"), _offsite.List().FirstOrDefault()?.LastError);
        }
        catch (Exception ex)
        {
            OffsiteStatusText.Text = string.Format(Localizer.T("Settings.OffsiteFailed"), ex.Message);
        }
        finally
        {
            OffsiteRunNowButton.IsEnabled = true;
            ReloadOffsiteStatus();
        }
    }

    private void ReloadRules()
    {
        RuleChannelCombo.Items.Clear();
        RuleChannelCombo.Items.Add(new ComboBoxItem { Content = Localizer.T("Settings.RuleAny"), Tag = null });
        foreach (var c in new ChannelRepository(_store).List())
        {
            RuleChannelCombo.Items.Add(new ComboBoxItem { Content = $"{c.Name}（#{c.Id}）", Tag = c.Id });
        }

        RuleChannelCombo.SelectedIndex = 0;

        RuleList.ItemsSource = _rules.ListAll().Select(r => new RuleRow(
            r.Id,
            r.Name,
            r.EventType ?? Localizer.T("Settings.RuleAny"),
            r.ChannelId is { } cid ? $"#{cid}" : Localizer.T("Settings.RuleAny"),
            r.Keyword ?? Localizer.T("Settings.RuleAny"),
            r.MatchEventTypes ?? Localizer.T("Settings.All"),
            r.FrameMinutes > 0 ? string.Format(Localizer.T("Settings.RuleAggWindow"), r.FrameMinutes, r.MinEventsInWindow) : Localizer.T("Settings.RulePerItem"),
            FormatRuleChannels(r.Channels),
            r.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"))).ToList();
    }

    /// <summary>M39：載入遮蔽偵測開關（app_settings `detect.tamper.enabled`）。</summary>
    private void ReloadTamper()
    {
        TamperEnabledBox.IsChecked = string.Equals(_settings.Get(TamperKey), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>M39：切換即寫入 app_settings（重新連線頻道後生效）。ai 旗標限定（M218）。</summary>
    private void OnTamperToggled(object sender, RoutedEventArgs e)
    {
        // TamperEnabledBox 以 ai 旗標隱藏（M209／§19.4）；藏了還得擋 handler。
        // 未授權時只把勾選復原、不覆寫既有設定，避免連設定頁都會改壞已存的設定值。
        if (!new LicenseUiGate(_store).Allows(LicenseFeatures.Ai))
        {
            if (TamperEnabledBox.IsChecked == true)
            {
                TamperEnabledBox.IsChecked = false;
            }

            return;
        }

        _settings.Set(TamperKey, TamperEnabledBox.IsChecked == true ? "true" : "false");
    }

    private IoDeviceRow? SelectedIoDevice => IoDeviceList.SelectedItem as IoDeviceRow;

    /// <summary>M40：重載 IO 模組清單、相機下拉與 DO 測試下拉。</summary>
    private void ReloadIo()
    {
        var devices = _io.ListDevices();
        IoDeviceList.ItemsSource = devices.Select(d => new IoDeviceRow(
            d.Id,
            d.Name,
            d.Host,
            $"{d.Port}/{d.UnitId}",
            d.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
            d.PollMs)).ToList();

        IoDoDeviceCombo.Items.Clear();
        foreach (var d in devices)
        {
            IoDoDeviceCombo.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        }

        if (IoDoDeviceCombo.Items.Count > 0)
        {
            IoDoDeviceCombo.SelectedIndex = 0;
            ReloadIoDoChannels();
        }
        else
        {
            IoDoChannelCombo.Items.Clear();
        }

        ReloadIoCameraCombo();
        ReloadIoChannels();
    }

    private void ReloadIoCameraCombo()
    {
        IoCameraCombo.Items.Clear();
        IoCameraCombo.Items.Add(new ComboBoxItem { Content = Localizer.T("Settings.Unbound"), Tag = null });
        foreach (var c in new ChannelRepository(_store).List())
        {
            IoCameraCombo.Items.Add(new ComboBoxItem { Content = $"{c.Name}（#{c.Id}）", Tag = c.Id });
        }

        IoCameraCombo.SelectedIndex = 0;
    }

    /// <summary>M40：依目前選取模組重載通道清單（未選則顯示全部）。</summary>
    private void ReloadIoChannels()
    {
        var devId = SelectedIoDevice?.Id;
        var cameras = new ChannelRepository(_store).List().ToDictionary(c => c.Id, c => c.Name);
        var rows = (devId is int did
            ? _io.ListChannels(deviceId: did)
            : _io.ListChannels()).Select(ch => new IoChannelRow(
                ch.Id,
                ch.Direction,
                ch.IoIndex,
                ch.Name,
                ch.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
                ch.Polarity ? "NC" : "",
                ch.DebounceMs,
                ch.CameraId is { } cam && cameras.TryGetValue(cam, out var n) ? n : "—")).ToList();
        IoChannelList.ItemsSource = rows;
        IoReportText.Text = devId is not null && rows.Count == 0 ? Localizer.T("Settings.NoChannelsForModule") : " ";
    }

    private void OnIoAddDeviceClicked(object sender, RoutedEventArgs e)
    {
        var name = IoNameBox.Text.Trim();
        var host = IoHostBox.Text.Trim();
        if (name.Length == 0 || host.Length == 0)
        {
            IoReportText.Text = Localizer.T("Settings.ModuleNameHostRequired");
            return;
        }

        if (!int.TryParse(IoPortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            IoReportText.Text = Localizer.T("Settings.PortInvalid");
            return;
        }

        if (!int.TryParse(IoUnitBox.Text, out var unit) || unit is < 0 or > 247)
        {
            IoReportText.Text = Localizer.T("Settings.UnitIdInvalid");
            return;
        }

        if (!int.TryParse(IoPollBox.Text, out var pollMs) || pollMs < 50)
        {
            IoReportText.Text = Localizer.T("Settings.PollIntervalTooSmall");
            return;
        }

        _io.AddDevice(name, host, port, unit, pollMs);
        IoNameBox.Clear();
        IoHostBox.Clear();
        IoReportText.Text = string.Format(Localizer.T("Settings.ModuleAdded"), name);
        ReloadIo();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoRefreshClicked(object sender, RoutedEventArgs e) => ReloadIo();

    private void OnIoDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReloadIoChannels();
        ReloadIoDoChannels();
    }

    private void OnIoToggleDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIoDevice is not { } dev)
        {
            IoReportText.Text = Localizer.T("Settings.SelectModuleFirst");
            return;
        }

        var current = _io.GetDevice(dev.Id);
        if (current is null)
        {
            ReloadIo();
            return;
        }

        _io.SetDeviceEnabled(dev.Id, !current.Enabled);
        ReloadIo();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoDeleteDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIoDevice is not { } dev)
        {
            IoReportText.Text = Localizer.T("Settings.SelectModuleFirst");
            return;
        }

        _io.DeleteDevice(dev.Id);
        IoReportText.Text = string.Format(Localizer.T("Settings.ModuleDeleted"), dev.Id);
        ReloadIo();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIoDevice is not { } dev)
        {
            IoReportText.Text = Localizer.T("Settings.SelectModuleAddChannel");
            return;
        }

        if (IoDirCombo.SelectedItem is not ComboBoxItem dirItem)
        {
            IoReportText.Text = Localizer.T("Settings.DirectionRequired");
            return;
        }

        var direction = dirItem.Content as string ?? "DI";
        if (!int.TryParse(IoIndexBox.Text, out var idx) || idx is < 0 or > 65535)
        {
            IoReportText.Text = Localizer.T("Settings.IoIndexInvalid");
            return;
        }

        var name = IoChannelNameBox.Text.Trim();
        if (name.Length == 0)
        {
            IoReportText.Text = Localizer.T("Settings.ChannelNameRequired");
            return;
        }

        if (!int.TryParse(IoDebounceBox.Text, out var debounce) || debounce < 0)
        {
            IoReportText.Text = Localizer.T("Settings.DebounceInvalid");
            return;
        }

        int? cameraId = IoCameraCombo.SelectedItem is ComboBoxItem { Tag: int cam } ? cam : null;
        if (direction == "DI" && cameraId is null)
        {
            IoReportText.Text = Localizer.T("Settings.DiNeedsCamera");
            return;
        }

        try
        {
            _io.AddChannel(dev.Id, direction, idx, name, debounce, IoNcBox.IsChecked == true, cameraId);
        }
        catch (Exception ex)
        {
            IoReportText.Text = string.Format(Localizer.T("Settings.AddChannelFailed"), ex.Message);
            return;
        }

        IoIndexBox.Clear();
        IoChannelNameBox.Clear();
        IoNcBox.IsChecked = false;
        IoReportText.Text = string.Format(Localizer.T("Settings.ChannelAdded"), name, direction, idx);
        ReloadIoChannels();
        ReloadIoDoChannels();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoToggleChannelClicked(object sender, RoutedEventArgs e)
    {
        if (IoChannelList.SelectedItem is not IoChannelRow row)
        {
            IoReportText.Text = Localizer.T("Settings.SelectChannelFirst");
            return;
        }

        var current = _io.GetChannel(row.Id);
        if (current is null)
        {
            ReloadIoChannels();
            return;
        }

        _io.SetChannelEnabled(row.Id, !current.Enabled);
        ReloadIoChannels();
        ReloadIoDoChannels();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoDeleteChannelClicked(object sender, RoutedEventArgs e)
    {
        if (IoChannelList.SelectedItem is not IoChannelRow row)
        {
            IoReportText.Text = Localizer.T("Settings.SelectChannelFirst");
            return;
        }

        _io.DeleteChannel(row.Id);
        ReloadIoChannels();
        ReloadIoDoChannels();
        _ioHost?.RefreshAndStart();
    }

    private void ReloadIoDoChannels()
    {
        IoDoChannelCombo.Items.Clear();
        if (IoDoDeviceCombo.SelectedItem is not ComboBoxItem { Tag: int devId })
        {
            return;
        }

        foreach (var ch in _io.ListChannels(deviceId: devId, direction: "DO"))
        {
            IoDoChannelCombo.Items.Add(new ComboBoxItem { Content = $"[{ch.IoIndex}] {ch.Name}", Tag = ch.Id });
        }

        if (IoDoChannelCombo.Items.Count > 0)
        {
            IoDoChannelCombo.SelectedIndex = 0;
        }
    }

    private void OnIoDoDeviceChanged(object sender, SelectionChangedEventArgs e) => ReloadIoDoChannels();

    private async void OnIoDoWriteClicked(object sender, RoutedEventArgs e)
    {
        if (_ioHost is null)
        {
            IoDoStatusText.Text = Localizer.T("Settings.IoHostNotWired");
            return;
        }

        if (IoDoDeviceCombo.SelectedItem is not ComboBoxItem { Tag: int devId } ||
            IoDoChannelCombo.SelectedItem is not ComboBoxItem { Tag: int chId } ||
            sender is not Button btn)
        {
            IoDoStatusText.Text = Localizer.T("Settings.SelectModuleDoChannel");
            return;
        }

        var on = string.Equals(btn.Tag?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var ok = await _ioHost.WriteOutputAsync(devId, chId, on);
        IoDoStatusText.Text = ok
            ? string.Format(Localizer.T("Settings.DoSet"), chId, on ? "ON" : "OFF")
            : Localizer.T("Settings.DoWriteFailed");
    }

    private static string FormatRuleChannels(string? channels)
        => string.IsNullOrWhiteSpace(channels) ? Localizer.T("Settings.AllChannels") : string.Join("＋",
            channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private string? BuildRuleChannels()
    {
        var set = new List<string>();
        if (RuleChannelWebhookBox.IsChecked == true) set.Add("webhook");
        if (RuleChannelSmtpBox.IsChecked == true) set.Add("smtp");
        if (RuleChannelMqttBox.IsChecked == true) set.Add("mqtt");
        if (RuleChannelPushBox.IsChecked == true) set.Add("push");
        if (RuleChannelSnmpBox.IsChecked == true) set.Add("snmp");
        if (RuleChannelTeamPlusBox.IsChecked == true) set.Add("teamplus");
        return set.Count == 0 ? null : string.Join(",", set);
    }

    private void OnAddRuleClicked(object sender, RoutedEventArgs e)
    {
        var name = RuleNameBox.Text.Trim();
        if (name.Length == 0)
        {
            RuleReportText.Text = Localizer.T("Settings.RuleNameRequired");
            return;
        }

        var eventType = RuleEventTypeBox.Text.Trim();
        int? channelId = RuleChannelCombo.SelectedItem is ComboBoxItem { Tag: int cid } ? cid : null;
        var keyword = RuleKeywordBox.Text.Trim();
        var channels = BuildRuleChannels();
        var matchTypesValue = string.IsNullOrWhiteSpace(RuleMatchTypesBox.Text)
            ? null
            : RuleMatchTypesBox.Text.Trim();
        var frameMinutes = int.TryParse(RuleFrameMinutesBox.Text.Trim(), out var fm) ? Math.Max(0, fm) : 0;
        var minEvents = int.TryParse(RuleMinEventsBox.Text.Trim(), out var me) ? Math.Max(1, me) : 1;

        _rules.Add(
            name,
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            channelId,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            channels,
            matchTypesValue,
            frameMinutes,
            minEvents);

        RuleReportText.Text = string.Format(Localizer.T("Settings.RuleAdded"), name, frameMinutes > 0 ? string.Format(Localizer.T("Settings.RuleAggFrame"), frameMinutes, minEvents) : Localizer.T("Settings.RulePerItem"));
        RuleNameBox.Clear();
        RuleEventTypeBox.Clear();
        RuleKeywordBox.Clear();
        RuleMatchTypesBox.Clear();
        RuleFrameMinutesBox.Clear();
        RuleMinEventsBox.Clear();
        RuleChannelCombo.SelectedIndex = 0;
        RuleChannelWebhookBox.IsChecked = false;
        RuleChannelSmtpBox.IsChecked = false;
        RuleChannelMqttBox.IsChecked = false;
        RuleChannelPushBox.IsChecked = false;
        RuleChannelSnmpBox.IsChecked = false;
        RuleChannelTeamPlusBox.IsChecked = false;
        ReloadRules();
    }

    private void OnToggleRuleClicked(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row)
        {
            RuleReportText.Text = Localizer.T("Settings.SelectRuleFirst");
            return;
        }

        var rule = _rules.ListAll().First(r => r.Id == row.Id);
        _rules.SetEnabled(rule.Id, !rule.Enabled);
        RuleReportText.Text = string.Format(Localizer.T("Settings.RuleToggled"), rule.Name, rule.Enabled ? Localizer.T("Settings.Disabled") : Localizer.T("Settings.Enabled"));
        ReloadRules();
    }

    private void OnDeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row)
        {
            RuleReportText.Text = Localizer.T("Settings.SelectRuleFirst");
            return;
        }

        _rules.Delete(row.Id);
        RuleReportText.Text = Localizer.T("Settings.RuleDeleted");
        ReloadRules();
    }

    private void ReloadQuota()
    {
        var quotaGb = QuotaGbFromStore();
        QuotaTextBox.Text = quotaGb.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 顯示目前的錄影總頻寬上限（換算為 Mbps）。設定以 bps 儲存（與協調器一致），
    /// 這裡只在顯示與輸入時換算，避免兩處各存一份而分歧。
    /// </summary>
    private void ReloadBandwidthBudget()
    {
        var bps = RecordingQualityCoordinator.ReadBudget(_store);
        BandwidthBudgetTextBox.Text = (bps / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 顯示目前的錄影分段長度（秒）。設定值以秒儲存，與錄影端 <see cref="RecordingSegmentPolicy"/>
    /// 讀的是同一個鍵；這裡只做顯示，不在 UI 端另存一份而分歧。
    /// </summary>
    private void ReloadSegmentSeconds()
    {
        SegmentSecondsTextBox.Text = RecordingSegmentPolicy.Read(_store)
            .ToString(CultureInfo.InvariantCulture);
    }

    private double QuotaGbFromStore()
    {
        var fromDb = _settings.GetDoubleOrDefault(QuotaKey, -1);
        if (fromDb > 0)
        {
            return fromDb;
        }

        var fromEnv = Environment.GetEnvironmentVariable("HELIVMS_QUOTA_GB");
        if (!string.IsNullOrWhiteSpace(fromEnv) &&
            double.TryParse(fromEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            return v;
        }

        return DefaultQuotaGb;
    }

    private void ReloadSnapshotDays()
    {
        SnapshotDaysTextBox.Text = SnapDaysFromStore().ToString(CultureInfo.InvariantCulture);
    }

    private int SnapDaysFromStore()
    {
        var fromDb = _settings.GetDoubleOrDefault(SnapshotDaysKey, -1);
        if (fromDb > 0)
        {
            return (int)fromDb;
        }

        var raw = Environment.GetEnvironmentVariable("HELIVMS_SNAPSHOT_DAYS");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            return (int)v;
        }

        return DefaultSnapshotDays;
    }

    private void ReloadAlarmRetentionDays()
    {
        AlarmRetentionDaysTextBox.Text = MetadataRetentionDaysFromStore(AlarmRetentionDaysKey)
            .ToString(CultureInfo.InvariantCulture);
    }

    private void ReloadAuditRetentionDays()
    {
        AuditRetentionDaysTextBox.Text = MetadataRetentionDaysFromStore(AuditRetentionDaysKey)
            .ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 讀中繼資料保管天數。0 是合法值（永久保留），所以不能用「&lt;=0 當未設定」的哨兵；
    /// 只有「鍵不存在」才落回預設。GetDoubleOrDefault 以 double 存字串，故取整。
    /// </summary>
    private int MetadataRetentionDaysFromStore(string key)
    {
        var raw = _settings.Get(key);
        if (raw is not null &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) &&
            days >= 0 && days <= MaxMetadataRetentionDays)
        {
            return (int)days;
        }

        return DefaultMetadataRetentionDays;
    }

    private void ReloadNotify()
    {
        var cfg = NotificationSettings.Load(_settings);
        NotifyEnabledBox.IsChecked = cfg.Enabled;
        NotifyWebhookUrlBox.Text = cfg.WebhookUrl ?? string.Empty;
        NotifySmtpEnabledBox.IsChecked = cfg.SmtpEnabled;
        NotifySmtpHostBox.Text = cfg.SmtpHost ?? string.Empty;
        NotifySmtpPortBox.Text = cfg.SmtpPort.ToString(CultureInfo.InvariantCulture);
        NotifySmtpFromBox.Text = cfg.SmtpFrom ?? string.Empty;
        NotifySmtpToBox.Text = string.Join(", ", cfg.SmtpTo);
        NotifySmtpUserBox.Text = cfg.SmtpUser ?? string.Empty;
        NotifySmtpPasswordBox.Password = string.Empty;   // 不預填密碼
        NotifyQuietStartBox.Text = cfg.QuietStart ?? string.Empty;
        NotifyQuietEndBox.Text = cfg.QuietEnd ?? string.Empty;
        NotifyQuietRetransmitBox.IsChecked = cfg.QuietRetransmit;
        NotifyMqttEnabledBox.IsChecked = cfg.MqttEnabled;
        NotifyMqttHostBox.Text = cfg.MqttHost ?? string.Empty;
        NotifyMqttPortBox.Text = cfg.MqttPort.ToString(CultureInfo.InvariantCulture);
        NotifyMqttTopicBox.Text = cfg.MqttTopic ?? string.Empty;
        NotifyMqttUserBox.Text = cfg.MqttUser ?? string.Empty;
        NotifyMqttPasswordBox.Password = string.Empty;   // 不預填密碼
        NotifyPushEnabledBox.IsChecked = cfg.PushEnabled;
        NotifyPushEndpointBox.Text = cfg.PushEndpoint ?? string.Empty;
        NotifySnmpEnabledBox.IsChecked = cfg.SnmpEnabled;
        NotifySnmpHostBox.Text = cfg.SnmpHost ?? string.Empty;
        NotifySnmpPortBox.Text = cfg.SnmpPort.ToString(CultureInfo.InvariantCulture);
        NotifySnmpCommunityBox.Text = cfg.SnmpCommunity ?? string.Empty;
        NotifyTeamPlusEnabledBox.IsChecked = cfg.TeamPlusEnabled;
        NotifyTeamPlusApiUrlBox.Text = cfg.TeamPlusApiUrl ?? string.Empty;
        NotifyTeamPlusAccountBox.Text = cfg.TeamPlusAccount ?? string.Empty;
        NotifyTeamPlusApiKeyBox.Password = string.Empty;   // 不預填 API Key
        NotifyTeamPlusChatSnBox.Text = cfg.TeamPlusChatSn ?? string.Empty;
    }

    private void OnApplyNotifyClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NotifySmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port <= 0)
        {
            NotifyReportText.Text = Localizer.T("Settings.SmtpPortInvalid");
            return;
        }

        _settings.Set(NotificationSettings.EnabledKey, NotifyEnabledBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.WebhookUrlKey, NotifyWebhookUrlBox.Text.Trim());
        _settings.Set(NotificationSettings.SmtpEnabledKey, NotifySmtpEnabledBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.SmtpHostKey, NotifySmtpHostBox.Text.Trim());
        _settings.Set(NotificationSettings.SmtpPortKey, port.ToString(CultureInfo.InvariantCulture));
        _settings.Set(NotificationSettings.SmtpFromKey, NotifySmtpFromBox.Text.Trim());
        _settings.Set(NotificationSettings.SmtpToKey, NotifySmtpToBox.Text.Trim());
        _settings.Set(NotificationSettings.SmtpUserKey, NotifySmtpUserBox.Text.Trim());

        var password = NotifySmtpPasswordBox.Password;
        if (!string.IsNullOrEmpty(password))
        {
            _settings.Set(NotificationSettings.SmtpPasswordKey, SecretProtector.Protect(password));
        }

        _settings.Set(NotificationSettings.QuietStartKey, NotifyQuietStartBox.Text.Trim());
        _settings.Set(NotificationSettings.QuietEndKey, NotifyQuietEndBox.Text.Trim());
        _settings.Set(NotificationSettings.QuietRetransmitKey,
            NotifyQuietRetransmitBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.MqttEnabledKey,
            NotifyMqttEnabledBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.MqttHostKey, NotifyMqttHostBox.Text.Trim());
        var mqttPort = int.TryParse(NotifyMqttPortBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var mp) && mp > 0 ? mp : 1883;
        _settings.Set(NotificationSettings.MqttPortKey, mqttPort.ToString(CultureInfo.InvariantCulture));
        _settings.Set(NotificationSettings.MqttTopicKey, NotifyMqttTopicBox.Text.Trim());
        _settings.Set(NotificationSettings.MqttUserKey, NotifyMqttUserBox.Text.Trim());
        var mqttPassword = NotifyMqttPasswordBox.Password;
        if (!string.IsNullOrEmpty(mqttPassword))
        {
            _settings.Set(NotificationSettings.MqttPasswordKey, SecretProtector.Protect(mqttPassword));
        }

        _settings.Set(NotificationSettings.PushEnabledKey,
            NotifyPushEnabledBox.IsChecked == true ? "true" : "false");
        var pushEndpoint = NotifyPushEndpointBox.Text.Trim();
        _settings.Set(NotificationSettings.PushEndpointKey, pushEndpoint);
        if (!string.IsNullOrWhiteSpace(pushEndpoint) &&
            string.IsNullOrWhiteSpace(_settings.Get(NotificationSettings.PushPrivateKeyKey)))
        {
            var (publicKey, privateKey) = PushNotifier.GenerateKeyPair();
            _settings.Set(NotificationSettings.PushPublicKeyKey, publicKey);
            _settings.Set(NotificationSettings.PushPrivateKeyKey, SecretProtector.Protect(privateKey));
        }

        _settings.Set(NotificationSettings.SnmpEnabledKey,
            NotifySnmpEnabledBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.SnmpHostKey, NotifySnmpHostBox.Text.Trim());
        var snmpPort = int.TryParse(NotifySnmpPortBox.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var sp) && sp > 0 ? sp : 162;
        _settings.Set(NotificationSettings.SnmpPortKey, snmpPort.ToString(CultureInfo.InvariantCulture));
        _settings.Set(NotificationSettings.SnmpCommunityKey, NotifySnmpCommunityBox.Text.Trim());

        _settings.Set(NotificationSettings.TeamPlusEnabledKey,
            NotifyTeamPlusEnabledBox.IsChecked == true ? "true" : "false");
        _settings.Set(NotificationSettings.TeamPlusApiUrlKey, NotifyTeamPlusApiUrlBox.Text.Trim());
        _settings.Set(NotificationSettings.TeamPlusAccountKey, NotifyTeamPlusAccountBox.Text.Trim());
        var teamPlusApiKey = NotifyTeamPlusApiKeyBox.Password;
        if (!string.IsNullOrEmpty(teamPlusApiKey))
        {
            _settings.Set(NotificationSettings.TeamPlusApiKeyKey, SecretProtector.Protect(teamPlusApiKey));
        }

        _settings.Set(NotificationSettings.TeamPlusChatSnKey, NotifyTeamPlusChatSnBox.Text.Trim());

        NotifyReportText.Text = Localizer.T("Settings.NotifyApplied");
    }

    private void OnApplySnapshotDaysClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SnapshotDaysTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ||
            days <= 0)
        {
            CleanupReportText.Text = Localizer.T("Settings.DaysPositive");
            return;
        }

        _settings.Set(SnapshotDaysKey, days.ToString(CultureInfo.InvariantCulture));
        CleanupReportText.Text = string.Format(Localizer.T("Settings.SnapshotDaysApplied"), days);
    }

    private void OnApplyAlarmRetentionDaysClicked(object sender, RoutedEventArgs e)
        => ApplyMetadataRetentionDays(AlarmRetentionDaysTextBox, AlarmRetentionDaysKey,
            AlarmRetentionDaysStatusText, "Settings.AlarmRetentionDaysApplied");

    private void OnApplyAuditRetentionDaysClicked(object sender, RoutedEventArgs e)
        => ApplyMetadataRetentionDays(AuditRetentionDaysTextBox, AuditRetentionDaysKey,
            AuditRetentionDaysStatusText, "Settings.AuditRetentionDaysApplied");

    /// <summary>
    /// 套用中繼資料保管天數。0 合法（永久保留），故範圍是 0..36500，與 WebApi PUT /config 的驗證一致。
    /// </summary>
    private void ApplyMetadataRetentionDays(TextBox box, string key, TextBlock status, string appliedKey)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ||
            days < 0 || days > MaxMetadataRetentionDays)
        {
            status.Text = Localizer.T("Settings.RetentionDaysRange");
            return;
        }

        _settings.Set(key, days.ToString(CultureInfo.InvariantCulture));
        status.Text = string.Format(Localizer.T(appliedKey), days);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";

    /// <summary>
    /// 重新計算錄影用量。
    ///
    /// 這裡以前是 <c>Directory.EnumerateFiles(recordingsRoot, "*.mp4",
    /// SearchOption.TopDirectoryOnly)</c>，而錄影檔實際寫在
    /// <c>recordings/chXXX/yyyyMMdd/HH/seg-HHmmssfff.mp4</c>（兩層底下）。
    /// 根層一個 mp4 都掃不到，所以用量恆為 0，畫面長期顯示「0B（0 段）」——
    /// 使用者無從知道磁碟快滿了，而配額清理正在背後默默吃掉舊錄影。
    ///
    /// 現在改用區段索引的 SUM：跟配額清理用的是同一個數字，所以畫面寫 5GB
    /// 的時候清理也確實在 5GB 就會動。順帶解決原本在 UI 執行緒同步列舉整棵目錄
    /// 的問題——30 天 × 16 路 × 15 秒一段是幾十萬個檔案，那會讓視窗直接凍結。
    /// </summary>
    private void ReloadUsage()
    {
        // 先做一輪「只要資料夾層級」的索引對帳，再算 SUM：使用者把錄影從磁碟刪掉後，索引要到
        // 下一輪對帳才標 missing，在那之前這裡顯示的用量、段數都會把已刪除的段算進去，看起來像
        // 「明明刪了卻還佔著」。資料夾層級只問「天數 × 頻道數」個 Directory.Exists，成本與整段
        // 歷史無關，同步跑在使用者按下「儲存」頁時是可接受的量。逐檔 stat 仍留給背景對帳。
        try
        {
            new SegmentIndexReconciler(new SegmentRepository(_store)).ReconcileFolders();
        }
        catch (Exception)
        {
            // 對帳失敗（權限、磁碟忙碌）不該讓用量頁打不開；照索引現況顯示。
        }

        var (total, count) = new SegmentRepository(_store).GetTotalUsageSummary();
        UsageText.Text = string.Format(Localizer.T("Settings.UsageText"), FormatBytes(total), count, QuotaGbFromStore());
    }

    /// <summary>
    /// 設定中心「儲存」頁的錄影磁碟：下拉列出本機固定磁碟與其可用空間，清單列出目前已設定、
    /// 會輪流寫入的磁碟（第 0 筆為主要碟）。
    ///
    /// 多磁碟是為了「一顆滿了自動續錄下一顆」：只設定單一根目錄時，錄滿就是整台停止，
    /// 而且畫面上沒有任何錯誤訊息，操作員要等到想回放才發現好幾天沒錄。清單順序即優先序，
    /// 套用後寫入端每個區段都依序挑還有空間的碟。
    /// </summary>
    private void ReloadRecordingsDisk()
    {
        var drives = RecordingStorage.ListFixedDrives();
        RecordingsDiskCombo.ItemsSource = drives
            .Select(d => new ComboBoxItem
            {
                Content = string.Format(
                    Localizer.T("Settings.RecordingsDiskItem"),
                    d.Root,
                    FormatBytes(d.FreeBytes),
                    FormatBytes(d.TotalBytes)),
                Tag = d.Root,
            })
            .ToList();

        // 預選目前錄影根目錄所在的磁碟（跨磁碟切換後再開設定中心時要顯示實況）。
        var currentRoot = Path.GetPathRoot(_recordingsRoot);
        for (var i = 0; i < RecordingsDiskCombo.Items.Count; i++)
        {
            if (RecordingsDiskCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, currentRoot, StringComparison.OrdinalIgnoreCase))
            {
                RecordingsDiskCombo.SelectedIndex = i;
                break;
            }
        }

        if (RecordingsDiskCombo.SelectedIndex < 0 && RecordingsDiskCombo.Items.Count > 0)
        {
            RecordingsDiskCombo.SelectedIndex = 0;
        }

        // 讀回已設定的磁碟清單（主要碟排首位）；未設定時以目前錄影磁碟起頭，
        // 讓既有單碟使用者看到自己的現況，而不是空白清單。
        _diskConfig = RecordingStorage
            .ParseDisks(_settings.Get(RecordingStorage.DisksSettingKey))
            .Where(d => !string.IsNullOrWhiteSpace(d.Root))
            .OrderByDescending(d => d.Primary)
            .ToList();
        if (_diskConfig.Count == 0 && !string.IsNullOrEmpty(currentRoot))
        {
            _diskConfig.Add(new RecordingStorage.RecordingDiskDto { Root = currentRoot, Primary = true });
        }

        RefreshRecordingsDiskList();
        RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.RecordingsDiskCurrent"), _recordingsRoot);
    }

    /// <summary>把目前編輯中的磁碟清單畫到清單檢視，並依目前語言設定欄位標題與角色文字。</summary>
    private void RefreshRecordingsDiskList()
    {
        DiskColDrive.Header = Localizer.T("Settings.DiskColDrive");
        DiskColRole.Header = Localizer.T("Settings.DiskColRole");
        DiskColSpace.Header = Localizer.T("Settings.DiskColSpace");

        var byRoot = RecordingStorage.ListFixedDrives()
            .ToDictionary(d => RecordingStorage.DriveRootOf(d.Root), StringComparer.OrdinalIgnoreCase);

        var rows = new List<DiskRow>(_diskConfig.Count);
        for (var i = 0; i < _diskConfig.Count; i++)
        {
            var root = RecordingStorage.DriveRootOf(_diskConfig[i].Root);
            var role = i == 0
                ? Localizer.T("Settings.DiskPrimary")
                : Localizer.T("Settings.DiskSecondary");
            var space = byRoot.TryGetValue(root, out var drive)
                ? string.Format(Localizer.T("Settings.DiskSpace"), FormatBytes(drive.FreeBytes), FormatBytes(drive.TotalBytes))
                : Localizer.T("Settings.DiskUnavailable");
            rows.Add(new DiskRow(root, role, space));
        }

        RecordingsDiskList.ItemsSource = rows;
    }

    /// <summary>把下拉選到的磁碟加入清單（清單本來是空的時候成為主要碟）；已在清單中則不重複。</summary>
    private void OnAddRecordingsDiskClicked(object sender, RoutedEventArgs e)
    {
        if (RecordingsDiskCombo.SelectedItem is not ComboBoxItem { Tag: string driveRoot })
        {
            RecordingsDiskStatusText.Text = Localizer.T("Settings.RecordingsDiskPick");
            return;
        }

        var norm = RecordingStorage.DriveRootOf(driveRoot);
        if (_diskConfig.Any(d => string.Equals(RecordingStorage.DriveRootOf(d.Root), norm, StringComparison.OrdinalIgnoreCase)))
        {
            RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.DiskAlready"), driveRoot);
            return;
        }

        _diskConfig.Add(new RecordingStorage.RecordingDiskDto
        {
            Root = norm,
            Primary = _diskConfig.Count == 0,
        });
        RefreshRecordingsDiskList();
        RecordingsDiskList.SelectedIndex = _diskConfig.Count - 1;
        RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.DiskAdded"), driveRoot);
    }

    /// <summary>從清單移除選取的磁碟（不會立即生效，仍要按「套用」）。</summary>
    private void OnRemoveRecordingsDiskClicked(object sender, RoutedEventArgs e)
    {
        var index = RecordingsDiskList.SelectedIndex;
        if (index < 0 || index >= _diskConfig.Count)
        {
            RecordingsDiskStatusText.Text = Localizer.T("Settings.DiskPick");
            return;
        }

        var root = _diskConfig[index].Root;
        _diskConfig.RemoveAt(index);
        RefreshRecordingsDiskList();
        RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.DiskRemoved"), root);
    }

    /// <summary>把選取的磁碟移到清單首位（成為主要碟）：主要碟是溢流前的第一順位。</summary>
    private void OnSetPrimaryRecordingsDiskClicked(object sender, RoutedEventArgs e)
    {
        var index = RecordingsDiskList.SelectedIndex;
        if (index < 0 || index >= _diskConfig.Count)
        {
            RecordingsDiskStatusText.Text = Localizer.T("Settings.DiskPick");
            return;
        }

        if (index == 0)
        {
            RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.DiskAlreadyPrimary"), _diskConfig[0].Root);
            return;
        }

        var chosen = _diskConfig[index];
        _diskConfig.RemoveAt(index);
        _diskConfig.Insert(0, chosen);
        RefreshRecordingsDiskList();
        RecordingsDiskList.SelectedIndex = 0;
        RecordingsDiskStatusText.Text = string.Format(Localizer.T("Settings.DiskPrimarySet"), chosen.Root);
    }

    /// <summary>
    /// 套用錄影磁碟清單：<c>recording.disks</c> 是真正來源，<c>recording.root</c> 記主要碟，
    /// <c>recording.known_roots</c> 聯集所有目錄，再通知主視窗立即套用。
    ///
    /// 寫入端每個區段都即時重讀 <c>recording.disks</c>，因此主要碟寫滿會自動溢流到下一顆；
    /// known_roots 讓「清理舊錄影」與遠端回放看得到每一顆碟，而不是只認最後一次切換的那一顆。
    /// </summary>
    private void OnApplyRecordingsDiskClicked(object sender, RoutedEventArgs e)
    {
        if (_diskConfig.Count == 0)
        {
            RecordingsDiskStatusText.Text = Localizer.T("Settings.DiskNone");
            return;
        }

        var ordered = _diskConfig
            .Select((d, i) => new RecordingStorage.RecordingDiskDto { Root = RecordingStorage.DriveRootOf(d.Root), Primary = i == 0 })
            .ToList();
        var folders = RecordingStorage.DiskRoots(ordered);
        var primaryFolder = folders[0];

        var known = RecordingStorage.MergeKnownRoot(
            _settings.Get(RecordingStorage.KnownRootsSettingKey), _recordingsRoot);
        foreach (var folder in folders)
        {
            known = RecordingStorage.MergeKnownRoot(known, folder);
        }

        _settings.Set(RecordingStorage.KnownRootsSettingKey, known);
        _settings.Set(RecordingStorage.DisksSettingKey, RecordingStorage.SerializeDisks(ordered));
        _settings.Set(RecordingStorage.RootSettingKey, primaryFolder);

        _recordingsRoot = primaryFolder;
        RecordingsRootText.Text = _recordingsRoot;

        _recordingsRootChanged?.Invoke(primaryFolder);
        RecordingsDiskStatusText.Text = string.Format(
            Localizer.T("Settings.DisksApplied"), folders.Count, primaryFolder);
    }

    private void OnApplyQuotaClicked(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(QuotaTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb <= 0)
        {
            CleanupReportText.Text = Localizer.T("Settings.GbPositive");
            return;
        }

        _settings.Set(QuotaKey, gb.ToString("0.#", CultureInfo.InvariantCulture));
        CleanupReportText.Text = string.Format(Localizer.T("Settings.QuotaApplied"), gb);
        ReloadUsage();
    }

    /// <summary>
    /// 套用錄影總頻寬上限（使用者輸入 Mbps；0＝不限制）。以 bps 寫入共用設定，
    /// 排程器每 30 秒讀一次，據此在超載時把最吃頻寬的頻道降到子碼流錄影。
    /// </summary>
    private void OnApplyBandwidthBudgetClicked(object sender, RoutedEventArgs e)
    {
        // 允許 0 代表「不限制」（恢復一律主碼流）；其餘必須是正數。負數或文字一律退件，
        // 因為「0.0 誤打成 -0.1」若被當成不限制，操作員會以為上限生效了而事實沒有。
        if (!double.TryParse(BandwidthBudgetTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps)
            || mbps < 0)
        {
            BandwidthBudgetStatusText.Text = Localizer.T("Settings.BandwidthBudgetInvalid");
            return;
        }

        var bps = (long)Math.Round(mbps * 1_000_000d);
        _settings.Set(RecordingQualityCoordinator.BudgetSettingKey, bps.ToString(CultureInfo.InvariantCulture));
        ReloadBandwidthBudget();

        BandwidthBudgetStatusText.Text = mbps <= 0
            ? Localizer.T("Settings.BandwidthBudgetUnlimited")
            : string.Format(Localizer.T("Settings.BandwidthBudgetApplied"), mbps);
    }

    /// <summary>
    /// 套用錄影分段長度（秒）。錄影端每段一個檔，分段越長檔數越少、磁碟與索引越省，
    /// 但 HLS 搜尋粒度與程序崩潰時的損失也越大；超出允許範圍一律退件並提示，
    /// 不悄悄夾值——操作員會以為設的值生效了，事實卻不是。
    /// </summary>
    private void OnApplySegmentSecondsClicked(object sender, RoutedEventArgs e)
    {
        if (!RecordingSegmentPolicy.TryParse(SegmentSecondsTextBox.Text, out var seconds))
        {
            SegmentSecondsStatusText.Text = string.Format(
                Localizer.T("Settings.SegmentSecondsInvalid"),
                RecordingSegmentPolicy.MinSeconds,
                RecordingSegmentPolicy.MaxSeconds);
            return;
        }

        _settings.Set(RecordingSegmentPolicy.SettingKey, seconds.ToString(CultureInfo.InvariantCulture));
        ReloadSegmentSeconds();
        SegmentSecondsStatusText.Text = string.Format(Localizer.T("Settings.SegmentSecondsApplied"), seconds);
    }

    private void OnCleanupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var repo = new SegmentRepository(_store);
            var service = new RetentionService(
                repo,
                _recordingsRoot,
                additionalRoots: RecordingStorage.ParseKnownRoots(_settings.Get(RecordingStorage.KnownRootsSettingKey)));
            var report = service.Apply(QuotaBytesFromGb(QuotaGbFromStore()), DateTime.UtcNow);
            var text = string.Format(Localizer.T("Settings.CleanupDone"), report.DeletedSegments, FormatBytes(report.FreedBytes)) +
                       (report.PurgedTmp > 0 ? string.Format(Localizer.T("Settings.CleanupTmp"), report.PurgedTmp) : string.Empty);

            var snapDays = SnapDaysFromStore();
            var snapReport = service.PurgeSnapshots(_snapshotsRoot, DateTime.UtcNow.AddDays(-snapDays));
            if (snapReport.DeletedFiles > 0)
            {
                text += string.Format(Localizer.T("Settings.CleanupSnapshots"), snapReport.DeletedFiles, FormatBytes(snapReport.FreedBytes));
            }
            else
            {
                text += Localizer.T("Settings.CleanupNoSnapshots");
            }

            CleanupReportText.Text = text;
        }
        catch (Exception ex)
        {
            CleanupReportText.Text = string.Format(Localizer.T("Settings.CleanupFailed"), ex.Message);
        }

        ReloadUsage();
    }

    /// <summary>
    /// 掃描各錄影根目錄，把「索引裡沒有、磁碟上還在」的段補回索引。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 根目錄除了目前位置與已知舊根目錄，還要加上 <c>&lt;dataRoot&gt;\recordings</c>：實機上
    /// 舊版的錄影就落在 <c>C:\HeliVMSData\recordings</c>（例如 <c>ch072</c>），只掃目前根目錄
    /// 的話那些檔案永遠不會被看到，而症狀正是「磁碟有錄影、回放空白」。
    /// </para>
    /// <para>
    /// 掃描是純檔案列舉，整顆碟上可能跑幾十秒，所以放背景執行緒；匯入在對話框裡跑，
    /// 讓操作員先確認「哪個資料夾掛到哪個頻道」再寫入索引。
    /// </para>
    /// </remarks>
    private async void OnImportRecordingsClicked(object sender, RoutedEventArgs e)
    {
        ImportRecordingsButton.IsEnabled = false;
        ImportRecordingsStatusText.Text = Localizer.T("Settings.ImportRecordingsScanning");

        try
        {
            var channels = new ChannelRepository(_store).List();
            var roots = new List<string> { _recordingsRoot };
            roots.AddRange(RecordingStorage.ParseKnownRoots(_settings.Get(RecordingStorage.KnownRootsSettingKey)));
            roots.Add(Path.Combine(_dataRoot, "recordings"));

            var distinct = roots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var folders = await Task.Run(() =>
                new RecordingDiskImporter(new SegmentRepository(_store)).Scan(distinct, channels));

            if (folders.Count == 0)
            {
                ImportRecordingsStatusText.Text = Localizer.T("Settings.ImportRecordingsEmpty");
                return;
            }

            var window = new RecordingImportWindow(_store, folders) { Owner = this };
            window.ShowDialog();

            if (window.Report is { } report)
            {
                ImportRecordingsStatusText.Text = report.AnyImported
                    ? string.Format(
                        Localizer.T("Settings.ImportRecordingsDone"),
                        report.Imported + report.Revived,
                        FormatBytes(report.Bytes))
                    : Localizer.T("Settings.ImportRecordingsNothingTaken");

                if (report.AnyImported)
                {
                    ReloadUsage();
                }
            }
            else
            {
                ImportRecordingsStatusText.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            ImportRecordingsStatusText.Text =
                string.Format(Localizer.T("Settings.ImportRecordingsFailed"), ex.Message);
        }
        finally
        {
            ImportRecordingsButton.IsEnabled = true;
        }
    }

    private static long QuotaBytesFromGb(double gb) => (long)(gb * 1024 * 1024 * 1024);

    private void ReloadLicense()
    {
        // 顯示與判斷共用同一份 DB 授權列（LicenseService），不再另外讀授權檔。
        // 兩邊各讀一份會出現「設定頁寫著已授權、錄影閘門卻認為未匯入」的矛盾。
        var gate = new LicenseUiGate(_store);
        var license = gate.Current;
        if (license.Decision == LicenseDecision.Valid && license.Record is not null)
        {
            var record = license.Record;
            var expire = record.ExpiresUtc.HasValue
                ? string.Format(Localizer.T("Settings.LicenseExpire"), record.ExpiresUtc.Value)
                : Localizer.T("Settings.LicensePerpetual");
            var tierText = string.IsNullOrEmpty(record.Tier) ? string.Empty : $"{record.Tier}／";
            LicenseStatusText.Text = string.Format(Localizer.T("Settings.LicenseStatusValid"), tierText, record.MaxCameras, expire);

            // 剩餘通道＝授權上限 − 已設定頻道數（§19.4「等級／到期／剩餘通道」）。
            // 與錄影閘門同源的 ChannelRepository，避免另一套「在錄數」算法造成兩個數字。
            var used = new ChannelRepository(_store).List().Count;
            var seats = Math.Max(0, record.MaxCameras - used);
            LicenseSeatsText.Text = string.Format(Localizer.T("Settings.LicenseSeats"), seats, used, record.MaxCameras);
        }
        else
        {
            LicenseStatusText.Text = string.Format(Localizer.T("Settings.LicenseStatusInvalid"), license.Message ?? license.Decision.ToString());
            LicenseSeatsText.Text = Localizer.T("Settings.LicenseSeatsNone");
        }

        MachineText.Text = MachineIdProvider.GetDeviceCode();
        LicenseFeatureList.ItemsSource = BuildFeatureChips(license.Features);
        ApplyLicenseVisibility(gate);
    }

    /// <summary>已授權功能清單的一列。</summary>
    private sealed record FeatureChip(string Label);

    /// <summary>
    /// 列出<b>全部</b>已知旗標，沒有的那幾項標成未授權。
    /// </summary>
    /// <remarks>
    /// 為什麼連未授權的也要列出：未授權功能是直接從介面上隱藏的（§19.4），
    /// 所以操作員看到的是「排程按鈕不見了」，畫面上沒有任何線索說明原因，
    /// 也不曉得要怎麼才能開。如果只顯示已授權的清單，他看到的就是一個空集合，
    /// 頂多以為自己買了什麼都沒有的版本。把缺什麼寫出來，他才知道該問廠商加哪一項。
    /// </remarks>
    /// <remarks>
    /// 授權檔裡出現我們不認識的旗標時也要顯示：<see cref="LicenseFeatures.IsKnown"/>
    /// 存在的理由就是上游會新增旗標，把不認識的當成「未授權」會讓操作員
    /// 對一個已經付款的功能產生錯誤印象。
    /// </remarks>
    private static IReadOnlyList<FeatureChip> BuildFeatureChips(IReadOnlyList<string> grantedFeatures)
    {
        var granted = new HashSet<string>(grantedFeatures, StringComparer.Ordinal);

        var chips = LicenseFeatures.All
            .Select(feature => new
            {
                Label = LicenseFeatures.DisplayName(feature),
                Granted = granted.Contains(feature),
            })
            .ToList();

        foreach (var feature in granted.Where(f => !LicenseFeatures.IsKnown(f)).OrderBy(f => f, StringComparer.Ordinal))
        {
            chips.Add(new { Label = feature, Granted = true });
        }

        var missing = Localizer.T("License.FeatureMissing");
        return chips
            .Select(c => new FeatureChip(c.Granted ? c.Label : $"{c.Label} {missing}"))
            .ToList();
    }

    /// <summary>把設備碼複製到剪貼簿，省去使用者手抄 32 碼 hex 的錯誤（§19.4 導入流程）。</summary>
    private void OnCopyMachineClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MachineText.Text);
            LicenseApplyText.Text = Localizer.T("Settings.LicenseCopied");
        }
        catch (Exception ex)
        {
            LicenseApplyText.Text = string.Format(Localizer.T("Settings.LicenseCopyFailed"), ex.Message);
        }
    }

    /// <summary>依授權隱藏設定中心裡的受限功能（M209），並補上到期提醒（M211）。</summary>
    private void ApplyLicenseVisibility(LicenseUiGate gate)
    {
        gate.Apply(ShareEnabledBox, LicenseFeatures.Remote);
        gate.Apply(SharePortBox, LicenseFeatures.Remote);
        gate.Apply(ShareBaseUrlBox, LicenseFeatures.Remote);
        gate.Apply(ShareApplyButton, LicenseFeatures.Remote);
        gate.Apply(ShareServiceStatusText, LicenseFeatures.Remote);
        gate.Apply(LaunchScheduleButton, LicenseFeatures.Schedule, Localizer.T("Settings.LaunchScheduleTip"));
        gate.Apply(LaunchDetectionButton, LicenseFeatures.AiL1, Localizer.T("Settings.LaunchDetectionTip"));
        gate.Apply(PatrolButton, LicenseFeatures.Schedule, Localizer.T("Main.PatrolTip"));
        gate.Apply(AudioButton, LicenseFeatures.Ai, Localizer.T("Main.AudioTip"));
        gate.Apply(IoButton, LicenseFeatures.Gis, Localizer.T("Main.IoTip"));
        gate.Apply(TamperEnabledBox, LicenseFeatures.Ai);
        gate.Apply(EnterpriseSection, LicenseFeatures.AdSso);

        // 續期動作就在這個頁面，提醒也得在這裡再說一次（§19.4 到期前 14 天提醒）。
        var notice = gate.Current.Expiry(DateTime.UtcNow);
        LicenseExpiryText.Text = notice.ShouldWarn ? notice.Message ?? string.Empty : string.Empty;
        LicenseExpiryText.Visibility = notice.ShouldWarn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnApplyLicenseClicked(object sender, RoutedEventArgs e)
    {
        var token = LicenseKeyBox.Text?.Trim() ?? string.Empty;
        var actor = SessionContext.CurrentUser?.Username ?? "system";

        // 單一來源（M222）：驗簽／機器綁定／落表／稽核一律走 LicenseService.Apply（§19.7／§19.8）。
        // 不再先用 raw LicenseManager 驗一次——兩套判斷會出現「設定頁說無效、閘門卻說有效」。
        var applied = new LicenseService(_store).Apply(token, actor, DateTime.UtcNow);

        // 只有可信任的結果才落授權檔；若先寫檔再驗，作廢／回流／簽章錯的金鑰會被
        // 下一次啟動的 RefreshDefault 從檔裡讀回來，等於繞過了拒絕。
        if (applied.Decision is not (LicenseDecision.Valid or LicenseDecision.Expired))
        {
            LicenseApplyText.Text = string.Format(Localizer.T("Settings.LicenseNotApplied"), applied.Message ?? applied.Decision.ToString());
            ReloadLicense();
            return;
        }

        try
        {
            var file = LicenseManager.DefaultPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(file, token);
            var expiryNote = applied.Message ?? Localizer.T("Settings.LicenseRenewSoon");
            LicenseApplyText.Text = applied.Decision == LicenseDecision.Valid
                ? string.Format(Localizer.T("Settings.LicenseApplied"), applied.MaxCameras)
                : string.Format(Localizer.T("Settings.LicenseAppliedExpired"), expiryNote);
        }
        catch (Exception ex)
        {
            LicenseApplyText.Text = string.Format(Localizer.T("Settings.LicenseAppliedFileFailed"), ex.Message);
        }

        ReloadLicense();
    }

    private void OnLaunchPlaybackClicked(object sender, RoutedEventArgs e)
        => new PlaybackWindow(_store, playbackFinalizer: _playbackFinalizer) { Owner = this }.Show();

    private void OnLaunchEventsClicked(object sender, RoutedEventArgs e)
        => new EventCenterWindow(_store, playbackFinalizer: _playbackFinalizer) { Owner = this }.Show();

    private void OnLaunchScheduleClicked(object sender, RoutedEventArgs e)
    {
        if (!new LicenseUiGate(_store).Allows(LicenseFeatures.Schedule))
        {
            LicenseApplyText.Text = new LicenseUiGate(_store).DenialMessage(LicenseFeatures.Schedule);
            return;
        }

        new SchedulingWindow(_store) { Owner = this }.Show();
    }

    private void OnLaunchDetectionClicked(object sender, RoutedEventArgs e)
    {
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.AiL1))
        {
            LicenseApplyText.Text = gate.DenialMessage(LicenseFeatures.AiL1);
            return;
        }

        new DetectionWindow(_store) { Owner = this }.Show();
    }

    // ── 工具（原監控中心「更多工具」，整合進設定中心）─────────────────────

    /// <summary>填入工具頁的 PTZ 頻道下拉（來源＝channels 清單）。</summary>
    private void ReloadTools()
    {
        var previous = PtzChannelCombo.SelectedItem is ChannelInfo c ? c.Id : (int?)null;
        var channels = new ChannelRepository(_store).List();
        PtzChannelCombo.ItemsSource = channels;
        if (previous is int id)
        {
            PtzChannelCombo.SelectedItem = channels.FirstOrDefault(c => c.Id == id);
        }

        if (PtzChannelCombo.SelectedItem is null && channels.Count > 0)
        {
            PtzChannelCombo.SelectedIndex = 0;
        }
    }

    private void OpenTool(Window window)
    {
        window.Owner = this;
        window.Show();
    }

    /// <summary>管理動作的身分檢查：viewer 一律擋下並說明原因。</summary>
    private bool RequireAdmin()
    {
        if (SessionContext.IsAdmin)
        {
            return true;
        }

        ToolsStatusText.Text = Localizer.T("Settings.AdminOnly");
        return false;
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new ExportWindow(_store, _dataRoot));
    }

    private void OnExportCenterClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new ExportCenterWindow(_store, _dataRoot));
    }

    private void OnNotificationClicked(object sender, RoutedEventArgs e)
    {
        OpenTool(new NotificationLogWindow(_store, ch =>
        {
            var center = new EventCenterWindow(_store, alerts: _manager?.Alerts, playbackFinalizer: _playbackFinalizer) { Owner = this };
            center.Show();
            center.FocusUnacknowledged(ch);
        }));
    }

    private void OnRedactionClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new RedactionWindow(_store, _dataRoot));
    }

    private void OnDewarpClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new DewarpWindow(_store, _dataRoot));
    }

    private void OnPtzClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        if (PtzChannelCombo.SelectedItem is not ChannelInfo channel)
        {
            ToolsStatusText.Text = Localizer.T("Settings.PtzPickChannel");
            return;
        }

        if (channel.DeviceId is not int deviceId)
        {
            ToolsStatusText.Text = Localizer.T("Settings.PtzNoDevice");
            return;
        }

        var device = new DeviceRepository(_store, new AuditLogRepository(_store)).Get(deviceId);
        if (device is null)
        {
            ToolsStatusText.Text = Localizer.T("Settings.PtzNoDevice");
            return;
        }

        OpenTool(new PtzWindow(_store, channel, device));
    }

    private void OnStreamClicked(object sender, RoutedEventArgs e)
        => OpenTool(new StreamSwitchWindow(_store, _manager));

    private void OnAudioClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Ai))
        {
            ToolsStatusText.Text = gate.DenialMessage(LicenseFeatures.Ai);
            return;
        }

        OpenTool(new AudioWindow(_store));
    }

    private void OnPatrolClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Schedule))
        {
            ToolsStatusText.Text = gate.DenialMessage(LicenseFeatures.Schedule);
            return;
        }

        OpenTool(new PatrolWindow(_store, _patrolHost));
    }

    private void OnReportsClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new ReportsWindow(_store, _dataRoot));
    }

    private void OnSynopsisClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new SynopsisWindow(_store, _dataRoot));
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e) => OpenSearchWindow();

    private void OpenSearchWindow() => OpenTool(new SearchWindow(_store));

    private void OnSystemHealthClicked(object sender, RoutedEventArgs e) => OpenSystemHealthWindow();

    private void OpenSystemHealthWindow() => OpenTool(new SystemHealthWindow());

    private void OnAlarmManagerClicked(object sender, RoutedEventArgs e)
        => OpenTool(new AlarmManagerWindow(_store, _manager?.Alerts));

    private void OnRulesClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new RulesWindow(_store));
    }

    private void OnIoClicked(object sender, RoutedEventArgs e)
    {
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Gis))
        {
            ToolsStatusText.Text = gate.DenialMessage(LicenseFeatures.Gis);
            return;
        }

        OpenTool(new IoWindow(_store));
    }

    private void OnAuditClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new AuditLogWindow(_store, _dataRoot));
    }

    private void OnLegalHoldClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new LegalHoldWindow(_store, allowRevoke: true));
    }

    private void OnSmartwallClicked(object sender, RoutedEventArgs e) => OpenSmartwallWindow();

    private void OpenSmartwallWindow() => OpenTool(new SmartwallWindow(_store));

    private void OnPosClicked(object sender, RoutedEventArgs e) => OpenPosWindow();

    private void OpenPosWindow() => OpenTool(new PosWindow(_store));

    private void OnEdgeBackfillClicked(object sender, RoutedEventArgs e) => OpenEdgeBackfillWindow();

    private void OpenEdgeBackfillWindow() => OpenTool(new EdgeBackfillWindow(_store, _dataRoot));

    private void OnFailoverClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new FailoverWindow(_store));
    }

    private void OnTalkClicked(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin())
        {
            return;
        }

        OpenTool(new TalkWindow(_store));
    }

    // ── 電子地圖（M41，§16.1）──────────────────────────────────────────

    private MapRecord? SelectedMap =>
        MapList.SelectedItem is MapRow r ? _maps.GetMap(r.Id) : null;

    private void ReloadMaps()
    {
        var previous = MapList.SelectedItem is MapRow r ? r.Id : (int?)null;
        MapList.ItemsSource = _maps.ListMaps()
            .Select(m => new MapRow(
                m.Id,
                m.Name,
                $"{m.Width}×{m.Height}",
                m.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
                m.SortOrder,
                MapGeometry.ScaleLabel(m.ScaleMPerPx)))
            .ToList();
        if (previous is int pid && MapList.ItemsSource is IEnumerable<MapRow> rows)
        {
            MapList.SelectedItem = rows.FirstOrDefault(x => x.Id == pid);
        }

        ReloadMapPins();
    }

    private void ReloadMapPins()
    {
        ReloadMapPinChannelCombo();

        if (MapPinCanvas != null)
        {
            for (int i = MapPinCanvas.Children.Count - 1; i >= 0; i--)
            {
                var c = MapPinCanvas.Children[i];
                if (c is Ellipse el && el.Tag is string s && s.StartsWith("md:", StringComparison.Ordinal))
                {
                    try { MapPinCanvas.Children.RemoveAt(i); } catch { }
                }
            }
        }

        if (MapPinList != null) MapPinList.ItemsSource = null;
        var map = SelectedMap;
        if (map is null)
        {
            _mapPinBitmap = null;
            _mapPinImageW = 0;
            _mapPinImageH = 0;
            if (MapPinImage != null) MapPinImage.Source = null;
            if (MapPinCanvas != null) { MapPinCanvas.Width = 0; MapPinCanvas.Height = 0; }
            if (MapPinImage != null) MapPinImage.Stretch = Stretch.None;
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = LoadBitmap(map.ImagePath);
        }
        catch (Exception ex)
        {
            MapReportText.Text = string.Format(Localizer.T("Settings.MapLoadFailed"), ex.Message);
            return;
        }

        if (MapScaleBox != null) MapScaleBox.Text = map.ScaleMPerPx.ToString("0.###", CultureInfo.InvariantCulture);
        _mapPinImageW = bitmap.PixelWidth;
        _mapPinImageH = bitmap.PixelHeight;
        _mapPinBitmap = bitmap;

        // 先把圖與畫布調整成「整張可見」的顯示尺寸，圖釘才能用顯示尺寸換算比例座標。
        ApplyMapPinFit();

        var cameras = new ChannelRepository(_store).List().ToDictionary(c => c.Id, c => c.Name);
        var ios = _io.ListChannels().ToDictionary(c => c.Id, c => c.Name);

        if (MapPinList != null)
        {
            MapPinList.ItemsSource = _maps.ListDevices(map.Id)
                .Select(p => new MapPinRow(
                    p.Id,
                    p.DeviceType,
                    p.ChannelId,
                    $"({p.X:0.00}, {p.Y:0.00})",
                    $"{p.Angle:0.#}°",
                    $"{p.FovDeg:0.#}°",
                    $"{p.FovDepth:0.#}m",
                    MapGeometry.Bearing(p.Angle),
                    p.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
                    (p.DeviceType?.ToLowerInvariant() == "camera")
                        ? (cameras.TryGetValue(p.ChannelId, out var cn) ? cn : string.Format(Localizer.T("Settings.MapChannelFallback"), p.ChannelId))
                        : (ios.TryGetValue(p.ChannelId, out var ioName) ? ioName : $"IO #{p.ChannelId}"))
                ).ToList();
        }

        CreateMapPinDots(map.Id);
        LayoutMapPins();
    }

    /// <summary>編輯畫面上這張平面圖的原始像素尺寸；0 表示還沒載圖。</summary>
    private double _mapPinImageW;
    private double _mapPinImageH;
    private BitmapImage? _mapPinBitmap;

    /// <summary>
    /// 讓整張平面圖剛好塞進編輯框（等比、不放大），並重排圖釘。
    /// </summary>
    /// <remarks>
    /// 這裡原本固定 1:1 放原始像素：一張 4K 的平面圖塞進 210px 高的框，操作員只看到左上角，
    /// 想把圖釘放到右邊的走廊就得先拉捲軸，而且拉過去之後還看不到整層的相對位置。
    /// 外層的捲軸留著：圖仍可能因為極端長寬比而以原始尺寸顯示（視窗還沒量好時），
    /// 這時捲軸是唯一的退路。
    /// </remarks>
    private void ApplyMapPinFit()
    {
        if (MapPinCanvas is null || MapPinImage is null || _mapPinBitmap is null)
        {
            return;
        }

        if (_mapPinImageW <= 0 || _mapPinImageH <= 0)
        {
            return;
        }

        MapPinImage.Source = _mapPinBitmap;

        var vw = MapPinScroll?.ViewportWidth ?? 0;
        var vh = MapPinScroll?.ViewportHeight ?? 0;

        // 分頁還沒被選取時 Viewport 是 0，但外框已經有實際尺寸。用外框當退路，
        // 才不會「第一次進設定頁是 1:1、切走再切回來才縮好」。
        if (vw < 32 && MapPinScroll is { ActualWidth: > 32 })
        {
            vw = MapPinScroll.ActualWidth - 4;
        }

        if (vh < 32 && MapPinScroll is { ActualHeight: > 32 })
        {
            vh = MapPinScroll.ActualHeight - 4;
        }

        var scale = 1.0;
        if (vw >= 32 && vh >= 32)
        {
            // 留 2px 給次像素捨入：算得剛剛好時捲軸會冒出來，冒出來又吃掉可用寬度而在兩個比例間跳動。
            scale = Math.Clamp(Math.Min((vw - 2) / _mapPinImageW, (vh - 2) / _mapPinImageH), 0.02, 1.0);
        }

        var w = _mapPinImageW * scale;
        var h = _mapPinImageH * scale;
        MapPinCanvas.Width = w;
        MapPinCanvas.Height = h;

        // 用 Fill 而不是 None：寬高已經按比例算好，Fill 不會變形；None 會維持原始像素而被框切掉。
        MapPinImage.Stretch = Stretch.Fill;
        MapPinImage.Width = w;
        MapPinImage.Height = h;
        MapPinImage.HorizontalAlignment = HorizontalAlignment.Left;
        MapPinImage.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(MapPinImage, 0);
        Canvas.SetTop(MapPinImage, 0);
        Canvas.SetZIndex(MapPinImage, 0);

        LayoutMapPins();
    }

    /// <summary>依目前畫布（顯示）尺寸重排圖釘；座標存的是 0..1 的比例。</summary>
    private void LayoutMapPins()
    {
        if (MapPinCanvas is null)
        {
            return;
        }

        var w = MapPinCanvas.Width;
        var h = MapPinCanvas.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (SelectedMap is not { } map)
        {
            return;
        }

        var positions = _maps.ListDevices(map.Id).ToDictionary(p => p.Id);
        foreach (var child in MapPinCanvas.Children.OfType<Ellipse>())
        {
            if (child.Tag is not string tag || !tag.StartsWith("md:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(tag.AsSpan("md:".Length), out var id)
                || !positions.TryGetValue(id, out var device))
            {
                continue;
            }

            // 圖釘的 (Left, Top) 是外框左上角，所以要把半徑扣掉，圓心才會落在比例座標上。
            Canvas.SetLeft(child, Math.Clamp(device.X, 0, 1) * w - child.Width / 2);
            Canvas.SetTop(child, Math.Clamp(device.Y, 0, 1) * h - child.Height / 2);
        }
    }

    private void CreateMapPinDots(int mapId)
    {
        if (MapPinCanvas is null)
        {
            return;
        }

        foreach (var p in _maps.ListDevices(mapId))
        {
            var dt = p.DeviceType?.ToLowerInvariant() ?? "camera";
            var dot = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = dt == "camera"
                    ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                    : (dt == "di" ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(245, 158, 11))),
                Stroke = Brushes.White,
                StrokeThickness = 3,
                Tag = $"md:{p.Id}",
                ToolTip = $"{p.DeviceType} #{p.ChannelId} ({p.X:0.00}, {p.Y:0.00})",
                Visibility = Visibility.Visible,
                IsHitTestVisible = true,
                Opacity = p.Enabled ? 1.0 : 0.7
            };
            Canvas.SetZIndex(dot, 50000);
            dot.MouseLeftButtonDown += OnMapPinDragStart;
            dot.MouseMove += OnMapPinDragMove;
            dot.MouseLeftButtonUp += OnMapPinDragEnd;
            MapPinCanvas.Children.Add(dot);
        }
    }

    /// <summary>目前圖層（以 Tag 為權威，不看在地化後的 Content）。</summary>
    private string PinKind()
        => (MapPinKindCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "camera";

    /// <summary>
    /// 重建「設備」下拉清單：只列目前圖層的設備，已放置者保留在清單中但停用並標示「已新增」。
    /// </summary>
    /// <remarks>
    /// 這裡原本每被呼叫一次就把 <c>SelectedIndex</c> 設回 0。呼叫端包含「點擊地圖」與
    /// 「按下放置鈕」，於是操作員選了攝影機 02 之後，一按放置就被重設成清單第一台，
    /// 症狀看起來像「只有攝影機 01 能新增」。因此改為保留先前選取的 Tag；已放置的設備
    /// 也不再直接從清單消失（那會讓操作員以為清單壞了），而是停用＋標記，按了沒反應才說得通。
    /// </remarks>
    private void ReloadMapPinChannelCombo(bool keepSelection = true)
    {
        if (MapPinChannelCombo is null)
        {
            return;
        }

        var previous = keepSelection && (MapPinChannelCombo.SelectedItem as ComboBoxItem)?.Tag is int prevId
            ? prevId
            : (int?)null;

        var kind = PinKind();
        var placed = new HashSet<(string Type, int Id)>();
        if (SelectedMap is { } map)
        {
            foreach (var p in _maps.ListDevices(map.Id))
            {
                placed.Add((p.DeviceType.ToLowerInvariant(), p.ChannelId));
            }
        }

        var suffix = Localizer.T("Settings.MapAlreadyPlaced");

        MapPinChannelCombo.Items.Clear();
        foreach (var (type, id, name) in PlaceableDevices(kind))
        {
            var isPlaced = placed.Contains((type, id));
            MapPinChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = isPlaced ? $"{name}（#{id}）{suffix}" : $"{name}（#{id}）",
                Tag = id,
                IsEnabled = !isPlaced,
            });
        }

        // 先找回原本選取的那一台，其次退而求其次選第一個還能放的；全都放過就讓清單停在
        // 第一個（停用）項目並關掉放置鈕，避免操作員按了只得到「重複新增」的例外訊息。
        var items = MapPinChannelCombo.Items.OfType<ComboBoxItem>().ToList();
        MapPinChannelCombo.SelectedItem =
            items.FirstOrDefault(i => i.IsEnabled && i.Tag is int id && id == previous)
            ?? items.FirstOrDefault(i => i.IsEnabled)
            ?? items.FirstOrDefault();

        if (MapPlacePinButton != null)
        {
            MapPlacePinButton.IsEnabled = MapPinChannelCombo.SelectedItem is ComboBoxItem { IsEnabled: true };
        }
    }

    /// <summary>列出目前圖層可放置的設備（型別、頻道 id、顯示名稱）。</summary>
    private IEnumerable<(string Type, int Id, string Name)> PlaceableDevices(string kind)
    {
        if (kind == "camera")
        {
            foreach (var c in new ChannelRepository(_store).List())
            {
                yield return ("camera", c.Id, $"{Localizer.T("Settings.MapPinKindCamera")} {c.Name}");
            }

            yield break;
        }

        foreach (var c in _io.ListChannels())
        {
            var direction = c.Direction?.ToLowerInvariant() ?? "di";
            if (kind != "io" && direction != kind)
            {
                continue;
            }

            var label = direction == "do"
                ? Localizer.T("Settings.MapPinKindDo")
                : Localizer.T("Settings.MapPinKindDi");
            yield return (direction, c.Id, $"{label} {c.Name}");
        }
    }

    private void OnMapBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Localizer.T("Settings.MapFileFilter"),
            Title = Localizer.T("Settings.MapFileDialogTitle"),
        };
        if (dlg.ShowDialog(this) == true)
        {
            MapImageBox.Text = dlg.FileName;
        }
    }

    private void OnMapAddClicked(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(MapNameBox.Text) ? Localizer.T("Settings.MapDefaultName") : MapNameBox.Text.Trim();
        var source = MapImageBox.Text.Trim();
        if (source.Length == 0)
        {
            // try browse
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Localizer.T("Settings.MapFileFilter"),
                Title = Localizer.T("Settings.MapFileDialogTitle"),
            };
            if (dlg.ShowDialog(this) == true)
            {
                source = dlg.FileName;
                MapImageBox.Text = source;
            }
            else
            {
                MapReportText.Text = Localizer.T("Settings.MapNameImageRequired");
                return;
            }
        }

        if (!File.Exists(source))
        {
            MapReportText.Text = string.Format(Localizer.T("Settings.MapImageMissing"), source);
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = LoadBitmap(source);
        }
        catch (Exception ex)
        {
            MapReportText.Text = string.Format(Localizer.T("Settings.MapImageUnreadable"), ex.Message);
            return;
        }

        Directory.CreateDirectory(_mapsRoot);
        var ext = Path.GetExtension(source);
        if (ext.Length == 0)
        {
            ext = ".png";
        }

        var target = Path.Combine(_mapsRoot, $"map-{Guid.NewGuid():N}{ext}");
        File.Copy(source, target, overwrite: true);

        _maps.AddMap(name, target, bitmap.PixelWidth, bitmap.PixelHeight, sortOrder: _maps.ListMaps().Count);
        MapNameBox.Clear();
        MapImageBox.Clear();
        MapReportText.Text = string.Format(Localizer.T("Settings.MapAdded"), name, bitmap.PixelWidth, bitmap.PixelHeight);
        ReloadMaps();
    }

    private void OnMapRefreshClicked(object sender, RoutedEventArgs e) => ReloadMaps();

    private void OnMapSelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadMapPins();

    private void OnMapToggleClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedMap is not { } map)
        {
            MapReportText.Text = Localizer.T("Settings.SelectMapFirst");
            return;
        }

        _maps.SetMapEnabled(map.Id, !map.Enabled);
        ReloadMaps();
    }

    private void OnMapDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedMap is not { } map)
        {
            MapReportText.Text = Localizer.T("Settings.SelectMapFirst");
            return;
        }

        _maps.DeleteMap(map.Id);
        MapReportText.Text = string.Format(Localizer.T("Settings.MapDeleted"), map.Name);
        ReloadMaps();
    }

    private void OnMapPinKindChanged(object sender, SelectionChangedEventArgs e)
    {
        // 換圖層等於換一組設備，保留舊選取沒有意義（camera id 與 io id 會撞號）。
        try { ReloadMapPinChannelCombo(keepSelection: false); } catch { }
        if (MapPinHint != null)
        {
            MapPinHint.Text = Localizer.T("Settings.MapPinEditHint");
        }
    }

    private void OnMapEditToggleClicked(object sender, RoutedEventArgs e)
    {
        if (MapPinCanvas != null)
        {
            MapPinCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        }
        if (MapPinHint != null)
        {
            MapPinHint.Text = Localizer.T("Settings.MapPinEditHint");
        }
        try { ReloadMapPinChannelCombo(); } catch { }
    }

    private void OnMapPlacePinClicked(object sender, RoutedEventArgs e)
    {
        if (MapPinCanvas != null) MapPinCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        try { ReloadMapPinChannelCombo(); } catch { }
        if (MapPinHint != null) MapPinHint.Text = Localizer.T("Settings.MapPinReadyHint");
    }

    private void OnMapReloadPinsClicked(object sender, RoutedEventArgs e)
    {
        try { ReloadMapPinChannelCombo(); } catch { }
        try { ReloadMapPins(); } catch { }
        if (MapPinHint != null) MapPinHint.Text = Localizer.T("Settings.MapPinsReloaded");
    }

    private void OnMapPinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapPinList.SelectedItem is not MapPinRow row)
        {
            return;
        }

        var dev = _maps.GetDevice(row.Id);
        if (dev is null)
        {
            return;
        }

        MapPinAngleBox.Text = dev.Angle.ToString("0.###", CultureInfo.InvariantCulture);
        MapPinFovBox.Text = dev.FovDeg.ToString("0.###", CultureInfo.InvariantCulture);
        MapPinDepthBox.Text = dev.FovDepth.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void OnMapApplyScaleClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedMap is not { } map)
        {
            MapReportText.Text = Localizer.T("Settings.SelectMapFirst");
            return;
        }

        if (!double.TryParse(MapScaleBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            scale < 0)
        {
            MapReportText.Text = Localizer.T("Settings.MapScaleInvalid");
            return;
        }

        try
        {
            _maps.SetMapScale(map.Id, scale);
            MapReportText.Text = scale > 0
                ? string.Format(Localizer.T("Settings.MapScaleSet"), map.Name, MapGeometry.ScaleLabel(scale))
                : string.Format(Localizer.T("Settings.MapScaleCleared"), map.Name);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            MapReportText.Text = ex.Message;
        }

        ReloadMaps();
    }

    private void OnMapApplyGeometryClicked(object sender, RoutedEventArgs e)
    {
        if (MapPinList.SelectedItem is not MapPinRow row)
        {
            MapReportText.Text = Localizer.T("Settings.SelectPinFirst");
            return;
        }

        if (!TryReadGeometry(out var angle, out var fovDeg, out var fovDepth))
        {
            return;
        }

        try
        {
            _maps.UpdateDeviceGeometry(row.Id, angle, fovDeg, fovDepth);
            MapReportText.Text = string.Format(Localizer.T("Settings.MapGeometryUpdated"), row.DeviceType, row.ChannelId, angle, fovDeg, fovDepth, MapGeometry.Bearing(angle));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            MapReportText.Text = ex.Message;
        }

        ReloadMapPins();
    }

    private bool TryReadGeometry(out double angle, out double fovDeg, out double fovDepth)
    {
        angle = 0;
        fovDeg = 90;
        fovDepth = 3;
        if (!TryParseBox(MapPinAngleBox, out angle) ||
            !TryParseBox(MapPinFovBox, out fovDeg) ||
            !TryParseBox(MapPinDepthBox, out fovDepth))
        {
            MapReportText.Text = Localizer.T("Settings.MapGeometryNumeric");
            return false;
        }

        try
        {
            angle = MapGeometry.ValidateGeometry(angle, fovDeg, fovDepth);
            return true;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            MapReportText.Text = ex.Message;
            return false;
        }
    }

    private static bool TryParseBox(TextBox box, out double value)
        => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           || double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void OnMapPinCanvasClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource;
        if (src is Ellipse)
        {
            return;
        }
        if (src is FrameworkElement fe && fe.Tag is string t && t.StartsWith("md:", StringComparison.Ordinal))
        {
            return;
        }
        e.Handled = true;

        var map = SelectedMap;
        if (map is null)
        {
            MapReportText.Text = Localizer.T("Settings.SelectMapFirst");
            return;
        }

        // 這裡刻意不再重建設備清單。舊版每次點地圖都呼叫 ReloadMapPinChannelCombo()，
        // 而它會把 SelectedIndex 重設成 0——操作員選好的攝影機在點下去的瞬間被換成第一台，
        // 於是「只能新增攝影機 01」。清單的維護交給圖層切換與放置後的重載。
        if (MapPinChannelCombo.SelectedItem is not ComboBoxItem { IsEnabled: true, Tag: int channelId })
        {
            MapReportText.Text = MapPinChannelCombo.Items.OfType<ComboBoxItem>().Any(i => i.IsEnabled)
                ? Localizer.T("Settings.MapNoChannel")
                : Localizer.T("Settings.MapPinDuplicate");
            return;
        }

        var kind = PinKind();
        double angle = 0, fovDeg = 90, fovDepth = 3;
        try { angle = MapGeometry.ValidateGeometry(0, 90, 3); } catch { angle = 0; }

        if (MapPinCanvas.Width <= 0 || MapPinCanvas.Height <= 0)
        {
            return;
        }

        var pos = e.GetPosition(MapPinCanvas);
        var w = MapPinCanvas.ActualWidth > 0 ? MapPinCanvas.ActualWidth : MapPinCanvas.Width;
        var h = MapPinCanvas.ActualHeight > 0 ? MapPinCanvas.ActualHeight : MapPinCanvas.Height;
        var x = Math.Clamp(pos.X / w, 0, 1);
        var y = Math.Clamp(pos.Y / h, 0, 1);

        try
        {
            _maps.AddDevice(map.Id, kind, channelId, x, y, angle, fovDeg, fovDepth);
            MapReportText.Text = string.Format(Localizer.T("Settings.MapPinPlaced"), kind, channelId, x, y);
        }
        catch (Exception ex)
        {
            MapReportText.Text = string.Format(Localizer.T("Settings.MapPinPlaceFailed"), ex.Message);
        }

        try { ReloadMapPins(); } catch { }
        try { Dispatcher.Invoke(() => { try { ReloadMapPins(); } catch { } }, System.Windows.Threading.DispatcherPriority.Render); } catch { }
    }

    private FrameworkElement? _dragPin;

    private void OnMapPinDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse dot || dot.Tag is not string tag || !tag.StartsWith("md:", StringComparison.Ordinal))
        {
            return;
        }

        _dragPin = dot;
        dot.CaptureMouse();
        e.Handled = true;
    }

    private void OnMapPinDragMove(object sender, MouseEventArgs e)
    {
        if (_dragPin is not Ellipse dot || ReferenceEquals(_dragPin, sender) is false || !dot.IsMouseCaptured)
        {
            return;
        }

        var pos = e.GetPosition(MapPinCanvas);
        Canvas.SetLeft(dot, pos.X - dot.Width / 2);
        Canvas.SetTop(dot, pos.Y - dot.Height / 2);
        e.Handled = true;
    }

    private void OnMapPinDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragPin is not Ellipse dot || dot.Tag is not string tag || !tag.StartsWith("md:", StringComparison.Ordinal))
        {
            return;
        }

        if (dot.IsMouseCaptured)
        {
            dot.ReleaseMouseCapture();
        }

        _dragPin = null;
        if (MapPinCanvas.Width <= 0 || MapPinCanvas.Height <= 0)
        {
            return;
        }

        var id = int.Parse(tag.AsSpan("md:".Length));
        var pos = e.GetPosition(MapPinCanvas);
        _maps.SetDevicePosition(id, Math.Clamp(pos.X / MapPinCanvas.Width, 0, 1), Math.Clamp(pos.Y / MapPinCanvas.Height, 0, 1));
        MapReportText.Text = string.Format(Localizer.T("Settings.MapPinMoved"), pos.X / MapPinCanvas.Width, pos.Y / MapPinCanvas.Height);
        try { ReloadMapPins(); } catch { }
    }

    private void OnMapDeletePinClicked(object sender, RoutedEventArgs e)
    {
        if (MapPinList.SelectedItem is not MapPinRow row)
        {
            MapReportText.Text = Localizer.T("Settings.SelectPinFirst");
            return;
        }

        _maps.DeleteDevice(row.Id);
        MapReportText.Text = string.Format(Localizer.T("Settings.MapPinDeleted"), row.DeviceType, row.ChannelId);
        ReloadMapPins();
    }

    /// <summary>
    /// 自動布局選取地圖的圖釘：把重疊的圖釘推開後逐一寫回（M284，§14.7 #11）。
    ///
    /// 平面圖上落點接近的鏡頭會完全疊住，畫面上只看得到最上面那一個，操作員還以為其他
    /// 鏡頭沒設定；這裡用純函式 <see cref="MapLayout.Resolve"/> 重排並持久化，讓批次上版
    /// 或重畫平面圖之後圖釘仍不會疊在一起。
    /// </summary>
    private void OnMapAutoLayoutClicked(object sender, RoutedEventArgs e)
    {
        var map = SelectedMap;
        if (map is null)
        {
            MapReportText.Text = Localizer.T("Settings.SelectMapFirst");
            return;
        }

        var pins = _maps.ListDevices(map.Id).ToList();
        if (pins.Count == 0)
        {
            MapReportText.Text = Localizer.T("Settings.MapLayoutNone");
            return;
        }

        var placements = MapLayout.Resolve(
            pins.Select(d => new MapLayout.Pin(d.Id, d.X, d.Y)).ToList());
        var byId = placements.ToDictionary(p => p.Id);
        foreach (var d in pins)
        {
            if (!byId.TryGetValue(d.Id, out var placed)) continue;
            try { _maps.SetDevicePosition(d.Id, placed.X, placed.Y); } catch { }
        }

        ReloadMapPins();
        MapReportText.Text = string.Format(Localizer.T("Settings.MapLayoutDone"), placements.Count);
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        return bi;
    }

    // ── 身份與權限（M42，§18.6） ──

    private void ReloadAuthPolicy()
    {
        var auth = new AuthService(_store);
        AuthEnabledBox.IsChecked = auth.IsAuthEnabled;
        AuthThresholdBox.Text = auth.LockoutThreshold.ToString(CultureInfo.InvariantCulture);
        AuthMinutesBox.Text = auth.LockoutMinutes.ToString(CultureInfo.InvariantCulture);
    }

    private void ReloadUsers()
    {
        UserList.ItemsSource = _users.ListUsers().Select(u => new UserRow(
            u.Id,
            u.Username,
            u.Role,
            u.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
            u.FailedLogins > 0 ? $"{u.FailedLogins}{(u.LockedUntil is { Length: > 0 } ? Localizer.T("Settings.LockedSuffix") : string.Empty)}" : "0",
            u.LastLogin is { Length: > 0 } ll
                ? SqliteStore.FromIso(ll).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "—")).ToList();

        UserToggleButton.IsEnabled = false;
        UserDeleteButton.IsEnabled = false;
        UserUnlockButton.IsEnabled = false;
    }

    // ── 企業身份（M50，§14.7 #1：OIDC／LDAP SSO） ──

    private void ReloadEnterpriseProviders()
    {
        EnterpriseProviderList.ItemsSource = _enterprise.ListProviders()
            .Select(p => new EnterpriseProviderRow(
                p.Id,
                p.Name,
                p.Kind.ToUpperInvariant(),
                p.Enabled ? Localizer.T("Settings.Enabled") : Localizer.T("Settings.Disabled"),
                SummarizeProvider(p)))
            .ToList();
    }

    private static string SummarizeProvider(AuthProviderRecord provider)
        => provider.Kind == AuthProviderRepository.KindOidc
            ? OidcOptions.FromJson(provider.ConfigJson).Issuer
            : LdapSettings.FromJson(provider.ConfigJson).Host;

    private void OnEntAddClicked(object sender, RoutedEventArgs e)
    {
        // ad 旗標限定（M219）：整區已隱藏，處理器仍要再擋一次。
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.AdSso))
        {
            EntReportText.Text = gate.DenialMessage(LicenseFeatures.AdSso);
            return;
        }

        var name = EntNameBox.Text.Trim();
        if (name.Length == 0)
        {
            EntReportText.Text = Localizer.T("Settings.ProviderNameRequired");
            return;
        }

        var kind = (EntKindCombo.SelectedItem as ComboBoxItem)?.Content as string ?? AuthProviderRepository.KindOidc;
        var issuer = EntIssuerBox.Text.Trim();
        var audience = EntAudienceBox.Text.Trim();
        var adminGroups = EntAdminGroupsBox.Text
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var defaultRole = (EntDefaultRoleCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "viewer";

        string configJson;
        if (kind == AuthProviderRepository.KindOidc)
        {
            if (issuer.Length == 0)
            {
                EntReportText.Text = Localizer.T("Settings.ProviderIssuerRequired");
                return;
            }

            var roleClaim = EntRoleClaimBox.Text.Trim();
            configJson = new OidcOptions
            {
                Issuer = issuer,
                Audience = audience,
                ClientId = audience.Length > 0 ? audience : null,
                RoleClaim = roleClaim.Length > 0 ? roleClaim : "roles",
                AdminGroups = adminGroups,
                DefaultRole = defaultRole,
                JwksJson = EntJwksBox.Text.Trim().Length > 0 ? EntJwksBox.Text.Trim() : null,
            }.ToJson();
        }
        else
        {
            configJson = new LdapSettings
            {
                Host = issuer,
                BaseDn = audience,
                AdminGroups = adminGroups,
                DefaultRole = defaultRole,
            }.ToJson();
        }

        try
        {
            _providers.Add(name, kind, configJson);
            EntReportText.Text = string.Format(Localizer.T("Settings.ProviderAdded"), name, kind);
            ReloadEnterpriseProviders();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            EntReportText.Text = string.Format(Localizer.T("Settings.ProviderNameExists"), name);
        }
    }

    private void OnEntToggleClicked(object sender, RoutedEventArgs e)
    {
        // ad 旗標限定（M219）：整區已隱藏，處理器仍要再擋一次。
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.AdSso))
        {
            EntReportText.Text = gate.DenialMessage(LicenseFeatures.AdSso);
            return;
        }

        if (EnterpriseProviderList.SelectedItem is not EnterpriseProviderRow row)
        {
            EntReportText.Text = Localizer.T("Settings.SelectProviderFirst");
            return;
        }

        var record = _providers.Get(row.Id);
        if (record is null)
        {
            EntReportText.Text = Localizer.T("Settings.ProviderMissing");
            ReloadEnterpriseProviders();
            return;
        }

        _providers.SetEnabled(row.Id, !record.Enabled);
        EntReportText.Text = string.Format(Localizer.T("Settings.ProviderToggled"), row.Name, record.Enabled ? Localizer.T("Settings.Disabled") : Localizer.T("Settings.Enabled"));
        ReloadEnterpriseProviders();
    }

    private void OnEntDeleteClicked(object sender, RoutedEventArgs e)
    {
        // ad 旗標限定（M219）：整區已隱藏，處理器仍要再擋一次。
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.AdSso))
        {
            EntReportText.Text = gate.DenialMessage(LicenseFeatures.AdSso);
            return;
        }

        if (EnterpriseProviderList.SelectedItem is not EnterpriseProviderRow row)
        {
            EntReportText.Text = Localizer.T("Settings.SelectProviderFirst");
            return;
        }

        _providers.Delete(row.Id);
        EntReportText.Text = string.Format(Localizer.T("Settings.ProviderDeleted"), row.Name);
        ReloadEnterpriseProviders();
    }

    private void OnAuthEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAuthToggle)
        {
            return;
        }

        var enabled = AuthEnabledBox.IsChecked == true;

        // 啟用前必須至少有一個啟用中的 admin。否則下次啟動會卡在 LoginWindow，
        // 沒有帳號能登入，等於把操作者鎖在門外（只能改 DB 救援）。
        if (enabled && !new AuthService(_store).HasEnabledAdmin())
        {
            _suppressAuthToggle = true;
            AuthEnabledBox.IsChecked = false;
            _suppressAuthToggle = false;
            UserReportText.Text = Localizer.T("Settings.AuthNoAdmin");
            return;
        }

        _settings.Set("auth.enabled", enabled ? "1" : "0");
        UserReportText.Text = enabled ? Localizer.T("Settings.AuthEnabledNotice") : Localizer.T("Settings.AuthDisabledNotice");
    }

    private void OnAuthApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AuthThresholdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold) || threshold < 1 ||
            !int.TryParse(AuthMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
        {
            UserReportText.Text = Localizer.T("Settings.LoginLockoutPositive");
            return;
        }

        _settings.Set("auth.lockout.threshold", threshold.ToString(CultureInfo.InvariantCulture));
        _settings.Set("auth.lockout.minutes", minutes.ToString(CultureInfo.InvariantCulture));
        UserReportText.Text = string.Format(Localizer.T("Settings.LoginLockoutApplied"), threshold, minutes);
    }

    private void OnUserAddClicked(object sender, RoutedEventArgs e)
    {
        var name = UserAddNameBox.Text.Trim();
        var password = UserAddPasswordBox.Password;
        if (name.Length == 0 || password.Length == 0)
        {
            UserReportText.Text = Localizer.T("Settings.UserNamePasswordRequired");
            return;
        }

        var role = (UserAddRoleCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "viewer";
        try
        {
            _users.CreateUser(name, PasswordHasher.Hash(password), role);
            UserReportText.Text = string.Format(Localizer.T("Settings.UserAdded"), name, role);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            UserReportText.Text = string.Format(Localizer.T("Settings.UserNameExists"), name);
            return;
        }

        UserAddNameBox.Clear();
        UserAddPasswordBox.Clear();
        ReloadUsers();
    }

    private void OnUserSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = UserList.SelectedItem is UserRow;
        UserToggleButton.IsEnabled = selected;
        UserDeleteButton.IsEnabled = selected;
        UserUnlockButton.IsEnabled = selected;
    }

    private void OnUserToggleClicked(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRow row)
        {
            UserReportText.Text = Localizer.T("Settings.SelectUserFirst");
            return;
        }

        var user = _users.GetById(row.Id);
        if (user is null)
        {
            return;
        }

        // 停用最後一個啟用中的 admin（且登入驗證已開）＝把自己鎖在門外。
        if (user.Enabled && new AuthService(_store).WouldRemoveLastEnabledAdmin(row.Id))
        {
            UserReportText.Text = string.Format(Localizer.T("Settings.CannotDisableLastAdmin"), row.Username);
            return;
        }

        _users.SetEnabled(row.Id, !user.Enabled);
        UserReportText.Text = string.Format(Localizer.T("Settings.UserToggled"), row.Username, user.Enabled ? Localizer.T("Settings.Disabled") : Localizer.T("Settings.Enabled"));
        ReloadUsers();
    }

    private void OnUserUnlockClicked(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRow row)
        {
            UserReportText.Text = Localizer.T("Settings.SelectUserFirst");
            return;
        }

        _users.ClearLock(row.Id);
        UserReportText.Text = string.Format(Localizer.T("Settings.UserUnlocked"), row.Username);
        ReloadUsers();
    }

    private void OnUserDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRow row)
        {
            UserReportText.Text = Localizer.T("Settings.SelectUserFirst");
            return;
        }

        // 刪除最後一個啟用中的 admin（且登入驗證已開）＝把自己鎖在門外。
        if (new AuthService(_store).WouldRemoveLastEnabledAdmin(row.Id))
        {
            UserReportText.Text = string.Format(Localizer.T("Settings.CannotDeleteLastAdmin"), row.Username);
            return;
        }

        _users.DeleteUser(row.Id);
        UserReportText.Text = string.Format(Localizer.T("Settings.UserDeleted"), row.Username);
        ReloadUsers();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { }
        Close();
    }
}