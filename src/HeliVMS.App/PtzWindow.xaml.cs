using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared;
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

    private IReadOnlyList<OnvifProfile> _profiles = Array.Empty<OnvifProfile>();
    private string _profileToken = string.Empty;
    private bool _ready;
    private bool _ptzHeld;

    // 每次點擊／步進的相對位移量（PTZ 正規化座標，約為全行程的比例）。實機覺得轉幅偏大或偏小時調整此值。
    private const double NudgeStep = 0.02;

    /// <summary>設備不支援 RelativeMove（回 ActionNotSupported）時改走 ContinuousMove，並記住以免每步都失敗一次。</summary>
    private bool _nudgeRelativeUnsupported;

    /// <summary>
    /// 目前這次移動是否由滑鼠按住觸發。放開滑鼠時 WPF 會在 PreviewMouseLeftButtonUp 之後
    /// 再補一個 Click，若不記錄來源，Click 會緊接著再送一次步進，形成「按住 + 400ms」的雙重合併。
    /// </summary>
    private bool _moveHeldByMouse;

    public PtzWindow(SqliteStore store, ChannelInfo channel, DeviceRecord device)
    {
        InitializeComponent();
        ApplyI18n();
        Title = string.Format(Localizer.T("Ptz.TitleWindow"), channel.Name);

        var host = $"http://{device.Ip}:{device.Port}/onvif/device_service";

        // 以 SafeUnprotect 而非 Unprotect：舊資料可能為明文或由其他機器／帳戶加密，
        // 直接解密會擲出例外而讓視窗開不起來（建構子無 try/catch）。
        var password = string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);
        _service = new OnvifDeviceService(host, device.Username, password);
        Loaded += async (_, _) => await InitializeAsync();
    }

    private void ApplyI18n()
    {
        // 微型模式（220x340）沒有鉛直空間放分組標題與獨立 Profile 標籤；
        // 資訊改由工具提示與狀態列承載。
        Title = Localizer.T("Ptz.Title");
        PtzStatusText.Text = Localizer.T("Ptz.StatusConnecting");
        PtzProfileCombo.ToolTip = Localizer.T("Ptz.ProfileLabel");
        PtzUpLeftButton.ToolTip = Localizer.T("Ptz.MoveUpLeftTip");
        PtzUpButton.ToolTip = Localizer.T("Ptz.MoveUpTip");
        PtzUpRightButton.ToolTip = Localizer.T("Ptz.MoveUpRightTip");
        PtzLeftButton.ToolTip = Localizer.T("Ptz.MoveLeftTip");
        PtzStopButton.ToolTip = Localizer.T("Ptz.StopTip");
        PtzRightButton.ToolTip = Localizer.T("Ptz.MoveRightTip");
        PtzDownLeftButton.ToolTip = Localizer.T("Ptz.MoveDownLeftTip");
        PtzDownButton.ToolTip = Localizer.T("Ptz.MoveDownTip");
        PtzDownRightButton.ToolTip = Localizer.T("Ptz.MoveDownRightTip");
        PtzZoomInButton.Content = Localizer.T("Ptz.ZoomIn");
        PtzZoomInButton.ToolTip = Localizer.T("Ptz.ZoomInTip");
        PtzZoomOutButton.Content = Localizer.T("Ptz.ZoomOut");
        PtzZoomOutButton.ToolTip = Localizer.T("Ptz.ZoomOutTip");
        PtzHomeButton.ToolTip = Localizer.T("Ptz.HomeTip");
        PtzSavePresetButton.ToolTip = Localizer.T("Ptz.SavePresetTip");
        PtzDeletePresetButton.ToolTip = Localizer.T("Ptz.DeletePresetTip");
        PtzGotoPresetButton.ToolTip = Localizer.T("Ptz.GotoPresetTip");
        PtzCloseButton.Content = Localizer.T("Ptz.Close");
        PtzCloseButton.ToolTip = Localizer.T("Ptz.CloseTip");
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _service.EnsurePtzCapabilityAsync();
            if (!_service.HasPtz)
            {
                PtzStatusText.Text = Localizer.T("Ptz.NoPtz");
                SetControlsEnabled(false);
                return;
            }

            var profiles = await _service.GetProfilesAsync();
            if (profiles.Count == 0)
            {
                PtzStatusText.Text = Localizer.T("Ptz.NoProfile");
                SetControlsEnabled(false);
                return;
            }

            _profiles = profiles;
            PtzProfileCombo.ItemsSource = _profiles;
            PtzProfileCombo.SelectedIndex = 0;
            _profileToken = _profiles[0].Token;

            _ready = true;
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.Connected"), _service.DeviceXAddr);
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.ConnectFailed"), ex.Message);
            SetControlsEnabled(false);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        var controls = new Control[] { PtzProfileCombo, PtzUpLeftButton, PtzUpButton, PtzUpRightButton,
            PtzLeftButton, PtzStopButton, PtzRightButton,
            PtzDownLeftButton, PtzDownButton, PtzDownRightButton,
            PtzZoomInButton, PtzZoomOutButton,
            PtzSavePresetButton, PtzGotoPresetButton, PtzDeletePresetButton, PtzPresetList };
        foreach (var c in controls)
        {
            c.IsEnabled = enabled;
        }
    }

    private async void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PtzProfileCombo.SelectedItem is not OnvifProfile profile)
        {
            return;
        }

        _profileToken = profile.Token;
        PtzStatusText.Text = string.Format(Localizer.T("Ptz.ProfileSwitched"), profile.Token);
        await RefreshPresetsAsync();
    }

    private async void OnMoveHeldDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || _ptzHeld || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        var (pan, tilt, zoom) = ParseDirection(tag);

        // 標記此次移動由滑鼠按住觸發：放開後 WPF 會再補一個 Click，OnMoveClicked 依此忽略它，
        // 否則「點一下」會被按住迴圈與 Click 兩條路徑各移動一次。
        _moveHeldByMouse = true;

        _ptzHeld = true;
        try
        {
            while (_ptzHeld)
            {
                await NudgeAsync(pan, tilt, zoom);

                // 200ms 間隔大於一般點擊的按住時間（約 80–150ms），所以「點一下」只位移一步；
                // 真正按住才連續步進。相機回應慢時 await 本身就會拉長間隔，無需另加節流。
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.MoveFailed"), ex.Message);
            _ptzHeld = false;
        }
    }

    private async void OnMoveHeldUp(object sender, MouseButtonEventArgs e) => await StopMoveAsync();

    private async void OnMoveMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 滑離按鈕通常代表放開時不會落在按鈕上（也就不會產生 Click）；先清掉滑鼠標記，
        // 免得之後改用鍵盤操作時被誤判成滑鼠點擊而吞掉。
        _moveHeldByMouse = false;
        await StopMoveAsync();
    }

    private async Task StopMoveAsync()
    {
        if (!_ptzHeld)
        {
            return;
        }

        _ptzHeld = false;
        try
        {
            await _service.StopPtzAsync(_profileToken);
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.StopFailed"), ex.Message);
        }
    }

    /// <summary>
    /// 送一次位移。優先使用 RelativeMove：它是「固定且極小的一步、設備自行停止」，
    /// 不受 Stop 晚到影響。ContinuousMove 是速度命令，必須靠後續的 Stop 才停；相機處理 SOAP
    /// 較慢時 Stop 會晚到，於是「點一下」的實際位移時間被網路往返拉長，實機症狀就是轉幅遠大於按住時間。
    /// <para>
    /// 設備若回 ActionNotSupported（不支援 RelativeMove）就改回 ContinuousMove 並記住結果，
    /// 之後不再嘗試 RelativeMove，行為退回舊版。
    /// </para>
    /// </summary>
    private async Task NudgeAsync(double pan, double tilt, double zoom)
    {
        if (!_nudgeRelativeUnsupported)
        {
            try
            {
                await _service.RelativeMoveAsync(
                    _profileToken,
                    Math.Sign(pan) * NudgeStep,
                    Math.Sign(tilt) * NudgeStep,
                    Math.Sign(zoom) * NudgeStep);
                return;
            }
            catch (OnvifException ex) when (IsActionNotSupported(ex))
            {
                _nudgeRelativeUnsupported = true;
            }
        }

        await _service.ContinuousMoveAsync(_profileToken, pan, tilt, zoom);
    }

    private static bool IsActionNotSupported(OnvifException ex) =>
        ex.FaultCode is { } code &&
        (code.EndsWith("ActionNotSupported", StringComparison.OrdinalIgnoreCase) ||
         code.EndsWith("NotSupported", StringComparison.OrdinalIgnoreCase));

    private static (double Pan, double Tilt, double Zoom) ParseDirection(string tag)
    {
        var parts = tag.Split(',');
        return (
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 方向／變焦鈕的 Click。滑鼠按住的路徑已在 <see cref="OnMoveHeldDown"/> 處理並於放開時送出 Stop，
    /// 這裡只服務鍵盤（Enter／Space）：送一次位移後停止。
    /// </summary>
    private async void OnMoveClicked(object sender, RoutedEventArgs e)
    {
        if (_moveHeldByMouse)
        {
            _moveHeldByMouse = false;
            return;
        }

        if (!_ready || _ptzHeld || sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        var (pan, tilt, zoom) = ParseDirection(tag);
        try
        {
            await NudgeAsync(pan, tilt, zoom);

            // 只有 ContinuousMove 後備路徑需要明確停止；RelativeMove 會自己停。
            if (_nudgeRelativeUnsupported)
            {
                await _service.StopPtzAsync(_profileToken);
            }
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.MoveFailed"), ex.Message);
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
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.StopFailed"), ex.Message);
        }
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
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.PresetReadFailed"), ex.Message);
        }
    }

    private async void OnSavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // 微型模式沒有名稱輸入框：一律用目前時刻當預設點名稱。
        var name = string.Format(Localizer.T("Ptz.DefaultPresetName"), DateTime.Now.ToString("HH:mm:ss"));
        try
        {
            var token = await _service.SetPtzPresetAsync(_profileToken, name);
            PtzStatusText.Text = string.IsNullOrEmpty(token)
                ? Localizer.T("Ptz.PresetSaveSent")
                : string.Format(Localizer.T("Ptz.PresetSaved"), token);
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.SaveFailed"), ex.Message);
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
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.GoneToPreset"), preset.Name);
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.GotoFailed"), ex.Message);
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
            PtzStatusText.Text = Localizer.T("Ptz.HomeDone");
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.HomeFailed"), ex.Message);
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
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.PresetDeleted"), preset.Name);
            await RefreshPresetsAsync();
        }
        catch (Exception ex)
        {
            PtzStatusText.Text = string.Format(Localizer.T("Ptz.DeleteFailed"), ex.Message);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _service.Dispose();
        base.OnClosed(e);
    }
}