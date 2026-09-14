using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Licensing;
using HeliVMS.Recording;
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
        ReloadUsage();
        ReloadLicense();
        ReloadLaunchAvailability();

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
        PageLicense.Visibility = visible == "授權" ? Visibility.Visible : Visibility.Collapsed;
        PageLaunch.Visibility = visible == "功能" ? Visibility.Visible : Visibility.Collapsed;
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
            CleanupReportText.Text = $"清理完成：移除 {report.DeletedSegments} 段，釋放 {FormatBytes(report.FreedBytes)}" +
                                     (report.PurgedTmp > 0 ? $"，清除 {report.PurgedTmp} 個暫存檔" : string.Empty);
        }
        catch (Exception ex)
        {
            CleanupReportText.Text = $"清理失敗：{ex.Message}";
        }

        ReloadUsage();
    }

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