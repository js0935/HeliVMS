using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Controls;

public class PtzTour {
    public string Name { get; set; } = "";
    public List<(string presetToken, string presetName, int dwellSec)> Steps { get; set; } = [];
}

public partial class PTZControlPanel : UserControl {
    private Camera? _camera;
    private readonly IOnvifService? _onvif;
    private readonly TourService? _tourService;
    private bool _deleteMode;
    private bool _tourRunning;
    private CancellationTokenSource? _tourCts;
    private readonly List<PtzTour> _tours = [];
    private PtzTour? _selectedTour;

    public PTZControlPanel() {
        InitializeComponent();
        _onvif = App.Services.GetService<IOnvifService>();
        _tourService = App.Services.GetService<TourService>();
    }

    public void LoadCamera(Camera camera) {
        _camera = camera;
        IsEnabled = camera.HasPTZ;
        if (camera.HasPTZ) {
            _ = LoadPresetsAsync(camera);
            _tourService?.LoadToursForCamera(camera.Id, _tours);
            RefreshToursPanel();
        }
    }

    public void UnloadCamera() {
        StopTour();
        _camera = null;
    }

    private async Task LoadPresetsAsync(Camera camera) {
        if (_onvif is null) return;
        try {
            var presets = await _onvif.PTZ_GetPresetsAsync(
                camera.IpAddress ?? "", camera.OnvifPort,
                camera.Username ?? "", camera.Password ?? "");
            _deleteMode = false;
            PresetsPanel.Children.Clear();
            foreach (var preset in presets) {
                var btn = new Button {
                    Content = string.IsNullOrEmpty(preset.Name) ? preset.Token : preset.Name,
                    Style = (Style)FindResource("SecondaryButton"),
                    Margin = new Thickness(0, 0, 4, 4),
                    Tag = preset.Token
                };
                btn.Click += PresetButton_Click;
                PresetsPanel.Children.Add(btn);
            }
            RefreshToursPanel();
        } catch { }
    }

    private void RefreshToursPanel() {
        ToursPanel.Children.Clear();
        _selectedTour = null;
        foreach (var tour in _tours) {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 4) };
            var btn = new Button {
                Content = tour.Name,
                Style = (Style)FindResource("SecondaryButton"),
                Tag = tour
            };
            btn.Click += TourSelect_Click;
            sp.Children.Add(btn);
            var delBtn = new Button {
                Content = "✕",
                Width = 22, Height = 22,
                FontSize = 10,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = tour
            };
            delBtn.Click += (s, _) => {
                if (s is Button { Tag: PtzTour t }) DeleteTour_Click(t);
            };
            sp.Children.Add(delBtn);
            ToursPanel.Children.Add(sp);
        }
        StartTourBtn.Content = "▶ 開始巡航";
    }

    private void TourSelect_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: PtzTour tour }) {
            _selectedTour = tour;
            StartTourBtn.Content = $"▶ {tour.Name}";
        }
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e) {
        try {
            if (_camera is null || _onvif is null) return;
            var dlg = new Dialog.InputDialog("儲存預設點", "請輸入預設點名稱：", "") {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Value)) {
                await _onvif.PTZ_SetPresetAsync(
                    _camera.IpAddress ?? "", _camera.OnvifPort,
                    _camera.Username ?? "", _camera.Password ?? "", dlg.Value);
                await LoadPresetsAsync(_camera);
            }
        } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ SavePreset error: {Msg}", ex.Message); }
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e) {
        if (_camera is null) return;
        _deleteMode = !_deleteMode;
        ((Button)sender).Content = _deleteMode ? "點選要刪除的預設點" : "－ 刪除";
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e) {
        try {
            if (_camera is null || _onvif is null) return;
            if (sender is Button btn && btn.Tag is string token) {
                if (_deleteMode) {
                    await _onvif.PTZ_RemovePresetAsync(
                        _camera.IpAddress ?? "", _camera.OnvifPort,
                        _camera.Username ?? "", _camera.Password ?? "", token);
                    _deleteMode = false;
                    await LoadPresetsAsync(_camera);
                } else {
                    await _onvif.PTZ_GotoPresetAsync(
                        _camera.IpAddress ?? "", _camera.OnvifPort,
                        _camera.Username ?? "", _camera.Password ?? "", token);
                }
            }
        } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ PresetButton error: {Msg}", ex.Message); }
    }

    private void AddTour_Click(object sender, RoutedEventArgs e) {
        try {
            if (_camera is null) return;
            var dlg = new Dialog.InputDialog("新增巡航路線", "請輸入路線名稱：", "") {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Value)) {
                var tour = new PtzTour { Name = dlg.Value.Trim() };
                _tours.Add(tour);
                _tourService?.SaveTour(_camera.Id, tour);
                RefreshToursPanel();
            }
        } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ AddTour error: {Msg}", ex.Message); }
    }

    private void DeleteTour_Click(PtzTour tour) {
        try {
            if (_camera is null) return;
            if (MessageBox.Show($"確定刪除巡航路線「{tour.Name}」？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                _tours.Remove(tour);
                _tourService?.DeleteTour(_camera.Id, tour.Name);
                if (_selectedTour == tour) {
                    _selectedTour = null;
                    StartTourBtn.Content = "▶ 開始巡航";
                }
                RefreshToursPanel();
            }
        } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ DeleteTour error: {Msg}", ex.Message); }
    }

    private async void ToggleTour_Click(object sender, RoutedEventArgs e) {
        if (_tourRunning) {
            StopTour();
            return;
        }
        if (_selectedTour is null || _selectedTour.Steps.Count == 0) {
            if (_selectedTour is not null) {
                var stepDlg = new Dialog.InputDialog("新增巡航步驟",
                    "格式：預設點名稱,停留秒數\n例如：入口,5", "入口,5") {
                    Owner = Window.GetWindow(this)
                };
                if (stepDlg.ShowDialog() == true && !string.IsNullOrEmpty(stepDlg.Value)) {
                    var parts = stepDlg.Value.Split(',');
                    if (parts.Length >= 2 && int.TryParse(parts[^1].Trim(), out var dwell)) {
                        var name = string.Join(",", parts.Take(parts.Length - 1)).Trim();
                        _selectedTour.Steps.Add(("", name, Math.Max(1, dwell)));
                    }
                }
            }
            return;
        }
        await StartTourAsync();
    }

    private async Task StartTourAsync() {
        if (_camera is null || _onvif is null || _selectedTour is null) return;
        _tourRunning = true;
        StartTourBtn.Content = "⏹ 停止巡航";
        _tourCts = new CancellationTokenSource();
        var ct = _tourCts.Token;
        _ = Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                foreach (var step in _selectedTour.Steps) {
                    if (ct.IsCancellationRequested) break;
                    try {
                        await _onvif.PTZ_GotoPresetAsync(
                            _camera.IpAddress ?? "", _camera.OnvifPort,
                            _camera.Username ?? "", _camera.Password ?? "", step.presetToken);
                        await Task.Delay(step.dwellSec * 1000, ct);
                    } catch { break; }
                }
            }
        });
    }

    private void StopTour() {
        _tourRunning = false;
        _tourCts?.Cancel();
        _tourCts?.Dispose();
        _tourCts = null;
        StartTourBtn.Content = "▶ 開始巡航";
    }

    private async void Up_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0.3f, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Up error: {Msg}", ex.Message); } }
    private async void Down_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, -0.3f, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Down error: {Msg}", ex.Message); } }
    private async void Left_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(-0.3f, 0, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Left error: {Msg}", ex.Message); } }
    private async void Right_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0.3f, 0, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Right error: {Msg}", ex.Message); } }
    private async void ZoomIn_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0, 0.3f); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ ZoomIn error: {Msg}", ex.Message); } }
    private async void ZoomOut_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0, -0.3f); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ ZoomOut error: {Msg}", ex.Message); } }

    private async Task SafeMoveAsync(float x, float y, float zoom) {
        if (_camera is null || _onvif is null) return;
        try {
            await _onvif.PTZ_ContinuousMoveAsync(
                _camera.IpAddress ?? "", _camera.OnvifPort,
                _camera.Username ?? "", _camera.Password ?? "", x, y, zoom);
            await Task.Delay(500);
            await _onvif.PTZ_StopAsync(
                _camera.IpAddress ?? "", _camera.OnvifPort,
                _camera.Username ?? "", _camera.Password ?? "");
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] PTZ MoveAsync error: {Msg}", ex.Message);
        }
    }
}
