using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HeliVMS.App.Services;
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
    private Window? _share;
    private Window? _keys;

    public ExportCenterWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();

        _jobs = new ExportJobRepository(store);

        // remote 旗標限定（M209）：未授權時連按鈕都不留下。
        new LicenseUiGate(store).Apply(ShareFromExportButton, LicenseFeatures.Remote);

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
        var parts = new List<string>
        {
            $"工作 #{job.Id} 完整性：{verdict}（SHA-256 比對）",
            DescribeReceipt(new ExportReceiptService(_store).Verify(job.OutputPath)),
        };

        if (report.FfprobeSummary is not null)
        {
            parts.Add(report.FfprobeSummary);
        }

        ExportCenterStatus.Text = string.Join("；", parts);
    }

    /// <summary>
    /// 收據／簽章驗證結果的一行摘要。與 SHA-256 比對分開呈現：雜湊相符只證明檔案沒變，
    /// 還要收據與簽章有效、且簽署者指紋可被信任，才構成「匯出即驗證」。
    /// </summary>
    private static string DescribeReceipt(ExportReceiptReport receipt)
    {
        if (!receipt.ReceiptExists)
        {
            return $"收據：{receipt.Detail}";
        }

        var signature = receipt.SignatureValid ? "簽章有效" : "簽章無效";
        var hash = receipt.HashMatches ? "雜湊相符" : "雜湊不符";
        var signer = receipt.SelfAssertedKey
            ? "簽署者未經外部比對"
            : receipt.SignerMatched ? "簽署者受信任" : "簽署者不在信任清單";
        return $"收據：{signature}、{hash}、{signer}（{receipt.Detail}）";
    }

    /// <summary>開啟簽章金鑰視窗（僅管理員）；視窗單例重用。</summary>
    private void OnSigningKeysClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ExportCenterStatus.Text = "只有系統管理員可以檢視或換發簽章金鑰。";
            return;
        }

        if (_keys is { IsVisible: true })
        {
            _keys.Activate();
            return;
        }

        _keys = new SigningKeysWindow(_store) { Owner = this };
        _keys.Closed += (_, _) => _keys = null;
        _keys.Show();
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

    /// <summary>將所選失敗工作重新排入佇列（結合「開始處理」重跑）。</summary>
    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem { Job.Status: "failed" } item)
        {
            ExportCenterStatus.Text = "請先選擇一個失敗（failed）的工作。";
            return;
        }

        _jobs.Retry(item.Job.Id);
        ExportCenterStatus.Text = $"已將工作 #{item.Job.Id} 重新排入佇列，按「開始處理」重跑。";
        Reload();
    }

    /// <summary>在檔案總管中顯示所選已完成工作的輸出檔。</summary>
    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => OpenSelectedInExplorer();

    /// <summary>雙擊已完成工作＝開啟資料夾；其他狀態給提示。</summary>
    private void OnJobDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem item)
        {
            return;
        }

        if (item.Job.Status == "done" && !string.IsNullOrEmpty(item.Job.OutputPath))
        {
            OpenSelectedInExplorer();
        }
        else
        {
            ExportCenterStatus.Text = $"工作 #{item.Job.Id} 尚未完成（狀態：{item.Job.Status}）。";
        }
    }

    private void OpenSelectedInExplorer()
    {
        if (JobList.SelectedItem is not JobItem { Job.Status: "done" } item ||
            string.IsNullOrEmpty(item.Job.OutputPath))
        {
            ExportCenterStatus.Text = "請先選擇一個已完成（done）的工作。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Job.OutputPath}\""));
        }
        catch
        {
            ExportCenterStatus.Text = "無法開啟檔案總管。";
        }
    }

    private void OnPurgeClicked(object sender, RoutedEventArgs e)
    {
        var removed = _jobs.PurgeFinished(DateTime.UtcNow.AddHours(-1));
        ExportCenterStatus.Text = $"已清除 {removed} 筆完成（done／failed）紀錄。";
        Reload();
    }

    /// <summary>以選取工作之輸出檔建立分享連結（M51，§14.7 #4）；分享窗單例重用。</summary>
    private void OnShareClicked(object sender, RoutedEventArgs e)
    {
        // remote 旗標限定（M209）：分享是 remote 唯一的主視窗外入口，藏了按鈕還得擋 handler。
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Remote))
        {
            ExportCenterStatus.Text = gate.DenialMessage(LicenseFeatures.Remote);
            return;
        }

        string? path = null;
        if (JobList.SelectedItem is JobItem { Job.OutputPath: { Length: > 0 } output })
        {
            path = output;
        }

        if (_share is { IsVisible: true })
        {
            _share.Activate();
            return;
        }

        _share = new ShareWindow(_store, _dataRoot, path)
        {
            Owner = this,
        };
        _share.Closed += (_, _) => _share = null;
        _share.Show();
    }
}