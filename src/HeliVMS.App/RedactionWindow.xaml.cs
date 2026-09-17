using System.Globalization;
using System.IO;
using System.Windows;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 錄影遮蔽窗（§14.7 #5）：選範圍與遮蔽區域 → ffmpeg boxblur 重新編碼輸出遮蔽版 MP4（SHA-256）。
/// </summary>
public partial class RedactionWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly List<RedactionRoiRow> _rois = new();

    /// <summary>遮蔽區域清單顯示列。</summary>
    private sealed record RedactionRoiRow(int X, int Y, int Width, int Height, string Label)
    {
        public RedactionRoi ToRoi() => new(X, Y, Width, Height);
    }

    public RedactionWindow(SqliteStore store, string dataRoot,
        int? channelId = null, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();

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

    private void OnAddRoiClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RoiX.Text, out var x) || !int.TryParse(RoiY.Text, out var y) ||
            !int.TryParse(RoiW.Text, out var w) || !int.TryParse(RoiH.Text, out var h) ||
            w <= 0 || h <= 0)
        {
            RedactStatusText.Text = "遮蔽區域需為正整數（寬高大於 0）。";
            return;
        }

        _rois.Add(new RedactionRoiRow(x, y, w, h, $"區域 {_rois.Count + 1}"));
        RoiList.ItemsSource = _rois.ToList();
        RedactStatusText.Text = $"已加入遮蔽區域：({x}, {y}) {w}×{h}。";
    }

    private void OnRemoveRoiClicked(object sender, RoutedEventArgs e)
    {
        if (RoiList.SelectedItem is not RedactionRoiRow row)
        {
            RedactStatusText.Text = "請先於清單選取要移除的遮蔽區域。";
            return;
        }

        _rois.Remove(row);
        RoiList.ItemsSource = _rois.ToList();
        RedactStatusText.Text = $"已移除遮蔽區域 ({row.X}, {row.Y})。";
    }

    private async void OnRedactClicked(object sender, RoutedEventArgs e)
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            RedactStatusText.Text = "請先選擇頻道。";
            return;
        }

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            RedactStatusText.Text = "請選擇起迄日期。";
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
            RedactStatusText.Text = "結束時間必須大於開始時間。";
            return;
        }

        if (_rois.Count == 0)
        {
            RedactStatusText.Text = "請先加入至少一個遮蔽區域。";
            return;
        }

        var outputDir = Path.Combine(_dataRoot, "redacted");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir,
            $"redact-ch{ch.Id}-{startUtc:yyyyMMddHHmmss}-{endUtc:yyyyMMddHHmmss}.mp4");

        RedactButton.IsEnabled = false;
        RedactProgress.Value = 0;
        try
        {
            var service = new RedactionService(_store);
            var progress = new Progress<ExportProgress>(p =>
            {
                RedactProgress.Value = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                {
                    RedactStatusText.Text = p.Status;
                }
            });

            var result = await service.RedactAsync(
                new RedactionRequest(ch.Id, startUtc, endUtc, outputPath,
                    _rois.Select(r => r.ToRoi()).ToList()),
                progress);

            RedactStatusText.Text = $"遮蔽完成：{Path.GetFileName(result.OutputPath)}" +
                                    $"（{FormatBytes(result.FileSizeBytes)}、{result.DurationSeconds:0.#} 秒）" +
                                    (result.Sha256 is null ? string.Empty : $"\nSHA-256：{result.Sha256}");
        }
        catch (Exception ex)
        {
            RedactStatusText.Text = $"遮蔽失敗：{ex.Message}";
        }
        finally
        {
            RedactButton.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024d * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.#}GB"
        : $"{bytes / 1024d / 1024:0.#}MB";
}