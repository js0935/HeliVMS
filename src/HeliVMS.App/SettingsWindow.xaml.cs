using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
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

    /// <summary>頻道頁顯示列。</summary>
    private sealed record ChannelRow(int Id, string Name, string MainStreamUrl, string RecordingModeLabel, string MotionLabel);

    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly SettingsRepository _settings;

    public SettingsWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        _recordingsRoot = Path.Combine(dataRoot, "recordings");
        _snapshotsRoot = Path.Combine(dataRoot, "snapshots");
        _settings = new SettingsRepository(store);

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