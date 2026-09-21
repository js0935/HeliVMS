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

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshStatus(), Dispatcher);
        _timer.Start();
        RefreshStatus();
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
            FailoverResultText.Text = "伺服器 ID 不可為空。";
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
        FailoverResultText.Text = cleared ? "release ok（已讓出）" : "release skipped（非現任 Leader，未改寫他人租約）";
        RefreshStatus();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        var status = Coordinator().GetStatus(DateTime.UtcNow);
        FailoverStatusText.Text =
            $"角色={status.Role}; Leader={status.LeaderId ?? "-"}; 本機={status.ServerId}; " +
            $"剩餘={status.LeaseRemaining.TotalSeconds:F1}s; 到期={status.LeaseExpiresUtc?.ToString("HH:mm:ss") ?? "-"}";
    }
}