using System;
using System.Windows;
using System.Windows.Threading;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// Failover 容錯測試/監控視窗（M88，§14.7 #9）：以 <see cref="FailoverCoordinator"/> 演練
/// leader 租約仲裁（取得/續約、讓出、狀態），每秒自動重新整理作為心跳監看。
/// </summary>
public partial class FailoverWindow : Window
{
    private readonly FailoverRepository _repo;
    private readonly DispatcherTimer _timer;
    private string _serverId = "primary";

    public FailoverWindow(SqliteStore store)
    {
        _repo = new FailoverRepository(store);
        InitializeComponent();
        ApplyI18n();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshStatus(), Dispatcher);
        _timer.Start();
        RefreshStatus();

        // 視窗關閉後必須停掉計時器。這個 lambda 捕獲了 this，所以 timer 會把視窗釘住：
        // 不停止的話，每開一次這個視窗就永久多留一個視窗實體，而且每秒都在
        // UI 執行緒上對一個已經關閉的視窗刷資料——症狀是開幾次之後記憶體只漲不跌。
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// 套用目前語系到容錯視窗的固定文字（M57 §14.7）。XAML 已無硬編中文，文字集中於此，
    /// 由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Failover.Title");
        DescriptionText.Text = Localizer.T("Failover.Description");
        ServerIdLabel.Text = Localizer.T("Failover.ServerId");
        LeaseSecLabel.Text = Localizer.T("Failover.LeaseSec");
        FailoverAcquireButton.Content = Localizer.T("Failover.Acquire");
        FailoverAcquireButton.ToolTip = Localizer.T("Failover.AcquireTip");
        FailoverReleaseButton.Content = Localizer.T("Failover.Release");
        FailoverReleaseButton.ToolTip = Localizer.T("Failover.ReleaseTip");
        FailoverRefreshButton.Content = Localizer.T("Failover.Refresh");
        FailoverRefreshButton.ToolTip = Localizer.T("Failover.RefreshTip");
        StatusLabelText.Text = Localizer.T("Failover.StatusLabel");
        FooterText.Text = Localizer.T("Failover.Footer");
    }

    private FailoverCoordinator Coordinator() => new(_repo, _serverId);

    private TimeSpan Lease()
    {
        var seconds = int.TryParse(LeaseSecText.Text, out var v) && v > 0 ? v : 15;
        return TimeSpan.FromSeconds(seconds);
    }

    private void OnAcquireClicked(object sender, RoutedEventArgs e)
    {
        var server = FailoverServerText.Text.Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            FailoverResultText.Text = Localizer.T("Failover.ServerRequired");
            return;
        }

        _serverId = server;
        var role = Coordinator().AcquireOrRenew(Lease(), DateTime.UtcNow);
        FailoverResultText.Text = $"acquire({_serverId})->{role}";
        RefreshStatus();
    }

    private void OnReleaseClicked(object sender, RoutedEventArgs e)
    {
        var cleared = Coordinator().Release(DateTime.UtcNow);
        FailoverResultText.Text = cleared ? Localizer.T("Failover.ReleaseOk") : Localizer.T("Failover.ReleaseSkipped");
        RefreshStatus();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        var status = Coordinator().GetStatus(DateTime.UtcNow);
        FailoverStatusText.Text = string.Format(
            Localizer.T("Failover.StatusFormat"),
            status.Role,
            status.LeaderId ?? "-",
            status.ServerId,
            status.LeaseRemaining.TotalSeconds,
            status.LeaseExpiresUtc?.ToString("HH:mm:ss") ?? "-");
    }
}