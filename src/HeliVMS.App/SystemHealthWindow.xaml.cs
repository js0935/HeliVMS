using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 系統健康視窗（M147，§14.7 #2）：把 <see cref="SystemMetricsService"/> 的本機計數器
/// 拉到桌面顯示。Web 早就有 /api/system-metrics，桌面先前完全看不到這些數字。
/// </summary>
public partial class SystemHealthWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public SystemHealthWindow()
    {
        InitializeComponent();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// 每次 Capture() 也會把量測點推進趨勢佇列，所以定時呼叫本身就是在累積 60 分鐘歷史；
    /// 只按一次「立即更新」不會有趨勢，這是預期行為。
    /// </summary>
    private void Refresh()
    {
        try
        {
            var s = SystemMetricsService.Capture();
            HealthSummaryText.Text =
                $"擷取時間：{s.CapturedAtUtc}\n" +
                $"運行時間：{s.UptimeMinutes} 分鐘\n" +
                $"工作集：{s.WorkingSetMb} MB　　受控堆積：{s.ManagedHeapMb} MB\n" +
                $"CPU：{s.CpuPercent:0.#}%";

            HealthDiskList.ItemsSource = s.Disks
                .Select(d => new
                {
                    Name = d.Name,
                    Total = $"{d.TotalMb / 1024d:0.#} GB",
                    Free = $"{d.FreeMb / 1024d:0.#} GB",
                    FreePercent = d.TotalMb > 0 ? $"{100.0 * d.FreeMb / d.TotalMb:0.#}%" : "-",
                    Format = d.Format,
                })
                .ToList();

            RenderTrend(SystemMetricsService.CaptureHistory());
            HealthStatusText.Text = $"已更新（{DateTime.Now:HH:mm:ss}），每 5 秒自動更新。";
        }
        catch (Exception ex)
        {
            HealthStatusText.Text = $"讀取失敗：{ex.Message}";
        }
    }

    /// <summary>把記憶體工作集趨勢畫成折線；點數不足或版面未量測時直接略過。</summary>
    private void RenderTrend(IReadOnlyList<SystemMetricsService.SystemMetricsTrendPoint> points)
    {
        HealthTrendCanvas.Children.Clear();
        var w = HealthTrendCanvas.ActualWidth;
        var h = HealthTrendCanvas.ActualHeight;
        if (points.Count < 2 || w <= 1 || h <= 1)
        {
            return;
        }

        var max = Math.Max(0.001, points.Max(p => p.WorkingSetGb));
        var line = new Polyline { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5 };
        for (var i = 0; i < points.Count; i++)
        {
            var x = w * i / (points.Count - 1);
            var y = h - (h - 8) * (points[i].WorkingSetGb / max) - 4;
            line.Points.Add(new Point(x, y));
        }

        HealthTrendCanvas.Children.Add(line);
    }
}
