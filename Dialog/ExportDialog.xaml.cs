using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace HeliVMS.Dialog;

public partial class ExportDialog : Window {
    private readonly IExportService _exportService;
    private readonly ICameraService _cameraService;
    private readonly List<CameraCheckItem> _cameraItems = new();

    public (DateTime start, DateTime end)? PresetRange {
        set {
            if (value.HasValue) {
                StartDatePicker.SelectedDate = value.Value.start.Date;
                EndDatePicker.SelectedDate = value.Value.end.Date;
                StartTimeBox.Text = value.Value.start.ToString("HH:mm:ss");
                EndTimeBox.Text = value.Value.end.ToString("HH:mm:ss");
            }
        }
    }

    public ExportDialog() {
        InitializeComponent();
        _exportService = App.Services.GetRequiredService<IExportService>();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        StartDatePicker.SelectedDate = DateTime.Today;
        EndDatePicker.SelectedDate = DateTime.Today;
        StartTimeBox.Text = "00:00:00";
        EndTimeBox.Text = "23:59:59";
        OutputPathBox.Text = Path.Combine(_exportService.GetDefaultExportPath(),
            $"export_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        LoadCameras();
    }

    private void LoadCameras() {
        var cameras = _cameraService.GetAllCameras();
        foreach (var cam in cameras) {
            var item = new CameraCheckItem { CameraId = cam.Id, CameraName = cam.Name, IsChecked = true };
            _cameraItems.Add(item);
            var border = new Border {
                Background = FindResource("SurfaceBrush") as System.Windows.Media.Brush,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(6, 3, 6, 3),
            };
            var cb = new CheckBox {
                Content = $"CH{cam.ChannelNumber ?? _cameraItems.Count}  {cam.Name}",
                IsChecked = true,
                FontSize = 11,
                Foreground = FindResource("TextBrush") as System.Windows.Media.Brush,
                Tag = item,
                Margin = new Thickness(0),
            };
            cb.Checked += CameraCheckChanged;
            cb.Unchecked += CameraCheckChanged;
            border.Child = cb;
            CameraListPanel.Children.Add(border);
        }
        UpdateCameraCount();
    }

    private void CameraCheckChanged(object? sender, RoutedEventArgs e) {
        UpdateCameraCount();
    }

    private void UpdateCameraCount() {
        var count = 0;
        foreach (var child in CameraListPanel.Children) {
            if (child is Border b && b.Child is CheckBox cb && cb.IsChecked == true)
                count++;
        }
        CameraCountText.Text = $"{count} / {_cameraItems.Count} 台";
    }

    private void SelectAllCameras_Click(object sender, RoutedEventArgs e) {
        foreach (var child in CameraListPanel.Children) {
            if (child is Border b && b.Child is CheckBox cb)
                cb.IsChecked = true;
        }
    }

    private void ClearAllCameras_Click(object sender, RoutedEventArgs e) {
        foreach (var child in CameraListPanel.Children) {
            if (child is Border b && b.Child is CheckBox cb)
                cb.IsChecked = false;
        }
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
        if (StartDatePicker.SelectedDate is not DateTime startDate || EndDatePicker.SelectedDate is not DateTime endDate) {
            MessageBox.Show("請選擇日期範圍", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TimeSpan.TryParse(StartTimeBox.Text, out var startTime)) startTime = TimeSpan.Zero;
        if (!TimeSpan.TryParse(EndTimeBox.Text, out var endTime)) endTime = new TimeSpan(23, 59, 59);

        var output = OutputPathBox.Text.Trim();
        if (string.IsNullOrEmpty(output)) {
            MessageBox.Show("請選擇輸出位置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedCameras = new List<string>();
        foreach (var child in CameraListPanel.Children) {
            if (child is Border b && b.Child is CheckBox cb && cb.IsChecked == true && cb.Tag is CameraCheckItem item)
                selectedCameras.Add(item.CameraId);
        }
        if (selectedCameras.Count == 0) {
            MessageBox.Show("請至少選擇一台攝影機", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportBtn.IsEnabled = false;
        ProgressText.Text = "正在匯出...";
        ProgressText.Foreground = FindResource("SecondaryTextBrush") as System.Windows.Media.Brush;

        try {
            var fmt = FormatCombo.SelectedItem is FrameworkElement fe ? fe.Tag?.ToString() ?? "mp4" : "mp4";
            var request = new ExportRequest {
                StartTime = startDate + startTime,
                EndTime = endDate + endTime,
                CameraIds = selectedCameras,
                OutputPath = output,
                Format = fmt,
            };

            var progress = new Progress<double>(p => {
                Dispatcher.Invoke(() => ProgressText.Text = $"匯出進度：{p * 100:F0}%");
            });

            var result = await _exportService.ExportAsync(request, progress);
            ProgressText.Text = $"匯出完成：{result}";
            ProgressText.Foreground = FindResource("SuccessBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.LimeGreen;
            ExportBtn.Content = "完成";
        } catch (Exception ex) {
            ProgressText.Text = $"{ex.Message}";
            ProgressText.Foreground = FindResource("ErrorBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.OrangeRed;
            ExportBtn.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }

    private class CameraCheckItem {
        public string CameraId { get; set; } = "";
        public string CameraName { get; set; } = "";
        public bool IsChecked { get; set; } = true;
    }
}
