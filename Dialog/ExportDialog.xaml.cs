using System.IO;
using System.Windows;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace HeliVMS.Dialog;

public partial class ExportDialog : Window {
    private readonly IExportService _exportService;
    private readonly ICameraService _cameraService;

    public ExportDialog() {
        InitializeComponent();
        _exportService = App.Services.GetRequiredService<IExportService>();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        StartDatePicker.SelectedDate = DateTime.Today;
        EndDatePicker.SelectedDate = DateTime.Today;
        OutputPathBox.Text = Path.Combine(_exportService.GetDefaultExportPath(),
            $"export_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    private void Browse_Click(object sender, RoutedEventArgs e) {
        var dialog = new SaveFileDialog {
            Title = "選擇匯出位置",
            Filter = "MP4 檔案 (*.mp4)|*.mp4|AVI 檔案 (*.avi)|*.avi",
            FileName = Path.GetFileName(OutputPathBox.Text),
        };
        if (dialog.ShowDialog() == true) {
            OutputPathBox.Text = dialog.FileName;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e) {
        var start = StartDatePicker.SelectedDate;
        var end = EndDatePicker.SelectedDate;
        if (start is null || end is null) {
            MessageBox.Show("請選擇日期範圍", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var output = OutputPathBox.Text.Trim();
        if (string.IsNullOrEmpty(output)) {
            MessageBox.Show("請選擇輸出位置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportBtn.IsEnabled = false;
        ProgressText.Text = "正在匯出...";

        try {
            var cameras = _cameraService.GetAllCameras().Select(c => c.Id).ToList();
            var fmt = FormatCombo.SelectedItem is FrameworkElement fe ? fe.Tag?.ToString() ?? "mp4" : "mp4";
            var request = new ExportRequest {
                StartTime = start.Value,
                EndTime = end.Value.AddDays(1).AddSeconds(-1),
                CameraIds = cameras,
                OutputPath = output,
                Format = fmt,
            };

            var progress = new Progress<double>(p => {
                Dispatcher.Invoke(() => ProgressText.Text = $"匯出進度：{p * 100:F0}%");
            });

            var result = await _exportService.ExportAsync(request, progress);
            ProgressText.Text = $"✓ 匯出完成：{result}";
            ProgressText.Foreground = System.Windows.Media.Brushes.LimeGreen;
            ExportBtn.Content = "完成";
        } catch (Exception ex) {
            ProgressText.Text = $"✗ {ex.Message}";
            ProgressText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            ExportBtn.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
