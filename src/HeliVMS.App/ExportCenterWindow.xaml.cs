using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 匯出中心（M44，§14.3(2)）：批量新增匯出工作 → 佇列循序處理 → 歷史清單／狀態／
/// 完整性驗證（<see cref="ExportVerifier"/>）。
/// </summary>
public partial class ExportCenterWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly ExportJobRepository _jobs;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _running;

    public ExportCenterWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();

        _jobs = new ExportJobRepository(store);

        var channels = new ChannelRepository(store).List();
        ChannelList.ItemsSource = channels;
        ChannelList.DisplayMemberPath = nameof(ChannelInfo.Name);

        StartDate.SelectedDate = DateTime.Now.Date;
        EndDate.SelectedDate = DateTime.Now.Date;
        CenterOutputBox.Text = Path.Combine(dataRoot, "exports");

        _timer.Tick += (_, _) => Reload();
        _timer.Start();
        Reload();
    }

    private sealed record JobItem(string Display, ExportJobRecord Job);

    private void Reload()
    {
        try
        {
            var all = _jobs.List();
            JobList.ItemsSource = all.Select(j =>
            {
                var suffix = j.Status switch
                {
                    "done" when j.Sha256 is not null => $"  sha={j.Sha256[..8]}",
                    "failed" => $"  err={j.Error}",
                    _ => string.Empty,
                };
                return new JobItem(
                    $"#{j.Id} ch{j.ChannelId} {j.StartUtc:yyyy-MM-dd HH:mm}~{j.EndUtc:HH:mm} [{j.Status}]{suffix}",
                    j);
            }).ToList();
        }
        catch
        {
            // 資料庫暫時不可用時保留上一次清單
        }
    }

    private void OnCenterBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇匯出資料夾",
            InitialDirectory = CenterOutputBox.Text,
        };

        if (dlg.ShowDialog() == true)
        {
            CenterOutputBox.Text = dlg.FolderName;
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var channels = ChannelList.SelectedItems.Cast<ChannelInfo>().ToList();
        if (channels.Count == 0)
        {
            ExportCenterStatus.Text = "請先選擇至少一個頻道。";
            return;
        }

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            ExportCenterStatus.Text = "請選擇起迄日期。";
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

        var count = 0;
        foreach (var ch in channels)
        {
            _jobs.Enqueue(ch.Id, "main", startUtc, endUtc);
            count++;
        }

        ExportCenterStatus.Text = $"已加入 {count} 筆匯出工作。";
        Reload();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Reload();

    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CenterRunButton.IsEnabled = false;
        ExportCenterStatus.Text = "開始處理佇列…";
        try
        {
            var svc = new ExportJobService(_store);
            var progress = new Progress<ExportProgress>(p => ExportCenterStatus.Text = $"處理中：{p.Status}");
            var result = await svc.ProcessQueuedAsync(CenterOutputBox.Text, progress);
            ExportCenterStatus.Text = $"處理完成：成功 {result.Succeeded}／失敗 {result.Failed}（共 {result.Processed} 筆）";
            Reload();
        }
        catch (Exception ex)
        {
            ExportCenterStatus.Text = $"處理失敗：{ex.Message}";
        }
        finally
        {
            _running = false;
            CenterRunButton.IsEnabled = true;
        }
    }

    private void OnVerifyClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem item)
        {
            ExportCenterStatus.Text = "請先選擇一個工作。";
            return;
        }

        var job = item.Job;
        if (job.Status != "done" || job.OutputPath is null)
        {
            ExportCenterStatus.Text = $"工作 #{job.Id} 尚未完成，無法驗證（狀態：{job.Status}）。";
            return;
        }

        var report = ExportVerifier.Verify(job.OutputPath, job.Sha256);
        var verdict = report.HashMatches ? "OK" : "不符";
        ExportCenterStatus.Text = $"工作 #{job.Id} 完整性：{verdict}（SHA-256 比對）" +
                                  (report.FfprobeSummary is null
                                      ? string.Empty
                                      : $"; {report.FfprobeSummary}");
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem item)
        {
            ExportCenterStatus.Text = "請先選擇一個工作。";
            return;
        }

        _jobs.Delete(item.Job.Id);
        ExportCenterStatus.Text = $"已刪除工作 #{item.Job.Id}。";
        Reload();
    }

    private void OnPurgeClicked(object sender, RoutedEventArgs e)
    {
        var removed = _jobs.PurgeFinished(DateTime.UtcNow.AddHours(-1));
        ExportCenterStatus.Text = $"已清除 {removed} 筆完成（done／failed）紀錄。";
        Reload();
    }

    /// <summary>以選取工作之輸出檔建立分享連結（M51，§14.7 #4）。</summary>
    private void OnShareClicked(object sender, RoutedEventArgs e)
    {
        string? path = null;
        if (JobList.SelectedItem is JobItem { Job.OutputPath: { Length: > 0 } output })
        {
            path = output;
        }

        var window = new ShareWindow(_store, _dataRoot, path)
        {
            Owner = this,
        };
        window.Show();
    }
}