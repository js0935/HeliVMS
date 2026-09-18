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
using HeliVMS.Recording;
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
    private const string QuotaKey = "recording.quota_gb";
    private const double DefaultQuotaGb = 10.0;
    private const string SnapshotDaysKey = "snapshots.retention_days";
    private const int DefaultSnapshotDays = 30;
    private const string TamperKey = "detect.tamper.enabled";

    /// <summary>頻道頁顯示列。</summary>
    private sealed record ChannelRow(int Id, string Name, string MainStreamUrl, string RecordingModeLabel, string MotionLabel);

    /// <summary>告警規則頁顯示列。</summary>
    private sealed record RuleRow(long Id, string Name, string EventType, string ChannelLabel, string Keyword, string Channels, string EnabledLabel);

    /// <summary>IO 模組頁顯示列。</summary>
    private sealed record IoDeviceRow(int Id, string Name, string Host, string EndpointLabel, string EnabledLabel, int PollMs);

    /// <summary>IO 通道頁顯示列。</summary>
    private sealed record IoChannelRow(int Id, string Direction, int IoIndex, string Name, string EnabledLabel, string NcLabel, int DebounceMs, string CameraLabel);
    private sealed record MapRow(int Id, string Name, string SizeLabel, string EnabledLabel, int SortOrder, string ScaleLabel);
    private sealed record MapPinRow(int Id, string DeviceType, int ChannelId, string PositionLabel, string AngleLabel, string FovLabel, string DepthLabel, string BearingLabel, string EnabledLabel, string Name);
    private sealed record UserRow(int Id, string Username, string Role, string EnabledLabel, string FailedLabel, string LastLoginLabel);

    /// <summary>備份紀錄頁顯示列。</summary>
    private sealed record BackupRow(string TimeLabel, string TargetLabel, string CopiedLabel, string FailedLabel, string AdvancedLabel);

    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly SettingsRepository _settings;
    private readonly AlertRuleRepository _rules;
    private readonly IoRepository _io;
    private readonly IoMonitorHost? _ioHost;
    private readonly MapRepository _maps;
    private readonly string _mapsRoot;
    private readonly UserRepository _users;

    public SettingsWindow(SqliteStore store, string dataRoot, IoMonitorHost? ioHost = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        _recordingsRoot = Path.Combine(dataRoot, "recordings");
        _snapshotsRoot = Path.Combine(dataRoot, "snapshots");
        _settings = new SettingsRepository(store);
        _rules = new AlertRuleRepository(store);
        _io = new IoRepository(store);
        _ioHost = ioHost;
        _maps = new MapRepository(store);
        _mapsRoot = Path.Combine(dataRoot, "maps");
        _users = new UserRepository(store);

        InitializeComponent();
        MapPinCanvas.MouseLeftButtonUp += OnMapPinCanvasClick;
        MapPinList.SelectionChanged += OnMapPinSelectionChanged;

        VersionText.Text = $"HeliVMS {Assembly.GetExecutingAssembly().GetName().Version}";
        DataRootText.Text = _dataRoot;
        RecordingsRootText.Text = _recordingsRoot;
        SnapshotsRootText.Text = _snapshotsRoot;

        ReloadQuota();
        ReloadSnapshotDays();
        ReloadUsage();
        ReloadLicense();
        ReloadLaunchAvailability();
        ReloadChannels();
        ReloadNotify();
        ReloadRules();
        ReloadTamper();
        ReloadIo();
        ReloadMaps();
        ReloadAuthPolicy();
        ReloadUsers();
        ReloadBackup();

        SettingsNav.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav is null || SettingsNav.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var visible = item.Content as string ?? string.Empty;
        PageGeneral.Visibility = visible == "一般" ? Visibility.Visible : Visibility.Collapsed;
        PageStorage.Visibility = visible == "儲存" ? Visibility.Visible : Visibility.Collapsed;
        PageChannels.Visibility = visible == "頻道" ? Visibility.Visible : Visibility.Collapsed;
        PageLicense.Visibility = visible == "授權" ? Visibility.Visible : Visibility.Collapsed;
        PageLaunch.Visibility = visible == "功能" ? Visibility.Visible : Visibility.Collapsed;
        PageNotify.Visibility = visible == "通知" ? Visibility.Visible : Visibility.Collapsed;
        PageRules.Visibility = visible == "規則" ? Visibility.Visible : Visibility.Collapsed;
        PageIo.Visibility = visible == "IO" ? Visibility.Visible : Visibility.Collapsed;
        PageMap.Visibility = visible == "地圖" ? Visibility.Visible : Visibility.Collapsed;
        PageUsers.Visibility = visible == "身份" ? Visibility.Visible : Visibility.Collapsed;
        PageBackup.Visibility = visible == "備份" ? Visibility.Visible : Visibility.Collapsed;
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
    }

    /// <summary>將備份設定（啟用/目標/間隔）寫入 app_settings；參數不合法時回傳 false。</summary>
    private bool SaveBackupSettings()
    {
        _settings.Set("backup.enabled", BackupEnabledBox.IsChecked == true ? "1" : "0");
        _settings.Set("backup.target", BackupTargetBox.Text.Trim());
        if (!double.TryParse(BackupIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) ||
            interval <= 0)
        {
            BackupStatusText.Text = "間隔需為大於 0 的數字。";
            return false;
        }

        _settings.Set("backup.interval_hours", interval.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private void OnSaveBackupClicked(object sender, RoutedEventArgs e)
    {
        if (SaveBackupSettings())
        {
            BackupStatusText.Text = "設定已儲存。";
        }
    }

    private void OnBackupBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "選擇備份目標目錄" };
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
            BackupStatusText.Text = "請先指定目標目錄。";
            return;
        }

        BackupNowButton.IsEnabled = false;
        BackupStatusText.Text = "備份執行中…";
        try
        {
            var result = await Task.Run(() => new BackupService(_store).Run(_recordingsRoot, target));
            BackupStatusText.Text = result.Failed == 0
                ? $"備份完成：掃描 {result.Scanned} 段、複製 {result.Copied} 段（{FormatBytes(result.CopiedBytes)}）、檢查點已推進。"
                : $"備份完成：掃描 {result.Scanned} 段、複製 {result.Copied} 段、失敗 {result.Failed}（未推進，下次重試）。";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"備份失敗：{ex.Message}";
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
            r.CheckpointUtc.HasValue ? "是" : "否")).ToList();
    }

    private void ReloadRules()
    {
        RuleChannelCombo.Items.Clear();
        RuleChannelCombo.Items.Add(new ComboBoxItem { Content = "不限", Tag = null });
        foreach (var c in new ChannelRepository(_store).List())
        {
            RuleChannelCombo.Items.Add(new ComboBoxItem { Content = $"{c.Name}（#{c.Id}）", Tag = c.Id });
        }

        RuleChannelCombo.SelectedIndex = 0;

        RuleList.ItemsSource = _rules.ListAll().Select(r => new RuleRow(
            r.Id,
            r.Name,
            r.EventType ?? "不限",
            r.ChannelId is { } cid ? $"#{cid}" : "不限",
            r.Keyword ?? "不限",
            FormatRuleChannels(r.Channels),
            r.Enabled ? "啟用" : "停用")).ToList();
    }

    /// <summary>M39：載入遮蔽偵測開關（app_settings `detect.tamper.enabled`）。</summary>
    private void ReloadTamper()
    {
        TamperEnabledBox.IsChecked = string.Equals(_settings.Get(TamperKey), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>M39：切換即寫入 app_settings（重新連線頻道後生效）。</summary>
    private void OnTamperToggled(object sender, RoutedEventArgs e)
    {
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
            d.Enabled ? "啟用" : "停用",
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
        IoCameraCombo.Items.Add(new ComboBoxItem { Content = "（不綁定）", Tag = null });
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
                ch.Enabled ? "啟用" : "停用",
                ch.Polarity ? "NC" : "",
                ch.DebounceMs,
                ch.CameraId is { } cam && cameras.TryGetValue(cam, out var n) ? n : "—")).ToList();
        IoChannelList.ItemsSource = rows;
        IoReportText.Text = devId is not null && rows.Count == 0 ? "此模組尚無通道。" : " ";
    }

    private void OnIoAddDeviceClicked(object sender, RoutedEventArgs e)
    {
        var name = IoNameBox.Text.Trim();
        var host = IoHostBox.Text.Trim();
        if (name.Length == 0 || host.Length == 0)
        {
            IoReportText.Text = "請填模組名稱與主機。";
            return;
        }

        if (!int.TryParse(IoPortBox.Text, out var port) || port is <= 0 or > 65535)
        {
            IoReportText.Text = "連接埠無效（1–65535）。";
            return;
        }

        if (!int.TryParse(IoUnitBox.Text, out var unit) || unit is < 0 or > 247)
        {
            IoReportText.Text = "Unit ID 需為 0–247。";
            return;
        }

        if (!int.TryParse(IoPollBox.Text, out var pollMs) || pollMs < 50)
        {
            IoReportText.Text = "輪詢間隔需 ≥50 ms。";
            return;
        }

        _io.AddDevice(name, host, port, unit, pollMs);
        IoNameBox.Clear();
        IoHostBox.Clear();
        IoReportText.Text = $"已新增模組「{name}」。";
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
            IoReportText.Text = "請先選取模組。";
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
            IoReportText.Text = "請先選取模組。";
            return;
        }

        _io.DeleteDevice(dev.Id);
        IoReportText.Text = $"已刪除模組 #{dev.Id}。";
        ReloadIo();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedIoDevice is not { } dev)
        {
            IoReportText.Text = "請先選取要新增通道的模組。";
            return;
        }

        if (IoDirCombo.SelectedItem is not ComboBoxItem dirItem)
        {
            IoReportText.Text = "請選擇方向（DI／DO）。";
            return;
        }

        var direction = dirItem.Content as string ?? "DI";
        if (!int.TryParse(IoIndexBox.Text, out var idx) || idx is < 0 or > 65535)
        {
            IoReportText.Text = "IO index 需為 0–65535。";
            return;
        }

        var name = IoChannelNameBox.Text.Trim();
        if (name.Length == 0)
        {
            IoReportText.Text = "請填通道名稱。";
            return;
        }

        if (!int.TryParse(IoDebounceBox.Text, out var debounce) || debounce < 0)
        {
            IoReportText.Text = "去抖需 ≥0 ms。";
            return;
        }

        int? cameraId = IoCameraCombo.SelectedItem is ComboBoxItem { Tag: int cam } ? cam : null;
        if (direction == "DI" && cameraId is null)
        {
            IoReportText.Text = "DI 通道必須綁定相機（io_input 事件才能寫入事件中心）。";
            return;
        }

        try
        {
            _io.AddChannel(dev.Id, direction, idx, name, debounce, IoNcBox.IsChecked == true, cameraId);
        }
        catch (Exception ex)
        {
            IoReportText.Text = $"新增失敗：{ex.Message}";
            return;
        }

        IoIndexBox.Clear();
        IoChannelNameBox.Clear();
        IoNcBox.IsChecked = false;
        IoReportText.Text = $"已新增通道「{name}」（{direction} #{idx}）。";
        ReloadIoChannels();
        ReloadIoDoChannels();
        _ioHost?.RefreshAndStart();
    }

    private void OnIoToggleChannelClicked(object sender, RoutedEventArgs e)
    {
        if (IoChannelList.SelectedItem is not IoChannelRow row)
        {
            IoReportText.Text = "請先選取通道。";
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
            IoReportText.Text = "請先選取通道。";
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
            IoDoStatusText.Text = "主視窗尚未接線監視器（IoMonitorHost）。";
            return;
        }

        if (IoDoDeviceCombo.SelectedItem is not ComboBoxItem { Tag: int devId } ||
            IoDoChannelCombo.SelectedItem is not ComboBoxItem { Tag: int chId } ||
            sender is not Button btn)
        {
            IoDoStatusText.Text = "請先選取模組與 DO 通道。";
            return;
        }

        var on = string.Equals(btn.Tag?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var ok = await _ioHost.WriteOutputAsync(devId, chId, on);
        IoDoStatusText.Text = ok
            ? $"DO #{chId} 已設 {(on ? "ON" : "OFF")}（FC05 完成）。"
            : "寫出失敗（裝置離線或非 DO 通道）。";
    }

    private static string FormatRuleChannels(string? channels)
        => string.IsNullOrWhiteSpace(channels) ? "全部通道" : string.Join("＋",
            channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private string? BuildRuleChannels()
    {
        var set = new List<string>();
        if (RuleChannelWebhookBox.IsChecked == true) set.Add("webhook");
        if (RuleChannelSmtpBox.IsChecked == true) set.Add("smtp");
        if (RuleChannelMqttBox.IsChecked == true) set.Add("mqtt");
        if (RuleChannelPushBox.IsChecked == true) set.Add("push");
        if (RuleChannelSnmpBox.IsChecked == true) set.Add("snmp");
        return set.Count == 0 ? null : string.Join(",", set);
    }

    private void OnAddRuleClicked(object sender, RoutedEventArgs e)
    {
        var name = RuleNameBox.Text.Trim();
        if (name.Length == 0)
        {
            RuleReportText.Text = "請填規則名稱。";
            return;
        }

        var eventType = RuleEventTypeBox.Text.Trim();
        int? channelId = RuleChannelCombo.SelectedItem is ComboBoxItem { Tag: int cid } ? cid : null;
        var keyword = RuleKeywordBox.Text.Trim();
        var channels = BuildRuleChannels();

        _rules.Add(
            name,
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            channelId,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            channels);

        RuleReportText.Text = $"已新增「{name}」。";
        RuleNameBox.Clear();
        RuleEventTypeBox.Clear();
        RuleKeywordBox.Clear();
        RuleChannelCombo.SelectedIndex = 0;
        RuleChannelWebhookBox.IsChecked = false;
        RuleChannelSmtpBox.IsChecked = false;
        RuleChannelMqttBox.IsChecked = false;
        RuleChannelPushBox.IsChecked = false;
        RuleChannelSnmpBox.IsChecked = false;
        ReloadRules();
    }

    private void OnToggleRuleClicked(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row)
        {
            RuleReportText.Text = "請先選取規則。";
            return;
        }

        var rule = _rules.ListAll().First(r => r.Id == row.Id);
        _rules.SetEnabled(rule.Id, !rule.Enabled);
        RuleReportText.Text = $"「{rule.Name}」已{(rule.Enabled ? "停用" : "啟用")}。";
        ReloadRules();
    }

    private void OnDeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row)
        {
            RuleReportText.Text = "請先選取規則。";
            return;
        }

        _rules.Delete(row.Id);
        RuleReportText.Text = $"已刪除規則。";
        ReloadRules();
    }

    private void ReloadQuota()
    {
        var quotaGb = QuotaGbFromStore();
        QuotaTextBox.Text = quotaGb.ToString("0.#", CultureInfo.InvariantCulture);
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
    }

    private void OnApplyNotifyClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NotifySmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port <= 0)
        {
            NotifyReportText.Text = "SMTP 埠必須是大於 0 的整數。";
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

        NotifyReportText.Text = "通知設定已套用（密碼以 DPAPI 加密保存）。";
    }

    private void OnApplySnapshotDaysClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SnapshotDaysTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ||
            days <= 0)
        {
            CleanupReportText.Text = "請輸入大於 0 的天數。";
            return;
        }

        _settings.Set(SnapshotDaysKey, days.ToString(CultureInfo.InvariantCulture));
        CleanupReportText.Text = $"已套用：快照保留 {days} 天（每小時例行清理讀取此值生效）";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";

    private void ReloadUsage()
    {
        long total = 0;
        var count = 0;
        if (Directory.Exists(_recordingsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_recordingsRoot, "*.mp4", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        UsageText.Text = $"{FormatBytes(total)}（{count} 段）；上限 {QuotaGbFromStore():0.#}GB";
    }

    private void OnApplyQuotaClicked(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(QuotaTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb <= 0)
        {
            CleanupReportText.Text = "請輸入大於 0 的 GB 數值。";
            return;
        }

        _settings.Set(QuotaKey, gb.ToString("0.#", CultureInfo.InvariantCulture));
        CleanupReportText.Text = $"已套用：錄影保留上限 {gb:0.#}GB（每小時例行清理讀取此值生效）";
        ReloadUsage();
    }

    private void OnCleanupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var repo = new SegmentRepository(_store);
            var service = new RetentionService(repo, _recordingsRoot);
            var report = service.Apply(QuotaBytesFromGb(QuotaGbFromStore()), DateTime.UtcNow);
            var text = $"清理完成：錄影移除 {report.DeletedSegments} 段（釋放 {FormatBytes(report.FreedBytes)}）" +
                       (report.PurgedTmp > 0 ? $"，清除 {report.PurgedTmp} 個暫存檔" : string.Empty);

            var snapDays = SnapDaysFromStore();
            var snapReport = service.PurgeSnapshots(_snapshotsRoot, DateTime.UtcNow.AddDays(-snapDays));
            if (snapReport.DeletedFiles > 0)
            {
                text += $"，快照移除 {snapReport.DeletedFiles} 個（{FormatBytes(snapReport.FreedBytes)}）";
            }
            else
            {
                text += "，無過期快照";
            }

            CleanupReportText.Text = text;
        }
        catch (Exception ex)
        {
            CleanupReportText.Text = $"清理失敗：{ex.Message}";
        }

        ReloadUsage();
    }

    private void ReloadChannels()
    {
        var rows = new ChannelRepository(_store).List()
            .Select(c => new ChannelRow(
                c.Id,
                c.Name,
                c.MainStreamUrl,
                c.RecordingMode.ToString(),
                c.MotionEnabled ? "開" : "關"))
            .ToList();
        ChannelList.ItemsSource = rows;
    }

    private void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        var url = AddChannelUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        try
        {
            new ChannelRepository(_store).Add(
                AddChannelNameBox.Text.Trim().Length > 0 ? AddChannelNameBox.Text.Trim() : "新頻道",
                url);
            CleanupReportText.Text = "已加入頻道。";
            AddChannelUrlBox.Text = string.Empty;
            ReloadChannels();
        }
        catch (Exception ex)
        {
            CleanupReportText.Text = $"加入失敗：{ex.Message}";
        }
    }

    private void OnOnvifWizardClicked(object sender, RoutedEventArgs e)
    {
        var wizard = new OnvifWizardWindow
        {
            Owner = this,
        };
        if (wizard.ShowDialog() == true && wizard.StreamUrl.Length > 0)
        {
            try
            {
                new ChannelRepository(_store).Add(wizard.ChannelName, wizard.StreamUrl);
                ReloadChannels();
            }
            catch (Exception ex)
            {
                CleanupReportText.Text = $"加入失敗：{ex.Message}";
            }
        }
    }

    private void OnRefreshChannelsClicked(object sender, RoutedEventArgs e) => ReloadChannels();

    private static long QuotaBytesFromGb(double gb) => (long)(gb * 1024 * 1024 * 1024);

    private void ReloadLicense()
    {
        var state = new LicenseManager().ValidateDefault();
        if (state.IsValid && state.Payload is not null)
        {
            var expire = state.Payload.ExpiresUtc.HasValue
                ? $"，到期 {state.Payload.ExpiresUtc.Value:u}"
                : string.Empty;
            LicenseStatusText.Text = $"已授權（{state.Payload.Cameras} 路{expire}）";
        }
        else
        {
            LicenseStatusText.Text = $"未授權：{state.Message ?? state.Status.ToString()}";
        }

        MachineText.Text = MachineIdProvider.GetFingerprint();
    }

    private void OnApplyLicenseClicked(object sender, RoutedEventArgs e)
    {
        var token = LicenseKeyBox.Text?.Trim() ?? string.Empty;
        var manager = new LicenseManager();
        var state = manager.Validate(token);
        if (!state.IsValid)
        {
            LicenseApplyText.Text = $"金鑰無效：{state.Message ?? state.Status.ToString()}";
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
            LicenseApplyText.Text = $"已套用：授權 {state.Payload!.Cameras} 路。";
        }
        catch (Exception ex)
        {
            LicenseApplyText.Text = $"寫入授權檔失敗：{ex.Message}";
        }

        ReloadLicense();
    }

    private void ReloadLaunchAvailability()
    {
        // 功能入口一律可用（視窗開啟由各 ctor 自行處理空資料庫情境）。
    }

    private void OnLaunchPlaybackClicked(object sender, RoutedEventArgs e)
        => new PlaybackWindow(_store) { Owner = this }.Show();

    private void OnLaunchEventsClicked(object sender, RoutedEventArgs e)
        => new EventCenterWindow(_store) { Owner = this }.Show();

    private void OnLaunchScheduleClicked(object sender, RoutedEventArgs e)
        => new SchedulingWindow(_store) { Owner = this }.Show();

    private void OnLaunchDetectionClicked(object sender, RoutedEventArgs e)
        => new DetectionWindow(_store) { Owner = this }.Show();

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
                m.Enabled ? "啟用" : "停用",
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

        foreach (var child in MapPinCanvas.Children.OfType<Ellipse>().ToList())
        {
            MapPinCanvas.Children.Remove(child);
        }

        MapPinList.ItemsSource = null;
        var map = SelectedMap;
        if (map is null)
        {
            MapPinImage.Source = null;
            MapPinCanvas.Width = 0;
            MapPinCanvas.Height = 0;
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = LoadBitmap(map.ImagePath);
        }
        catch (Exception ex)
        {
            MapReportText.Text = $"無法載入地圖圖檔：{ex.Message}";
            return;
        }

        MapScaleBox.Text = map.ScaleMPerPx.ToString("0.###", CultureInfo.InvariantCulture);
        MapPinImage.Source = bitmap;
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        MapPinCanvas.Width = w;
        MapPinCanvas.Height = h;
        MapPinImage.Width = w;
        MapPinImage.Height = h;

        var cameras = new ChannelRepository(_store).List().ToDictionary(c => c.Id, c => c.Name);
        var ios = _io.ListChannels().ToDictionary(c => c.Id, c => c.Name);

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
                p.Enabled ? "啟用" : "停用",
                p.DeviceType == "camera"
                    ? (cameras.TryGetValue(p.ChannelId, out var cn) ? cn : $"頻道 #{p.ChannelId}")
                    : (ios.TryGetValue(p.ChannelId, out var ioName) ? ioName : $"IO #{p.ChannelId}"))
            ).ToList();

        foreach (var p in _maps.ListDevices(map.Id))
        {
            if (!p.Enabled)
            {
                continue;
            }

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = p.DeviceType == "camera"
                    ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                    : new SolidColorBrush(Color.FromRgb(107, 122, 144)),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Tag = $"md:{p.Id}",
                ToolTip = $"{p.DeviceType} #{p.ChannelId} ({p.X:0.00}, {p.Y:0.00})",
            };
            Canvas.SetLeft(dot, p.X * w - 5);
            Canvas.SetTop(dot, p.Y * h - 5);
            dot.MouseLeftButtonDown += OnMapPinDragStart;
            dot.MouseMove += OnMapPinDragMove;
            dot.MouseLeftButtonUp += OnMapPinDragEnd;
            MapPinCanvas.Children.Add(dot);
        }
    }

    private void ReloadMapPinChannelCombo()
    {
        if (MapPinChannelCombo is null)
        {
            return;
        }

        MapPinChannelCombo.Items.Clear();
        var kind = (MapPinKindCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "camera";
        if (kind == "camera")
        {
            foreach (var c in new ChannelRepository(_store).List())
            {
                MapPinChannelCombo.Items.Add(new ComboBoxItem { Content = $"{c.Name}（#{c.Id}）", Tag = c.Id });
            }
        }
        else
        {
            foreach (var c in _io.ListChannels())
            {
                MapPinChannelCombo.Items.Add(new ComboBoxItem { Content = $"{c.Name}（#{c.Id}）", Tag = c.Id });
            }
        }

        if (MapPinChannelCombo.Items.Count > 0)
        {
            MapPinChannelCombo.SelectedIndex = 0;
        }
    }

    private void OnMapBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "平面圖（PNG/JPG/BMP）|*.png;*.jpg;*.jpeg;*.bmp|所有檔案|*.*",
            Title = "選擇平面圖",
        };
        if (dlg.ShowDialog(this) == true)
        {
            MapImageBox.Text = dlg.FileName;
        }
    }

    private void OnMapAddClicked(object sender, RoutedEventArgs e)
    {
        var name = MapNameBox.Text.Trim();
        var source = MapImageBox.Text.Trim();
        if (name.Length == 0 || source.Length == 0)
        {
            MapReportText.Text = "請填地圖名稱與圖檔路徑。";
            return;
        }

        if (!File.Exists(source))
        {
            MapReportText.Text = $"圖檔不存在：{source}";
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = LoadBitmap(source);
        }
        catch (Exception ex)
        {
            MapReportText.Text = $"無法讀取圖檔：{ex.Message}";
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
        MapReportText.Text = $"已新增地圖「{name}」（{bitmap.PixelWidth}×{bitmap.PixelHeight}）。";
        ReloadMaps();
    }

    private void OnMapRefreshClicked(object sender, RoutedEventArgs e) => ReloadMaps();

    private void OnMapSelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadMapPins();

    private void OnMapToggleClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedMap is not { } map)
        {
            MapReportText.Text = "請先選擇地圖。";
            return;
        }

        _maps.SetMapEnabled(map.Id, !map.Enabled);
        ReloadMaps();
    }

    private void OnMapDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedMap is not { } map)
        {
            MapReportText.Text = "請先選擇地圖。";
            return;
        }

        _maps.DeleteMap(map.Id);
        MapReportText.Text = $"已刪除地圖「{map.Name}」。";
        ReloadMaps();
    }

    private void OnMapPinKindChanged(object sender, SelectionChangedEventArgs e)
        => ReloadMapPinChannelCombo();

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
            MapReportText.Text = "請先選擇地圖。";
            return;
        }

        if (!double.TryParse(MapScaleBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            scale < 0)
        {
            MapReportText.Text = "比例尺需為非負數字（0＝未標定）。";
            return;
        }

        try
        {
            _maps.SetMapScale(map.Id, scale);
            MapReportText.Text = scale > 0
                ? $"已設定地圖「{map.Name}」比例尺：{MapGeometry.ScaleLabel(scale)}。"
                : $"已清除地圖「{map.Name}」比例尺（未標定）。";
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
            MapReportText.Text = "請先選擇圖釘。";
            return;
        }

        if (!TryReadGeometry(out var angle, out var fovDeg, out var fovDepth))
        {
            return;
        }

        try
        {
            _maps.UpdateDeviceGeometry(row.Id, angle, fovDeg, fovDepth);
            MapReportText.Text = $"已更新幾何：{row.DeviceType} #{row.ChannelId} 角度 {angle:0.#}°／FOV {fovDeg:0.#}°／深度 {fovDepth:0.#} m（{MapGeometry.Bearing(angle)}）。";
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
            MapReportText.Text = "角度／FOV／深度需為數字。";
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
        if (e.OriginalSource is Ellipse)
        {
            return;
        }

        var map = SelectedMap;
        if (map is null)
        {
            MapReportText.Text = "請先選擇地圖。";
            return;
        }

        if (MapPinChannelCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not int channelId)
        {
            MapReportText.Text = "尚無可放置的頻道／IO 通道。";
            return;
        }

        var kind = (MapPinKindCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "camera";
        if (!TryReadGeometry(out var angle, out var fovDeg, out var fovDepth))
        {
            return;
        }

        var pos = e.GetPosition(MapPinCanvas);
        var x = Math.Clamp(pos.X / MapPinCanvas.Width, 0, 1);
        var y = Math.Clamp(pos.Y / MapPinCanvas.Height, 0, 1);

        try
        {
            _maps.AddDevice(map.Id, kind, channelId, x, y, angle, fovDeg, fovDepth);
            MapReportText.Text = $"已放置 {kind} #{channelId}（{x:0.00}, {y:0.00}）";
        }
        catch (Exception ex)
        {
            MapReportText.Text = $"放置失敗：{ex.Message}";
        }

        ReloadMapPins();
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
        MapReportText.Text = $"已更新圖釘位置（{pos.X / MapPinCanvas.Width:0.00}, {pos.Y / MapPinCanvas.Height:0.00}）。";
        ReloadMapPins();
    }

    private void OnMapDeletePinClicked(object sender, RoutedEventArgs e)
    {
        if (MapPinList.SelectedItem is not MapPinRow row)
        {
            MapReportText.Text = "請先選擇圖釘。";
            return;
        }

        _maps.DeleteDevice(row.Id);
        MapReportText.Text = $"已刪除圖釘 {row.DeviceType} #{row.ChannelId}。";
        ReloadMapPins();
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
            u.Enabled ? "啟用" : "停用",
            u.FailedLogins > 0 ? $"{u.FailedLogins}{(u.LockedUntil is { Length: > 0 } ? "（鎖）" : string.Empty)}" : "0",
            u.LastLogin is { Length: > 0 } ll
                ? SqliteStore.FromIso(ll).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "—")).ToList();

        UserToggleButton.IsEnabled = false;
        UserDeleteButton.IsEnabled = false;
        UserUnlockButton.IsEnabled = false;
    }

    private void OnAuthEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var enabled = AuthEnabledBox.IsChecked == true;
        _settings.Set("auth.enabled", enabled ? "1" : "0");
        UserReportText.Text = enabled ? "已啟用登入驗證（下次啟動生效）。" : "已停用登入驗證。";
    }

    private void OnAuthApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AuthThresholdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold) || threshold < 1 ||
            !int.TryParse(AuthMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
        {
            UserReportText.Text = "鎖定門檻與分鐘需為正整數。";
            return;
        }

        _settings.Set("auth.lockout.threshold", threshold.ToString(CultureInfo.InvariantCulture));
        _settings.Set("auth.lockout.minutes", minutes.ToString(CultureInfo.InvariantCulture));
        UserReportText.Text = $"已套用：連續失敗 {threshold} 次鎖定 {minutes} 分鐘。";
    }

    private void OnUserAddClicked(object sender, RoutedEventArgs e)
    {
        var name = UserAddNameBox.Text.Trim();
        var password = UserAddPasswordBox.Password;
        if (name.Length == 0 || password.Length == 0)
        {
            UserReportText.Text = "請輸入使用者名稱與密碼。";
            return;
        }

        var role = (UserAddRoleCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "viewer";
        try
        {
            _users.CreateUser(name, PasswordHasher.Hash(password), role);
            UserReportText.Text = $"已新增使用者「{name}」（{role}）。";
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            UserReportText.Text = $"使用者名稱「{name}」已存在。";
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
            UserReportText.Text = "請先選擇使用者。";
            return;
        }

        var user = _users.GetById(row.Id);
        if (user is null)
        {
            return;
        }

        _users.SetEnabled(row.Id, !user.Enabled);
        UserReportText.Text = $"使用者「{row.Username}」已{(user.Enabled ? "停用" : "啟用")}。";
        ReloadUsers();
    }

    private void OnUserUnlockClicked(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRow row)
        {
            UserReportText.Text = "請先選擇使用者。";
            return;
        }

        _users.ClearLock(row.Id);
        UserReportText.Text = $"使用者「{row.Username}」已解除鎖定。";
        ReloadUsers();
    }

    private void OnUserDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRow row)
        {
            UserReportText.Text = "請先選擇使用者。";
            return;
        }

        _users.DeleteUser(row.Id);
        UserReportText.Text = $"已刪除使用者「{row.Username}」。";
        ReloadUsers();
    }
}