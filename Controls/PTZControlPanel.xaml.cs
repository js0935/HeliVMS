using System;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Controls;

public partial class PTZControlPanel : UserControl {
    private Camera? _camera;
    private readonly IOnvifService? _onvif;
    private bool _deleteMode;

    public PTZControlPanel() {
        InitializeComponent();
        _onvif = App.Services.GetService<IOnvifService>();
    }

    public void LoadCamera(Camera camera) {
        _camera = camera;
        IsEnabled = camera.HasPTZ;

        if (camera.HasPTZ) {
            _ = LoadPresetsAsync(camera);
        }
    }

    private async Task LoadPresetsAsync(Camera camera) {
        if (_onvif is null) { return; }
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
        } catch { }
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e) {
        try {
            if (_camera is null || _onvif is null) { return; }

            var dlg = new Dialog.InputDialog("Preset Name", "Enter name for this preset", "") {
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
        if (_camera is null) { return; }

        var btn = (Button)sender;
        _deleteMode = !_deleteMode;
        btn.Content = _deleteMode ? "Click preset to delete" : "Delete Preset";
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e) {
        try {
            if (_camera is null || _onvif is null) { return; }
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

    private async void Up_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0.3f, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Up error: {Msg}", ex.Message); } }
    private async void Down_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, -0.3f, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Down error: {Msg}", ex.Message); } }
    private async void Left_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(-0.3f, 0, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Left error: {Msg}", ex.Message); } }
    private async void Right_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0.3f, 0, 0); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ Right error: {Msg}", ex.Message); } }
    private async void ZoomIn_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0, 0.3f); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ ZoomIn error: {Msg}", ex.Message); } }
    private async void ZoomOut_Click(object sender, RoutedEventArgs e) { try { await SafeMoveAsync(0, 0, -0.3f); } catch (Exception ex) { Log.Debug("[HeliVMS] PTZ ZoomOut error: {Msg}", ex.Message); } }

    private async Task SafeMoveAsync(float x, float y, float zoom) {
        if (_camera is null || _onvif is null) { return; }
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
