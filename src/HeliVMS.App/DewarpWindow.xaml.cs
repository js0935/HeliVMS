using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 魚眼矯正窗（§14.7 #2）：選範圍與投影參數 → ffmpeg v360 重新編碼輸出校正版 MP4（SHA-256）。
/// </summary>
public partial class DewarpWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;

    public DewarpWindow(SqliteStore store, string dataRoot,
        int? channelId = null, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();

        InputCombo.ItemsSource = new[] { "fisheye", "dfisheye", "equirect" };
        OutputCombo.ItemsSource = new[] { "flat", "equirect", "c3x2" };
        InputCombo.SelectedIndex = 0;
        OutputCombo.SelectedIndex = 0;

        var channels = new ChannelRepository(store).List().ToList();
        ChannelCombo.ItemsSource = channels;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        var segRepo = new SegmentRepository(store);
        var index = channelId.HasValue
            ? channels.FindIndex(c => c.Id == channelId.Value)
            : channels.FindIndex(c => segRepo.ListFinal(c.Id).Count > 0);
        ChannelCombo.SelectedIndex = index >= 0 ? index : (channels.Count > 0 ? 0 : -1);

        var defStart = fromUtc;
        var defEnd = toUtc;
        if (!defStart.HasValue && ChannelCombo.SelectedItem is ChannelInfo sel)
        {
            var finals = segRepo.ListFinal(sel.Id);
            if (finals.Count > 0)
            {
                defStart = finals[0].StartUtc;
                defEnd = finals[^1].EndUtc ?? finals[^1].StartUtc.AddMinutes(1);
            }
        }

        if (defStart.HasValue)
        {
            StartDate.SelectedDate = defStart.Value.ToLocalTime().Date;
            StartTime.Text = defStart.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            StartDate.SelectedDate = DateTime.Now.Date;
        }

        if (defEnd.HasValue)
        {
            EndDate.SelectedDate = defEnd.Value.ToLocalTime().Date;
            EndTime.Text = defEnd.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            EndDate.SelectedDate = DateTime.Now.Date;
        }
    }

    private void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            DewarpStatusText.Text = "請先選擇頻道。";
            return;
        }

        if (!TryResolveRange(out var startUtc, out var endUtc))
        {
            return;
        }

        var segments = new SegmentRepository(_store)
            .ListByRange(ch.Id, "main", startUtc, endUtc)
            .OrderBy(s => s.StartUtc)
            .ToList();
        if (segments.Count == 0)
        {
            DewarpStatusText.Text = "所選範圍無錄影段落可供矯正。";
            return;
        }

        try
        {
            var settings = BuildSettings();
            var png = Path.Combine(Path.GetTempPath(), $"helivms-dewarp-preview-{Guid.NewGuid():N}.png");
            DewarpService.RenderPreview(segments[0].FilePath, settings, png);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(png);
            bmp.EndInit();
            PreviewImage.Source = bmp;

            DewarpStatusText.Text = $"預覽完成：{settings.Input} → {settings.Output}。";
        }
        catch (Exception ex)
        {
            DewarpStatusText.Text = $"預覽失敗：{ex.Message}";
        }
    }

    private async void OnDewarpClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            DewarpStatusText.Text = "請先選擇頻道。";
            return;
        }

        if (!TryResolveRange(out var startUtc, out var endUtc))
        {
            return;
        }

        DewarpSettings settings;
        try
        {
            settings = BuildSettings();
        }
        catch (Exception ex)
        {
            DewarpStatusText.Text = $"參數無效：{ex.Message}";
            return;
        }

        var outputDir = Path.Combine(_dataRoot, "dewarped");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            $"dewarp-ch{ch.Id}-{startUtc:yyyyMMddHHmmss}-{endUtc:yyyyMMddHHmmss}.mp4");

        DewarpButton.IsEnabled = false;
        DewarpProgress.Value = 0;
        try
        {
            var service = new DewarpService(_store);
            var progress = new Progress<ExportProgress>(p =>
            {
                DewarpProgress.Value = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                {
                    DewarpStatusText.Text = p.Status;
                }
            });

            var result = await service.DewarpAsync(
                new DewarpRequest(ch.Id, startUtc, endUtc, outputPath, settings),
                progress);

            DewarpStatusText.Text = $"矯正完成：{Path.GetFileName(result.OutputPath)}" +
                                    $"（{FormatBytes(result.FileSizeBytes)}、{result.DurationSeconds:0.#} 秒）" +
                                    (result.Sha256 is null ? string.Empty : $"\nSHA-256：{result.Sha256}");
        }
        catch (Exception ex)
        {
            DewarpStatusText.Text = $"矯正失敗：{ex.Message}";
        }
        finally
        {
            DewarpButton.IsEnabled = true;
        }
    }

    private bool TryResolveRange(out DateTime startUtc, out DateTime endUtc)
    {
        startUtc = default;
        endUtc = default;

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            DewarpStatusText.Text = "請選擇起迄日期。";
            return false;
        }

        if (!TimeSpan.TryParse(StartTime.Text, CultureInfo.InvariantCulture, out var startTime))
        {
            startTime = TimeSpan.Zero;
        }

        if (!TimeSpan.TryParse(EndTime.Text, CultureInfo.InvariantCulture, out var endTime))
        {
            endTime = new TimeSpan(23, 59, 59);
        }

        startUtc = StartDate.SelectedDate.Value.Add(startTime).ToUniversalTime();
        endUtc = EndDate.SelectedDate.Value.Add(endTime).ToUniversalTime();
        if (endUtc <= startUtc)
        {
            DewarpStatusText.Text = "結束時間必須大於開始時間。";
            return false;
        }

        return true;
    }

    private DewarpSettings BuildSettings()
    {
        var input = InputCombo.SelectedIndex switch
        {
            1 => DewarpProjection.DualFisheye,
            2 => DewarpProjection.Equirect,
            _ => DewarpProjection.Fisheye,
        };
        var output = OutputCombo.SelectedIndex switch
        {
            1 => DewarpProjection.Equirect,
            2 => DewarpProjection.Cubemap3x2,
            _ => DewarpProjection.Flat,
        };

        return new DewarpSettings
        {
            Input = input,
            Output = output,
            InputHFov = ParseDouble(InputHFov.Text, 180),
            InputVFov = ParseDouble(InputVFov.Text, 180),
            HFov = ParseDouble(HFov.Text, 90),
            VFov = ParseDouble(VFov.Text, 90),
            Yaw = ParseDouble(Yaw.Text, 0),
            Pitch = ParseDouble(Pitch.Text, 0),
            Roll = ParseDouble(Roll.Text, 0),
            Width = ParseInt(WidthBox.Text, 1280),
            Height = ParseInt(HeightBox.Text, 720),
        };
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";
}
