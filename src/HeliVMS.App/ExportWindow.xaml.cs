using System.Globalization;
using System.IO;
using System.Windows;
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

    public ExportWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();

        _channels = new ChannelRepository(store).List();
        ChannelCombo.ItemsSource = _channels;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        if (_channels.Count > 0)
        {
            ChannelCombo.SelectedIndex = 0;
        }

        StartDate.SelectedDate = DateTime.Now.Date;
        EndDate.SelectedDate = DateTime.Now.Date;

        OutputFolderBox.Text = Path.Combine(_dataRoot, "exports");
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇匯出資料夾",
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
            StatusText.Text = "請先選擇頻道。";
            return;
        }

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            StatusText.Text = "請選擇起迄日期。";
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
            StatusText.Text = "結束時間必須大於開始時間。";
            return;
        }

        var outputDir = OutputFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            StatusText.Text = "請選擇輸出資料夾。";
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
            var progress = new Progress<ExportProgress>(p =>
            {
                ExportProgress.Value = p.Percent;
                StatusText.Text = p.Status;
            });

            var result = await service.ExportAsync(
                new ExportRequest(ch.Id, startUtc, endUtc, outputPath, watermark, generateHash),
                progress,
                _cts.Token);

            ResultText.Text = $"匯出成功：{Path.GetFileName(result.OutputPath)}" +
                              $"（{FormatBytes(result.FileSizeBytes)}、{result.DurationSeconds:0.#} 秒）" +
                              (result.Sha256Hash is not null
                                  ? $"\nSHA-256：{result.Sha256Hash}"
                                  : string.Empty);

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

                    ResultText.Text += $"\\n證據包：{Path.GetFileName(bundlePath)}（SHA-256：{bundleSha}）";
                    StatusText.Text = "匯出成功，證據包已建立。";
                }
                catch (Exception bEx)
                {
                    StatusText.Text = $"匯出成功，但證據包失敗：{bEx.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "匯出已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"匯出失敗：{ex.Message}";
        }
        finally
        {
            ExportButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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
            throw new FormatException("到期日格式須為 yyyy-MM-dd。");
        }

        return day.Date.AddDays(1).ToUniversalTime().AddSeconds(-1);
    }
}