using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 邊緣補抓視窗（M94，§14.7 #10）：設定設備 SD 串流網址、建立補抓工作、執行到期工作並回灌為區段。
/// 先前 <see cref="EdgeBackfillJobRepository"/>／<see cref="EdgeBackfillExecutor"/> 全無呼叫端，
/// 且 <see cref="DeviceRecord"/> 沒有 SD 網址欄位，補抓 runner 根本無從解析來源。
/// </summary>
public partial class EdgeBackfillWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly DeviceRepository _devices;
    private readonly ChannelRepository _channels;
    private readonly EdgeBackfillJobRepository _jobs;

    public EdgeBackfillWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        _devices = new DeviceRepository(store, new AuditLogRepository(store));
        _channels = new ChannelRepository(store);
        _jobs = new EdgeBackfillJobRepository(store);
        InitializeComponent();

        EdgeStartDate.SelectedDate = DateTime.Today;
        EdgeEndDate.SelectedDate = DateTime.Today;
        RefreshDevices();
        RefreshJobs();
    }

    private void RefreshDevices(int? selectId = null)
    {
        var devices = _devices.List();
        EdgeDeviceCombo.ItemsSource = devices;
        EdgeDeviceCombo.DisplayMemberPath = nameof(DeviceRecord.Name);

        var index = selectId is { } sid
            ? devices.ToList().FindIndex(d => d.Id == sid)
            : (devices.Count > 0 ? 0 : -1);
        EdgeDeviceCombo.SelectedIndex = index >= 0 ? index : (devices.Count > 0 ? 0 : -1);
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EdgeDeviceCombo.SelectedItem is not DeviceRecord device)
        {
            EdgeChannelCombo.ItemsSource = null;
            EdgeSdUrlBox.Text = string.Empty;
            return;
        }

        var channels = _channels.List().Where(c => c.DeviceId == device.Id).ToList();
        EdgeChannelCombo.ItemsSource = channels;
        EdgeChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        EdgeChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;
        EdgeSdUrlBox.Text = device.SdUrl ?? string.Empty;
    }

    private void OnSaveSdUrlClicked(object sender, RoutedEventArgs e)
    {
        if (EdgeDeviceCombo.SelectedItem is not DeviceRecord device)
        {
            EdgeStatusText.Text = "請先選擇設備。";
            return;
        }

        _devices.SetSdUrl(device.Id, EdgeSdUrlBox.Text.Trim());
        EdgeStatusText.Text = $"已儲存設備「{device.Name}」的 SD 串流網址。";
        RefreshDevices(device.Id);
    }

    private bool TryRange(out DateTime startUtc, out DateTime endUtc)
    {
        startUtc = default;
        endUtc = default;

        if (EdgeStartDate.SelectedDate is not { } startDate || EdgeEndDate.SelectedDate is not { } endDate)
        {
            EdgeStatusText.Text = "請選擇起迄日期。";
            return false;
        }

        if (!TimeSpan.TryParse(EdgeStartTime.Text, CultureInfo.InvariantCulture, out var startTime))
        {
            startTime = TimeSpan.Zero;
        }

        if (!TimeSpan.TryParse(EdgeEndTime.Text, CultureInfo.InvariantCulture, out var endTime))
        {
            endTime = TimeSpan.Zero;
        }

        startUtc = startDate.Add(startTime).ToUniversalTime();
        endUtc = endDate.Add(endTime).ToUniversalTime();
        if (endUtc <= startUtc)
        {
            EdgeStatusText.Text = "結束時間必須大於開始時間。";
            return false;
        }

        return true;
    }

    private void OnCreateJobClicked(object sender, RoutedEventArgs e)
    {
        if (EdgeDeviceCombo.SelectedItem is not DeviceRecord device ||
            EdgeChannelCombo.SelectedItem is not ChannelInfo channel)
        {
            EdgeStatusText.Text = "請先選擇設備與頻道。";
            return;
        }

        if (!TryRange(out var startUtc, out var endUtc))
        {
            return;
        }

        var id = _jobs.Create(device.Id, channel.Id, startUtc, endUtc);
        EdgeStatusText.Text = id < 0
            ? "此時段與既有補抓重疊，已略過。"
            : $"已建立補抓工作 #{id}。";
        RefreshJobs();
    }

    private EdgePullTarget Resolve(EdgeBackfillJob job)
    {
        var source = _devices.Get(job.DeviceId)?.SdUrl;
        if (string.IsNullOrWhiteSpace(source))
        {
            // 不可在這裡丟例外：resolver 在 EdgeFfmpegBackfillRunner 的 try 之外被呼叫，
            // 例外會直接炸掉 executor 迴圈並讓工作卡在 Downloading。改用必定失敗的位址，
            // 讓 ffmpeg 回報非零離開碼，走正常的失敗退避。
            source = "rtsp://127.0.0.1:1/no-sd-url";
        }

        var dir = Path.Combine(_dataRoot, "backfill", $"device-{job.DeviceId}", $"channel-{job.ChannelId}");
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, $"{job.StartUtc:yyyyMMddHHmmss}-{job.EndUtc:yyyyMMddHHmmss}.mp4");
        return new EdgePullTarget(source, destination);
    }

    private async void OnRunDueClicked(object sender, RoutedEventArgs e)
    {
        EdgeRunDueButton.IsEnabled = false;
        try
        {
            var inner = new EdgeFfmpegBackfillRunner(Resolve);
            var runner = new SegmentRegisteringEdgeBackfillRunner(inner, Resolve, new SegmentRepository(_store));
            var executor = new EdgeBackfillExecutor(_jobs, runner, maxConcurrent: 1, retryInterval: TimeSpan.FromMinutes(5));

            var run = await executor.ExecuteOnceAsync(DateTime.UtcNow);
            var ok = run.Items.Count(i => i.Success);
            var failed = run.Items.Count - ok;
            EdgeStatusText.Text = run.Items.Count == 0
                ? "目前沒有到期的補抓工作。"
                : $"執行完成：成功 {ok}、失敗 {failed}。";
            RefreshJobs();
        }
        catch (Exception ex)
        {
            EdgeStatusText.Text = "執行失敗：" + ex.Message;
        }
        finally
        {
            EdgeRunDueButton.IsEnabled = true;
        }
    }

    private void RefreshJobs()
    {
        var deviceNames = _devices.List().ToDictionary(d => d.Id, d => d.Name);
        var channelNames = _channels.List().ToDictionary(c => c.Id, c => c.Name);

        EdgeJobList.ItemsSource = _jobs.QueryAll()
            .Select(j => new
            {
                j.Id,
                Device = deviceNames.GetValueOrDefault(j.DeviceId, $"#{j.DeviceId}"),
                Channel = channelNames.GetValueOrDefault(j.ChannelId, $"#{j.ChannelId}"),
                Range = $"{j.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} → {j.EndUtc.ToLocalTime():HH:mm:ss}",
                Status = j.Status.ToString(),
                j.Attempts,
                Error = j.LastError ?? string.Empty,
            })
            .ToList();
    }
}
