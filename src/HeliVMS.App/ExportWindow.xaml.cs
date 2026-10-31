using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 匯出精靈（§8.5/§14）：選定範圍 → ffmpeg concat + re-encode → MP4（可選浮水印＋SHA-256）。
/// </summary>
public partial class ExportWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private IReadOnlyList<ChannelInfo> _channels = [];
    private CancellationTokenSource? _cts;
    private string? _lastOutputPath;

    public ExportWindow(SqliteStore store, string dataRoot, long? focusChannelId = null, DateTime? focusFromUtc = null, DateTime? focusToUtc = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();
        ApplyI18n();

        _channels = new ChannelRepository(store).List();
        ChannelCombo.ItemsSource = _channels;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        FocusChannel(focusChannelId);

        StartDate.SelectedDate = DateTime.Now.Date;
        EndDate.SelectedDate = DateTime.Now.Date;

        // 回放帶子「匯出所選區間」帶來的精確起止（§8.5）：預填日期＋時間欄，操作員確認後直接匯出。
        if (focusFromUtc is { } from && focusToUtc is { } to)
        {
            var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(from, TimeZoneInfo.Local);
            var toLocal = TimeZoneInfo.ConvertTimeFromUtc(to, TimeZoneInfo.Local);
            StartDate.SelectedDate = fromLocal.Date;
            StartTime.Text = fromLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndDate.SelectedDate = toLocal.Date;
            EndTime.Text = toLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        OutputFolderBox.Text = Path.Combine(_dataRoot, "exports");
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Export.Title");
        HeaderTitle.Text = Localizer.T("Export.Title");
        HeaderCloseButton.ToolTip = Localizer.T("Export.CloseTip");
        ExportFormHeader.Text = Localizer.T("Export.FormHeader");
        ExportStatusHeader.Text = Localizer.T("Export.StatusHeader");
        ExportChannelLabel.Text = Localizer.T("Export.Channel");
        ExportRangeLabel.Text = Localizer.T("Export.Range");
        ExportStartLabel.Text = Localizer.T("Export.StartLabel");
        ExportEndLabel.Text = Localizer.T("Export.EndLabel");
        ExportOutputFolderLabel.Text = Localizer.T("Export.OutputFolder");
        BrowseButton.Content = Localizer.T("Export.Browse");
        BrowseButton.ToolTip = Localizer.T("Export.BrowseTip");
        ExportWatermarkLabel.Text = Localizer.T("Export.Watermark");
        HashCheckBox.Content = Localizer.T("Export.Hash");
        ApplyRedactionCheckBox.Content = Localizer.T("Export.ApplyRedaction");
        BundleCheckBox.Content = Localizer.T("Export.Bundle");
        ExportBundlePasswordLabel.Text = Localizer.T("Export.BundlePassword");
        ExportBundleExpiryLabel.Text = Localizer.T("Export.BundleExpiry");
        ExportButton.Content = Localizer.T("Export.Export");
        ExportButton.ToolTip = Localizer.T("Export.ExportTip");
        OpenFolderButton.Content = Localizer.T("Export.OpenFolder");
        OpenFolderButton.ToolTip = Localizer.T("Export.OpenFolderTip");
        CloseButton.Content = Localizer.T("Export.Close");
        CloseButton.ToolTip = Localizer.T("Export.CloseTip");
    }

    /// <summary>重定向本窗到指定頻道（null 或找不到時回到首筆；供主視窗右鍵「此頻道」重用同窗）。</summary>
    public void FocusChannel(long? channelId)
    {
        for (var i = 0; i < _channels.Count; i++)
        {
            if (channelId is { } id && _channels[i].Id == id)
            {
                ChannelCombo.SelectedIndex = i;
                return;
            }
        }

        if (_channels.Count > 0)
        {
            ChannelCombo.SelectedIndex = 0;
        }
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localizer.T("Export.BrowseTitle"),
            InitialDirectory = OutputFolderBox.Text,
        };

        if (dlg.ShowDialog() == true)
        {
            OutputFolderBox.Text = dlg.FolderName;
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            StatusText.Text = Localizer.T("Export.PickChannel");
            return;
        }

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            StatusText.Text = Localizer.T("Export.PickDates");
            return;
        }

        if (!TimeSpan.TryParse(StartTime.Text, CultureInfo.InvariantCulture, out var startTime))
        {
            startTime = TimeSpan.Zero;
        }

        if (!TimeSpan.TryParse(EndTime.Text, CultureInfo.InvariantCulture, out var endTime))
        {
            endTime = new TimeSpan(23, 59, 59);
        }

        var startUtc = StartDate.SelectedDate.Value.Add(startTime).ToUniversalTime();
        var endUtc = EndDate.SelectedDate.Value.Add(endTime).ToUniversalTime();
        if (endUtc <= startUtc)
        {
            StatusText.Text = Localizer.T("Export.EndAfterStart");
            return;
        }

        var outputDir = OutputFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            StatusText.Text = Localizer.T("Export.PickOutput");
            return;
        }

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            $"export-ch{ch.Id}-{startUtc:yyyyMMddHHmmss}-{endUtc:yyyyMMddHHmmss}.mp4");

        var watermark = string.IsNullOrWhiteSpace(WatermarkBox.Text) ? null : WatermarkBox.Text.Trim();
        var generateHash = HashCheckBox.IsChecked == true;

        ExportButton.IsEnabled = false;
        _cts = new CancellationTokenSource();

        try
        {
            var service = new ExportService(_store);
            // §14.7 #4/#5：勾選時自動取回此頻道／範圍已登錄的遮蔽區域並套用（無登錄則等同不遮蔽）。
            var redactionRois = ApplyRedactionCheckBox.IsChecked == true
                ? service.LoadStoredRois(ch.Id, startUtc, endUtc)
                : null;
            var progress = new Progress<ExportProgress>(p =>
            {
                ExportProgress.Value = p.Percent;
                StatusText.Text = p.Status;
            });

            var result = await service.ExportAsync(
                new ExportRequest(ch.Id, startUtc, endUtc, outputPath, watermark, generateHash, redactionRois),
                progress,
                _cts.Token);

            ResultText.Text = string.Format(
                                  Localizer.T("Export.Success"),
                                  Path.GetFileName(result.OutputPath),
                                  FormatBytes(result.FileSizeBytes),
                                  result.DurationSeconds.ToString("0.#")) +
                              (result.Sha256Hash is not null
                                  ? "\n" + string.Format(Localizer.T("Export.ShaLine"), result.Sha256Hash)
                                  : string.Empty);
            _lastOutputPath = result.OutputPath;
            OpenFolderButton.IsEnabled = true;

            if (BundleCheckBox.IsChecked == true)
            {
                try
                {
                    var sourceFiles = new List<string> { result.OutputPath };
                    if (generateHash && File.Exists(outputPath + ".sha256"))
                    {
                        sourceFiles.Add(outputPath + ".sha256");
                    }

                    var manifest = EvidencePackager.BuildManifest(
                        $"HeliVMS-{DateTime.UtcNow:yyyyMMddHHmmss}",
                        sourceFiles);
                    var expires = ParseExpiryUtc(BundleExpiryBox.Text);
                    if (expires is not null)
                    {
                        manifest = manifest with { ExpiresUtc = expires };
                    }

                    var bundlePassword = BundlePasswordBox.Password;
                    var bundlePath = outputPath + ".evp";
                    var bundleSha = EvidencePackager.Create(
                        bundlePath,
                        manifest,
                        string.IsNullOrEmpty(bundlePassword) ? null : bundlePassword);

                    ResultText.Text += "\n" + string.Format(Localizer.T("Export.BundleLine"),
                        Path.GetFileName(bundlePath), bundleSha);
                    StatusText.Text = Localizer.T("Export.BundleOk");
                }
                catch (Exception bEx)
                {
                    StatusText.Text = string.Format(Localizer.T("Export.BundleFailed"), bEx.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Localizer.T("Export.Canceled");
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Localizer.T("Export.Failed"), ex.Message);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>無邊框最大化視窗沒有系統標題列：Esc 是鍵盤出口（關閉鈕之外）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>在檔案總管中顯示上次成功匯出的檔案。</summary>
    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOutputPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutputPath}\""));
        }
        catch
        {
            StatusText.Text = Localizer.T("Export.ExplorerFailed");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";

    private static DateTime? ParseExpiryUtc(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (!DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new FormatException(Localizer.T("Export.ExpiryFormat"));
        }

        return day.Date.AddDays(1).ToUniversalTime().AddSeconds(-1);
    }
}