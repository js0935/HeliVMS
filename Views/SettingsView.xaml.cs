using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Serilog;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Views;

public partial class SettingsView : UserControl {
    private readonly IEventService _eventService;
    private readonly ISettingsService _settingsService;
    private readonly ICameraService _cameraService;
    private readonly IBrandConfigService _brandConfigService;
    private string _keyword = "";
    private bool _loaded;
    private int _currentTab;

    public SettingsView() {
        InitializeComponent();
        _eventService = App.Services.GetRequiredService<IEventService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _brandConfigService = App.Services.GetRequiredService<IBrandConfigService>();

        Loaded += (_, _) => {
            _loaded = true;
            PopulateCategoryFilter();
            RefreshLog();
            CheckFfmpegStatus();
        };
    }

    private void TabButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) {
            ShowTab(index);
        }
    }

    private void ShowTab(int index) {
        if (index == _currentTab) return;
        _currentTab = index;

        TabEventLog.BorderBrush = index == 0
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabEventLog.Foreground = index == 0
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabFfmpeg.BorderBrush = index == 1
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabFfmpeg.Foreground = index == 1
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabFont.BorderBrush = index == 2
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabFont.Foreground = index == 2
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabRetention.BorderBrush = index == 3
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabRetention.Foreground = index == 3
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabUser.BorderBrush = index == 4
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabUser.Foreground = index == 4
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabDebug.BorderBrush = index == 5
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabDebug.Foreground = index == 5
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        TabMediaMTX.BorderBrush = index == 6
            ? TryFindResource("PrimaryBrush") as Brush ?? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.Transparent;
        TabMediaMTX.Foreground = index == 6
            ? TryFindResource("TextBrush") as Brush ?? System.Windows.Media.Brushes.White
            : TryFindResource("SecondaryTextBrush") as Brush ?? System.Windows.Media.Brushes.Gray;

        EventLogPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        FfmpegPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        FontPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        RetentionPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        UserPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        DebugPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        MediaMTXPanel.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 1) {
            CheckFfmpegStatus();
        } else if (index == 2) {
            LoadFontSettings();
        } else if (index == 3) {
            LoadRetentionSettings();
        } else if (index == 5) {
            LoadDebugSettings();
        } else if (index == 6) {
            RefreshMediaMTXStatus();
        }
    }

    private void PopulateCategoryFilter() {
        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(new ComboBoxItem { Content = "全部", Tag = "" });

        var categories = new[]
        {
            EventCategories.Operation,
            EventCategories.Setting,
            EventCategories.Connection,
            EventCategories.Alarm,
            EventCategories.Playback,
            EventCategories.Create,
            EventCategories.Update,
            EventCategories.Delete,
            EventCategories.Security,
            EventCategories.System,
            EventCategories.Debug,
        };

        foreach (var cat in categories) {
            CategoryFilter.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
        }
    }

    private void RefreshLog() {
        if (!_loaded) return;
        var category = CategoryFilter.SelectedItem is ComboBoxItem c ? c.Tag?.ToString() : null;
        var severity = SeverityFilter.SelectedItem is ComboBoxItem s ? s.Tag?.ToString() : null;

        if (string.IsNullOrEmpty(category)) category = null;
        if (string.IsNullOrEmpty(severity)) severity = null;

        var keyword = string.IsNullOrWhiteSpace(_keyword) ? null : _keyword;

        var events = _eventService.QueryEvents(category, severity, keyword, 500);
        EventLogGrid.ItemsSource = events;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) {
        RefreshLog();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        _keyword = SearchBox.Text.Trim();
        RefreshLog();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) {
        RefreshLog();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) {
        if (MessageBox.Show("確定清除所有事件日誌？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
            _eventService.ClearEvents();
            RefreshLog();
        }
    }

    private void CheckFfmpegStatus() {
        try {
            var version = FFmpegBinariesHelper.GetVersion();
            FfmpegVersionText.Text = version ?? "未安裝";

            var ffmpegDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");
            if (Directory.Exists(ffmpegDir)) {
                FfmpegDllPathText.Text = ffmpegDir;
                var dllCount = Directory.GetFiles(ffmpegDir, "*.dll").Length;
                var exeExists = File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe"));
                FfmpegDllCountText.Text = $"{dllCount} 個 DLL" + (exeExists ? " + ffmpeg.exe" : "");
            } else {
                FfmpegDllPathText.Text = "尚未下載";
                FfmpegDllCountText.Text = "0";
            }
        } catch (Exception ex) {
            FfmpegVersionText.Text = "檢查失敗";
            Log.Debug("[HeliVMS] FFmpeg status check error: {Msg}", ex.Message);
        }
    }

    private async void UpdateFfmpeg_Click(object sender, RoutedEventArgs e) {
        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "download-ffmpeg.ps1");
        if (!File.Exists(scriptPath)) {
            MessageBox.Show("找不到 FFmpeg 下載腳本。請手動前往 GitHub 下載。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            "將從 BtbN/FFmpeg-Builds (GitHub) 下載最新 FFmpeg 共享函式庫。\n" +
            "下載約需數分鐘，完成後請重新啟動應用程式。\n\n" +
            "是否繼續？", "更新 FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try {
            UpdateFfmpegBtn.IsEnabled = false;
            UpdateFfmpegBtn.Content = "下載中...";

            var psi = new ProcessStartInfo {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) {
                MessageBox.Show("無法啟動下載程序。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Read stdout/stderr asynchronously, non-blocking UI
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var exitTask = proc.WaitForExitAsync();
            var timeoutTask = Task.Delay(300_000);
            var done = await Task.WhenAny(exitTask, timeoutTask);

            if (done == timeoutTask) {
                proc.Kill();
                MessageBox.Show("下載逾時（超過 5 分鐘），請稍後再試。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (proc.ExitCode == 0) {
                MessageBox.Show("FFmpeg 下載完成！請重新啟動應用程式以套用更新。",
                                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                CheckFfmpegStatus();
            } else {
                MessageBox.Show($"下載失敗 (ExitCode={proc.ExitCode})。\n{error}",
                                "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        } catch (Exception ex) {
            MessageBox.Show($"下載時發生錯誤：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            UpdateFfmpegBtn.IsEnabled = true;
            UpdateFfmpegBtn.Content = "下載／更新 FFmpeg";
        }
    }

    private void OpenFfmpegGitHub_Click(object sender, RoutedEventArgs e) {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = "https://github.com/BtbN/FFmpeg-Builds/releases",
                UseShellExecute = true
            });
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Open URL error: {Msg}", ex.Message);
        }
    }

    private void BrowseFfmpegPath_Click(object sender, RoutedEventArgs e) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "選擇 FFmpeg 壓縮檔（.zip）或貼上資料夾路徑",
            Filter = "FFmpeg 壓縮檔 (*.zip)|*.zip|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true) {
            FfmpegManualPath.Text = dialog.FileName;
        }
    }

    private async void ImportFfmpeg_Click(object sender, RoutedEventArgs e) {
        var path = FfmpegManualPath.Text.Trim();
        if (string.IsNullOrEmpty(path)) {
            MessageBox.Show("請先輸入或瀏覽選擇 FFmpeg 資料夾／壓縮檔路徑。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportFfmpegBtn.IsEnabled = false;
        FfmpegImportStatus.Text = "驗證中...";
        FfmpegImportStatus.Foreground = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        try {
            await Task.Run(() => ValidateAndImportFfmpeg(path));
        } catch (Exception ex) {
            FfmpegImportStatus.Text = $"✗ {ex.Message}";
            FfmpegImportStatus.Foreground = Brushes.OrangeRed;
            ImportFfmpegBtn.IsEnabled = true;
            return;
        }

        FfmpegImportStatus.Text = "✓ 匯入完成，請重新啟動應用程式";
        FfmpegImportStatus.Foreground = Brushes.LimeGreen;
        ImportFfmpegBtn.IsEnabled = true;
        CheckFfmpegStatus();
    }

    private static void ValidateAndImportFfmpeg(string sourcePath) {
        string? extractDir = null;
        try {
            var dllDir = FindDllDirectory(sourcePath);
            if (dllDir is null && File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                extractDir = Path.Combine(Path.GetTempPath(), "HeliVMS_FFmpeg_" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(sourcePath, extractDir);
                dllDir = FindDllDirectory(extractDir);
            }
            if (dllDir is null) {
                throw new FileNotFoundException(
                    Directory.Exists(sourcePath) || (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        ? "指定資料夾或壓縮檔內找不到 FFmpeg DLL（avformat-*.dll），請確認下載的是 BtbN/FFmpeg-Builds 的 shared 版本。"
                        : "指定的路徑不存在。請確認資料夾或 .zip 檔路徑是否正確。");
            }

            var requiredPrefixes = new[] { "avformat-", "avcodec-", "avutil-", "swscale-", "swresample-" };
            var missing = new List<string>(requiredPrefixes.Length);
            foreach (var prefix in requiredPrefixes) {
                if (Directory.GetFiles(dllDir, prefix + "*.dll").Length == 0) {
                    missing.Add(prefix + "*.dll");
                }
            }
            if (missing.Count > 0) {
                throw new FileNotFoundException($"缺少必要 DLL：{string.Join("、", missing)}。請下載 shared 版本（非 dev 版）。");
            }

            var targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");
            if (!Directory.Exists(targetDir)) {
                Directory.CreateDirectory(targetDir);
            }

            foreach (var f in Directory.GetFiles(dllDir, "*.dll")) {
                File.Copy(f, Path.Combine(targetDir, Path.GetFileName(f)), overwrite: true);
            }

            var exePath = Path.Combine(dllDir, "ffmpeg.exe");
            if (File.Exists(exePath)) {
                File.Copy(exePath, Path.Combine(targetDir, "ffmpeg.exe"), overwrite: true);
            }
        } finally {
            if (extractDir is not null && Directory.Exists(extractDir)) {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }
    }

    private static string? FindDllDirectory(string rootDir) {
        if (Directory.GetFiles(rootDir, "avformat-*.dll").Length > 0) {
            return rootDir;
        }

        var binDir = Path.Combine(rootDir, "bin");
        if (Directory.Exists(binDir) && Directory.GetFiles(binDir, "avformat-*.dll").Length > 0) {
            return binDir;
        }

        foreach (var subDir in Directory.GetDirectories(rootDir)) {
            if (Directory.GetFiles(subDir, "avformat-*.dll").Length > 0) {
                return subDir;
            }
            var subBin = Path.Combine(subDir, "bin");
            if (Directory.Exists(subBin) && Directory.GetFiles(subBin, "avformat-*.dll").Length > 0) {
                return subBin;
            }
        }
        return null;
    }

    #region Font settings
    private void LoadFontSettings() {
        FontSizeSlider.Value = _settingsService.Settings.OverlayFontSize;
        EnableCameraNameOverlayCheckBox.IsChecked = _settingsService.Settings.EnableCameraNameOverlay;
        EnableTimestampOverlayCheckBox.IsChecked = _settingsService.Settings.EnableTimestampOverlay;
        UpdateFontPreview();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (!_loaded) return;
        UpdateFontPreview();
    }

    private void UpdateFontPreview() {
        var size = (int)FontSizeSlider.Value;
        FontSizeValue.Text = size.ToString();
        FontPreviewText.FontSize = size;
    }

    private void SaveFontSettings_Click(object sender, RoutedEventArgs e) {
        _settingsService.Settings.OverlayFontSize = (int)FontSizeSlider.Value;
        _settingsService.Settings.EnableCameraNameOverlay = EnableCameraNameOverlayCheckBox.IsChecked ?? true;
        _settingsService.Settings.EnableTimestampOverlay = EnableTimestampOverlayCheckBox.IsChecked ?? true;
        _settingsService.Save();
        MessageBox.Show("字型設定已儲存。", "設定", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyOverlayAndRestart_Click(object sender, RoutedEventArgs e) {
        _settingsService.Settings.EnableCameraNameOverlay = EnableCameraNameOverlayCheckBox.IsChecked ?? true;
        _settingsService.Settings.EnableTimestampOverlay = EnableTimestampOverlayCheckBox.IsChecked ?? true;
        _settingsService.Save();

        var result = MessageBox.Show(
            "已儲存錄影崁入設定，重新啟動程式後才會生效。\n是否立即重新啟動？",
            "套用設定",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes) {
            var exePath = Environment.ProcessPath;
            if (exePath is not null) {
                Process.Start(exePath);
            }
            Application.Current.Shutdown();
        }
    }
    #endregion

    #region Retention settings
    private void LoadRetentionSettings() {
        var s = _settingsService.Settings;
        EnableAutoPurgeCheckBox.IsChecked = s.EnableAutoPurge;
        RetentionDaysSlider.Value = s.RetentionDays;
        MaxStorageGBTextBox.Text = s.MaxStorageGB.ToString();
        UpdateCurrentStorageInfo();
    }

    private void RetentionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (!_loaded) return;
        var days = (int)RetentionDaysSlider.Value;
        RetentionDaysValue.Text = days.ToString();
        _settingsService.Settings.RetentionDays = days;
        _settingsService.Save();
    }

    private void RetentionSettingChanged(object sender, RoutedEventArgs e) {
        if (!_loaded) return;
        var s = _settingsService.Settings;
        s.EnableAutoPurge = EnableAutoPurgeCheckBox.IsChecked ?? true;
        if (int.TryParse(MaxStorageGBTextBox.Text, out var gb) && gb >= 0) {
            s.MaxStorageGB = gb;
        }
        _settingsService.Save();
    }

    private async void PurgeNow_Click(object sender, RoutedEventArgs e) {
        PurgeNowBtn.IsEnabled = false;
        PurgeResultText.Text = "清理中...";
        PurgeResultText.Foreground = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

        try {
            var indexService = App.Services.GetRequiredService<IVideoIndexService>();
            var recordingService = App.Services.GetRequiredService<IRecordingService>();
            var settings = _settingsService.Settings;

            var (deleted, freed) = await Task.Run(() =>
                indexService.PurgeByRetentionPolicyAsync(
                    settings.RetentionDays,
                    settings.MaxStorageGB,
                    recordingService.GetBasePath()));

            if (deleted > 0) {
                PurgeResultText.Text = $"✓ 已清除 {deleted} 個片段，釋放 {freed / (1024.0 * 1024.0):F1} MB";
                PurgeResultText.Foreground = Brushes.LimeGreen;
            } else {
                PurgeResultText.Text = "✓ 無需清理";
                PurgeResultText.Foreground = Brushes.LimeGreen;
            }

            UpdateCurrentStorageInfo();
        } catch (Exception ex) {
            PurgeResultText.Text = $"✗ {ex.Message}";
            PurgeResultText.Foreground = Brushes.OrangeRed;
        } finally {
            PurgeNowBtn.IsEnabled = true;
        }
    }

    private void UpdateCurrentStorageInfo() {
        try {
            var recordingService = App.Services.GetRequiredService<IRecordingService>();
            var basePath = recordingService.GetBasePath();
            if (!Directory.Exists(basePath)) {
                CurrentStorageText.Text = "";
                return;
            }

            var root = Path.GetPathRoot(basePath);
            if (root is not null) {
                var drive = new DriveInfo(root);
                if (drive.IsReady) {
                    var usedGB = (drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 * 1024.0 * 1024.0);
                    var totalGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                    var pct = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;

                    if (_settingsService.Settings.MaxStorageGB > 0) {
                        CurrentStorageText.Text = $"目前磁碟使用：{usedGB:F1} / {totalGB:F0} GB ({pct:F0}%) ｜ 錄影硬上限：{_settingsService.Settings.MaxStorageGB} GB";
                    } else {
                        CurrentStorageText.Text = $"目前磁碟使用：{usedGB:F1} / {totalGB:F0} GB ({pct:F0}%)";
                    }
                    return;
                }
            }
            CurrentStorageText.Text = "";
        } catch {
            CurrentStorageText.Text = "";
        }
    }
    #endregion

    #region MediaMTX status
    private MediaMTXService? _mtxService;

    private MediaMTXService? GetMediaMTX() {
        if (_mtxService is null) {
            try { _mtxService = App.Services.GetRequiredService<MediaMTXService>(); } catch { }
        }
        return _mtxService;
    }

    private void RefreshMediaMTXStatus() {
        var mtx = GetMediaMTX();
        if (mtx is null) {
            MtxStatusDot.Fill = Brushes.Gray;
            MtxStatusText.Text = "服務未註冊";
            MtxUptimeText.Text = "—";
            return;
        }

        MtxConfigPathText.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaMTX", "mediamtx.yml");
        MtxPortText.Text = "8554";

        if (mtx.IsRunning) {
            MtxStatusDot.Fill = Brushes.LimeGreen;
            MtxStatusText.Text = "執行中";
        } else {
            MtxStatusDot.Fill = Brushes.OrangeRed;
            MtxStatusText.Text = "已停止";
            MtxUptimeText.Text = "—";
        }
    }

    private void MtxRestart_Click(object sender, RoutedEventArgs e) {
        var mtx = GetMediaMTX();
        if (mtx is null) return;

        MtxRestartBtn.IsEnabled = false;
        MtxStatusText.Text = "重新啟動中...";
        MtxStatusDot.Fill = Brushes.Orange;

        Task.Run(() => {
            mtx.Restart();
            _ = Dispatcher.BeginInvoke(() => {
                RefreshMediaMTXStatus();
                MtxRestartBtn.IsEnabled = true;
            });
        });
    }

    private void MtxRefresh_Click(object sender, RoutedEventArgs e) {
        RefreshMediaMTXStatus();
    }
    #endregion

    #region Debug settings
    private void LoadDebugSettings() {
        ShowDragDebugCheckBox.IsChecked = _settingsService.Settings.ShowDragDebugPanel;
        UseAsyncRtspRecordingCheckBox.IsChecked = _settingsService.Settings.UseAsyncRtspRecording;
        UseAsyncRtspLiveViewCheckBox.IsChecked = _settingsService.Settings.UseAsyncRtspLiveView;

        SysVersionText.Text = "V1.0.0";
        SysOsText.Text = RuntimeInformation.OSDescription;
        SysDotnetText.Text = RuntimeInformation.FrameworkDescription;
        SysCameraCountText.Text = $"{_cameraService.GetAllCameras().Count} 台";
        SysSettingsPathText.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

        BrandCountText.Text = $"{_brandConfigService.BrandCount} 個品牌 ／ {_brandConfigService.TotalModelCount} 個型號";
    }

    private void ShowDragDebugCheckBox_Changed(object sender, RoutedEventArgs e) {
        _settingsService.Settings.ShowDragDebugPanel = ShowDragDebugCheckBox.IsChecked ?? false;
        _settingsService.Save();
    }

    private void UseAsyncRtspRecording_Changed(object sender, RoutedEventArgs e) {
        _settingsService.Settings.UseAsyncRtspRecording = UseAsyncRtspRecordingCheckBox.IsChecked ?? false;
        _settingsService.Save();
    }

    private void UseAsyncRtspLiveView_Changed(object sender, RoutedEventArgs e) {
        _settingsService.Settings.UseAsyncRtspLiveView = UseAsyncRtspLiveViewCheckBox.IsChecked ?? false;
        _settingsService.Save();
    }

    private async void UpdateBrandConfig_Click(object sender, RoutedEventArgs e) {
        try {
            UpdateBrandConfigBtn.IsEnabled = false;
            BrandUpdateStatusText.Text = "正在更新品牌資料...";
            BrandUpdateStatusText.Foreground = TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray;

            var (added, updatedModels, errors) = await _brandConfigService.UpdateFromStrixCamDBAsync();

            BrandCountText.Text = $"{_brandConfigService.BrandCount} 個品牌 ／ {_brandConfigService.TotalModelCount} 個型號";

            if (errors.Length > 0) {
                BrandUpdateStatusText.Text = $"✗ {errors[0]}";
                BrandUpdateStatusText.Foreground = Brushes.OrangeRed;
            } else if (added > 0 || updatedModels > 0) {
                var parts = new List<string>(2);
                if (added > 0) parts.Add($"新增 {added} 個品牌");
                if (updatedModels > 0) parts.Add($"更新 {updatedModels} 個型號資料");
                BrandUpdateStatusText.Text = $"✓ 更新完成，{string.Join("，", parts)}。共 {_brandConfigService.BrandCount} 個品牌／{_brandConfigService.TotalModelCount} 個型號";
                BrandUpdateStatusText.Foreground = Brushes.LimeGreen;
            } else {
                BrandUpdateStatusText.Text = $"✓ 已是最新（{_brandConfigService.BrandCount} 個品牌／{_brandConfigService.TotalModelCount} 個型號）";
                BrandUpdateStatusText.Foreground = Brushes.LimeGreen;
            }

            UpdateBrandConfigBtn.IsEnabled = true;
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] BrandConfig update error: {Msg}", ex.Message);
            BrandUpdateStatusText.Text = $"✗ 更新失敗: {ex.Message}";
            BrandUpdateStatusText.Foreground = Brushes.OrangeRed;
            UpdateBrandConfigBtn.IsEnabled = true;
        }
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) {
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        try {
            if (Directory.Exists(dataDir)) {
                Process.Start(new ProcessStartInfo { FileName = dataDir, UseShellExecute = true });
            } else {
                MessageBox.Show("資料夾不存在：" + dataDir, "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Open folder error: {Msg}", ex.Message);
        }
    }

    private void OpenCameraDataFile_Click(object sender, RoutedEventArgs e) {
        var camFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "cameras.json");
        try {
            if (File.Exists(camFile)) {
                Process.Start(new ProcessStartInfo { FileName = camFile, UseShellExecute = true });
            } else {
                MessageBox.Show("找不到攝影機資料檔：" + camFile, "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Open file error: {Msg}", ex.Message);
        }
    }
    #endregion
}
