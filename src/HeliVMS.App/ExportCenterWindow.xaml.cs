using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
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

    /// <summary>整批處理的取消來源；「停止」按鈕用它打斷 <see cref="OnRunClicked"/> 裡的批次。</summary>
    private CancellationTokenSource? _batchCts;
    private Window? _share;
    private Window? _keys;

    public ExportCenterWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        InitializeComponent();
        ApplyI18n();

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

        // 關閉後停掉計時器。這個 lambda 捕獲了 this，不停止的話每開一次匯出中心
        // 就永久留一個視窗實體，並且每兩秒在 UI 執行緒上刷新一個已關閉的視窗。
        Closed += (_, _) => _timer.Stop();
    }

        private sealed record JobItem(string Display, ExportJobRecord Job);

    private void ApplyI18n()
    {
        Title = Localizer.T("ExportCenter.Title");
        HeaderTitle.Text = Localizer.T("ExportCenter.Title");
        HeaderCloseButton.ToolTip = Localizer.T("ExportCenter.CloseTip");
        ExportCenterAddHeader.Text = Localizer.T("ExportCenter.AddHeader");
        ExportCenterStartLabel.Text = Localizer.T("Export.StartLabel");
        ExportCenterEndLabel.Text = Localizer.T("Export.EndLabel");
        ExportCenterChannelLabel.Text = Localizer.T("ExportCenter.Channel");
        ExportCenterOutputLabel.Text = Localizer.T("ExportCenter.OutputFolder");
        ExportCenterListLabel.Text = Localizer.T("ExportCenter.ListHeader");
        JobList.ToolTip = Localizer.T("ExportCenter.ListTip");
        CenterBrowseButton.Content = Localizer.T("ExportCenter.Browse");
        CenterBrowseButton.ToolTip = Localizer.T("ExportCenter.BrowseTip");
        AddExportButton.Content = Localizer.T("ExportCenter.Add");
        AddExportButton.ToolTip = Localizer.T("ExportCenter.AddTip");
        CenterRefreshButton.Content = Localizer.T("ExportCenter.Refresh");
        CenterRefreshButton.ToolTip = Localizer.T("ExportCenter.RefreshTip");
        CenterRunButton.Content = Localizer.T("ExportCenter.Run");
        CenterRunButton.ToolTip = Localizer.T("ExportCenter.RunTip");
        CenterVerifyButton.Content = Localizer.T("ExportCenter.Verify");
        CenterVerifyButton.ToolTip = Localizer.T("ExportCenter.VerifyTip");
        CenterSigningKeysButton.Content = Localizer.T("ExportCenter.SigningKeys");
        CenterSigningKeysButton.ToolTip = Localizer.T("ExportCenter.SigningKeysTip");
        CenterDeleteButton.Content = Localizer.T("ExportCenter.Delete");
        CenterDeleteButton.ToolTip = Localizer.T("ExportCenter.DeleteTip");
        CenterRetryButton.Content = Localizer.T("ExportCenter.Retry");
        CenterRetryButton.ToolTip = Localizer.T("ExportCenter.RetryTip");
        CenterCancelButton.Content = Localizer.T("ExportCenter.Cancel");
        CenterCancelButton.ToolTip = Localizer.T("ExportCenter.CancelTip");
        CenterStopButton.Content = Localizer.T("ExportCenter.Stop");
        CenterStopButton.ToolTip = Localizer.T("ExportCenter.StopTip");
        CenterOpenFolderButton.Content = Localizer.T("ExportCenter.OpenFolder");
        CenterOpenFolderButton.ToolTip = Localizer.T("ExportCenter.OpenFolderTip");
        CenterPurgeButton.Content = Localizer.T("ExportCenter.Purge");
        CenterPurgeButton.ToolTip = Localizer.T("ExportCenter.PurgeTip");
        ShareFromExportButton.Content = Localizer.T("ExportCenter.Share");
        ShareFromExportButton.ToolTip = Localizer.T("ExportCenter.ShareTip");
        CloseButton.Content = Localizer.T("ExportCenter.Close");
        CloseButton.ToolTip = Localizer.T("ExportCenter.CloseTip");
    }

    /// <summary>無邊框最大化視窗沒有系統標題列：✕、關閉鈕與 Esc 是離開路徑。</summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
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
            Title = Localizer.T("ExportCenter.BrowseTitle"),
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
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickChannel");
            return;
        }

        if (!StartDate.SelectedDate.HasValue || !EndDate.SelectedDate.HasValue)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickDates");
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

        ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Added"), count);
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
        CenterStopButton.IsEnabled = true;
        ExportCenterStatus.Text = Localizer.T("ExportCenter.Running");
        try
        {
            // QC_Live 的備份任務可中途停止（ctrlRecBackupTask）：這裡的 token 打斷整批——
            // 正在跑的那筆會以「已取消」收尾（ffmpeg 收到 ct 即收），沒跑的保持 queued。
            using var cts = new CancellationTokenSource();
            _batchCts = cts;
            var svc = new ExportJobService(_store);
            var progress = new Progress<ExportProgress>(p => ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Progress"), p.Status));
            var result = await svc.ProcessQueuedAsync(CenterOutputBox.Text, progress, cts.Token);
            ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Done"), result.Succeeded, result.Failed, result.Processed);
            Reload();
        }
        catch (OperationCanceledException)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.Stopped");
            Reload();
        }
        catch (Exception ex)
        {
            ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.RunFailed"), ex.Message);
        }
        finally
        {
            _running = false;
            CenterRunButton.IsEnabled = true;
            CenterStopButton.IsEnabled = false;
            _batchCts = null;
        }
    }

    /// <summary>停止整批處理（QC_Live 的 ctrlRecBackupTask 對齊）：正在跑的中斷、沒跑的留在佇列。</summary>
    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.NotRunning");
            return;
        }

        _batchCts?.Cancel();
    }

    /// <summary>取消所選排隊中的工作（正在跑的用「停止」，已結束的沒有可取消的東西）。</summary>
    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem item)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickJob");
            return;
        }

        if (!_jobs.Cancel(item.Job.Id))
        {
            ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.NotCancellable"), item.Job.Id);
            return;
        }

        ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Cancelled"), item.Job.Id);
        Reload();
    }

    private void OnVerifyClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem item)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickJob");
            return;
        }

        var job = item.Job;
        if (job.Status != "done" || job.OutputPath is null)
        {
            ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.NotDoneVerify"), job.Id, job.Status);
            return;
        }

        var report = ExportVerifier.Verify(job.OutputPath, job.Sha256);
        var verdict = report.HashMatches ? "OK" : Localizer.T("ExportCenter.Mismatch");
        var parts = new List<string>
        {
            string.Format(Localizer.T("ExportCenter.Integrity"), job.Id, verdict),
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
            return string.Format(Localizer.T("ExportCenter.ReceiptDetail"), receipt.Detail);
        }

        var signature = receipt.SignatureValid ? Localizer.T("ExportCenter.ReceiptValid") : Localizer.T("ExportCenter.ReceiptInvalid");
        var hash = receipt.HashMatches ? Localizer.T("ExportCenter.ReceiptHashMatch") : Localizer.T("ExportCenter.ReceiptHashMismatch");
        var signer = receipt.SelfAssertedKey
            ? Localizer.T("ExportCenter.ReceiptSelfAsserted")
            : receipt.SignerMatched ? Localizer.T("ExportCenter.ReceiptTrusted") : Localizer.T("ExportCenter.ReceiptUntrusted");
        return string.Format(Localizer.T("ExportCenter.ReceiptSummary"), signature, hash, signer, receipt.Detail);
    }

    /// <summary>開啟簽章金鑰視窗（僅管理員）；視窗單例重用。</summary>
    private void OnSigningKeysClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.AdminOnly");
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
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickJob");
            return;
        }

        _jobs.Delete(item.Job.Id);
        ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Deleted"), item.Job.Id);
        Reload();
    }

    /// <summary>將所選失敗工作重新排入佇列（結合「開始處理」重跑）。</summary>
    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem { Job.Status: "failed" } item)
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.RetryPick");
            return;
        }

        _jobs.Retry(item.Job.Id);
        ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Retried"), item.Job.Id);
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
            ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.NotDone"), item.Job.Id, item.Job.Status);
        }
    }

    private void OpenSelectedInExplorer()
    {
        if (JobList.SelectedItem is not JobItem { Job.Status: "done" } item ||
            string.IsNullOrEmpty(item.Job.OutputPath))
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.PickDone");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Job.OutputPath}\""));
        }
        catch
        {
            ExportCenterStatus.Text = Localizer.T("ExportCenter.ExplorerFailed");
        }
    }

    private void OnPurgeClicked(object sender, RoutedEventArgs e)
    {
        var removed = _jobs.PurgeFinished(DateTime.UtcNow.AddHours(-1));
        ExportCenterStatus.Text = string.Format(Localizer.T("ExportCenter.Purged"), removed);
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