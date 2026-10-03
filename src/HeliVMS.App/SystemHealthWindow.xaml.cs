using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
        ApplyI18n();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Health.Title");
        HeadingText.Text = Localizer.T("Health.Heading");
        HealthRefreshButton.Content = Localizer.T("Health.Refresh");
        TrendLabel.Text = Localizer.T("Health.Trend");

        if (HealthDiskList.View is GridView grid && grid.Columns.Count >= 5)
        {
            grid.Columns[0].Header = Localizer.T("Health.ColDisk");
            grid.Columns[1].Header = Localizer.T("Health.ColTotal");
            grid.Columns[2].Header = Localizer.T("Health.ColFree");
            grid.Columns[3].Header = Localizer.T("Health.ColFreePercent");
            grid.Columns[4].Header = Localizer.T("Health.ColFormat");
        }
    }

    /// <summary>
    /// 每次 Capture() 也會把量測點推進趨勢佇列，所以定時呼叫本身就是在累積 60 分鐘歷史；
    /// 只按一次「立即更新」不會有趨勢，這是預期行為。
    /// </summary>
    private void Refresh()
    {
        try
        {
            var s = SystemMetricsService.Capture();
            HealthSummaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Health.Summary"),
                s.CapturedAtUtc,
                s.UptimeMinutes,
                s.WorkingSetMb,
                s.ManagedHeapMb,
                s.CpuPercent.ToString("0.#", CultureInfo.InvariantCulture));

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
            HealthStatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Health.Updated"),
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            HealthStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Health.Failed"), ex.Message);
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
