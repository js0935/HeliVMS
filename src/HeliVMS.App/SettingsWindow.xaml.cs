using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly SettingsRepository _settings;
    private readonly AlertRuleRepository _rules;
    private readonly IoRepository _io;
    private readonly IoMonitorHost? _ioHost;

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

        InitializeComponent();

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
}