using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// PTZ 控制（M25）：對綁定 ONVIF 設備的頻道進行雲台移動／變焦／預設點操作。
/// 需頻道已綁定 <see cref="ChannelInfo.DeviceId"/>，且設備具備 PTZ 能力
/// （<see cref="OnvifDeviceService.EnsurePtzCapabilityAsync"/>）。
/// </summary>
public partial class PtzWindow : Window
{
    private readonly OnvifDeviceService _service;

    private string _profileToken = string.Empty;
    private bool _ready;
    private bool _ptzHeld;

    public PtzWindow(SqliteStore store, ChannelInfo channel, DeviceRecord device)
    {
        InitializeComponent();
        Title = $"PTZ 控制（{channel.Name}）";

        var host = $"http://{device.Ip}:{device.Port}/onvif/device_service";
        var password = string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? null
            : DeviceRepository.Unprotect(device.PasswordEncrypted);
        _service = new OnvifDeviceService(host, device.Username, password);
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _service.EnsurePtzCapabilityAsync();
            if (!_service.HasPtz)
            {
                PtzStatusText.Text = "此設備不支援 PTZ。";
                SetControlsEnabled(false);
                return;
            }

            var profiles = await _service.GetProfilesAsync();
            _profileToken = profiles.FirstOrDefault()?.Token ?? string.Empty;
            if (_profileToken.Length == 0)
            {
                PtzStatusText.Text = "已連線，但設備未提供可操控的 Profile。";
                SetControlsEnabled(false);
                return;
            }

            _ready = true;
            PtzStatusText.Text = $"已連線（{_service.DeviceXAddr}）· PTZ 支援。";
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"連線失敗：{ex.Message}";
            SetControlsEnabled(false);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        var controls = new Control[] { PtzUpLeftButton, PtzUpButton, PtzUpRightButton,
            PtzLeftButton, PtzStopButton, PtzRightButton,
            PtzDownLeftButton, PtzDownButton, PtzDownRightButton,
            PtzZoomInButton, PtzZoomOutButton,
            PtzPresetNameBox, PtzSavePresetButton, PtzRefreshButton, PtzGotoPresetButton, PtzPresetList };
        foreach (var c in controls)
        {
            c.IsEnabled = enabled;
        }
    }

    private async void OnMoveHeldDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        var parts = tag.Split(',');
        var (pan, tilt, zoom) = (
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture));

        _ptzHeld = true;
        try
        {
            await _service.ContinuousMoveAsync(_profileToken, pan, tilt, zoom);
            PtzStatusText.Text = "連續移動中…（放開停止）";
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"移動失敗：{ex.Message}";
            _ptzHeld = false;
        }
    }

    private async void OnMoveHeldUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ptzHeld)
        {
            return;
        }

        _ptzHeld = false;
        try
        {
            await _service.StopPtzAsync(_profileToken);
            PtzStatusText.Text = "已停止。";
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"停止失敗：{ex.Message}";
        }
    }

    private async void OnMoveClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _ptzHeld || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        var parts = tag.Split(',');
        var (pan, tilt, zoom) = (
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture));

        try
        {
            await _service.ContinuousMoveAsync(_profileToken, pan, tilt, zoom);
            await Task.Delay(400);
            await _service.StopPtzAsync(_profileToken);
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"移動失敗：{ex.Message}";
        }
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            await _service.StopPtzAsync(_profileToken);
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"停止失敗：{ex.Message}";
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        await RefreshPresetsAsync();
    }

    private async Task RefreshPresetsAsync()
    {
        try
        {
            var presets = await _service.GetPtzPresetsAsync(_profileToken);
            PtzPresetList.ItemsSource = presets;
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"讀取預設點失敗：{ex.Message}";
        }
    }

    private async void OnSavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(PtzPresetNameBox.Text) ? $"預設點 {DateTime.Now:HH:mm:ss}" : PtzPresetNameBox.Text;
        try
        {
            var token = await _service.SetPtzPresetAsync(_profileToken, name);
            PtzStatusText.Text = string.IsNullOrEmpty(token) ? "已送出儲存要求。" : $"已儲存預設點（{token}）。";
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"儲存失敗：{ex.Message}";
        }
    }

    private async void OnGotoPresetClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || PtzPresetList.SelectedItem is not PtzPreset preset)
        {
            return;
        }

        try
        {
            await _service.GotoPtzPresetAsync(_profileToken, preset.Token);
            PtzStatusText.Text = $"已移至預設點：{preset.Name}";
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"移轉失敗：{ex.Message}";
        }
    }

    private async void OnHomeClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            await _service.HomeAsync(_profileToken);
            PtzStatusText.Text = "已移至原點。";
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"移至原點失敗：{ex.Message}";
        }
    }

    private async void OnDeletePresetClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || PtzPresetList.SelectedItem is not PtzPreset preset)
        {
            return;
        }

        try
        {
            await _service.RemovePtzPresetAsync(_profileToken, preset.Token);
            PtzStatusText.Text = $"已刪除預設點：{preset.Name}";
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = $"刪除失敗：{ex.Message}";
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _service.Dispose();
        base.OnClosed(e);
    }
}