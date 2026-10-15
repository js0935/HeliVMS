using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.App.Services;
using HeliVMS.Devices.Onvif;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Microsoft.Win32;

namespace HeliVMS.App;

/// <summary>
/// 設備工具列（§7.4）：對單一攝影機做遠端維運——ONVIF 診斷、音視探測、
/// 遠端重啟與韌體升級，並列出這台設備的操作歷史（v51 <c>device_operations</c>）。
///
/// <para>
/// 這支視窗直接動到現場設備，因此兩個破壞性命令（重啟、韌體升級）都要求管理員且二次確認；
/// 診斷與音視探測是唯讀，任何角色皆可執行。每個命令都會留下操作紀錄與稽核，失敗也照記——
/// 「按了沒反應」正是這張歷史表存在的理由。
/// </para>
///
/// <para>
/// 韌體升級採 ONVIF <c>StartFirmwareUpgrade</c>（將檔案內嵌於 <c>Upload</c>）；多數設備
/// 升級後仍需重啟才生效，故成功後會提示操作員再按「重啟」。
/// </para>
/// </summary>
public partial class DeviceToolsWindow : Window
{
    private readonly DeviceOperationRepository _operations;
    private readonly DeviceRepository _devices;
    private readonly int _deviceId;
    private readonly DeviceRecord? _device;
    private bool _rebootArm;
    private bool _busy;

    public DeviceToolsWindow(SqliteStore store, int deviceId)
    {
        var audit = new AuditLogRepository(store);
        _devices = new DeviceRepository(store, audit);
        _operations = new DeviceOperationRepository(store, audit);
        _deviceId = deviceId;
        _device = _devices.Get(deviceId);

        InitializeComponent();
        ApplyI18n();

        if (_device is null)
        {
            // 設備可能在視窗開啟前被刪除；停用全部命令，避免對不存在的設備送 SOAP。
            SetCommandsEnabled(false);
            StatusText.Text = Localizer.T("DeviceTools.NoDevice");
            return;
        }

        ToolsDesc.Text = string.Format(
            Localizer.T("DeviceTools.DeviceLine"), _device.Name, _device.Ip, _device.Port);
        StatusText.Text = Localizer.T("DeviceTools.StatusIdle");
        ReloadHistory();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("DeviceTools.Title");
        ToolsHeader.Text = Localizer.T("DeviceTools.Header");
        DiagnosticsButton.Content = Localizer.T("DeviceTools.Diagnostics");
        DiagnosticsButton.ToolTip = Localizer.T("DeviceTools.DiagnosticsTip");
        AvTestButton.Content = Localizer.T("DeviceTools.AvTest");
        AvTestButton.ToolTip = Localizer.T("DeviceTools.AvTestTip");
        RebootButton.Content = Localizer.T("DeviceTools.Reboot");
        RebootButton.ToolTip = Localizer.T("DeviceTools.RebootTip");
        FirmwareButton.Content = Localizer.T("DeviceTools.Firmware");
        FirmwareButton.ToolTip = Localizer.T("DeviceTools.FirmwareTip");
        RefreshButton.Content = Localizer.T("DeviceTools.Refresh");
        RefreshButton.ToolTip = Localizer.T("DeviceTools.RefreshTip");
        CloseButton.Content = Localizer.T("DeviceTools.Close");
        ReportHeader.Text = Localizer.T("DeviceTools.ReportHeader");
        HistoryHeader.Text = Localizer.T("DeviceTools.HistoryHeader");
        ColTime.Header = Localizer.T("DeviceTools.ColTime");
        ColOp.Header = Localizer.T("DeviceTools.ColOp");
        ColResult.Header = Localizer.T("DeviceTools.ColResult");
        ColDetail.Header = Localizer.T("DeviceTools.ColDetail");
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnRefreshHistoryClicked(object sender, RoutedEventArgs e) => ReloadHistory();

    /// <summary>ONVIF 診斷（唯讀）：設備資訊、Profile 數、與本機的時鐘偏差。</summary>
    private async void OnDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (_device is null)
        {
            return;
        }

        await RunExclusiveAsync(async () =>
        {
            try
            {
                using var service = CreateService(_device);
                var info = await service.GetInfoAsync();
                var profiles = await service.GetProfilesAsync();

                var sb = new StringBuilder();
                sb.AppendLine(string.Format(
                    Localizer.T("DeviceTools.DiagInfo"),
                    info.Manufacturer, info.Model, info.FirmwareVersion, info.SerialNumber));
                sb.AppendLine(string.Format(Localizer.T("DeviceTools.DiagProfiles"), profiles.Count));
                sb.AppendLine(info.SystemDateTimeUtc is DateTimeOffset deviceUtc
                    ? string.Format(Localizer.T("DeviceTools.DiagClock"), (deviceUtc.UtcDateTime - DateTime.UtcNow).TotalSeconds)
                    : Localizer.T("DeviceTools.DiagClockUnknown"));

                ReportText.Text = sb.ToString();
                Record(DeviceOperationTypes.Diagnostics, DeviceOperationResults.Ok, null);
                StatusText.Text = Localizer.T("DeviceTools.DiagnosticsDone");
            }
            catch (Exception ex)
            {
                ReportText.Text = ex.Message;
                Record(DeviceOperationTypes.Diagnostics, DeviceOperationResults.Failed, ex.Message);
                StatusText.Text = string.Format(Localizer.T("DeviceTools.DiagnosticsFailed"), ex.Message);
            }
        });
    }

    /// <summary>音視探測（唯讀）：以 ffprobe 探測主碼流的解析度、編碼與音訊。</summary>
    private async void OnAvTestClicked(object sender, RoutedEventArgs e)
    {
        if (_device is null)
        {
            return;
        }

        var url = await ResolveStreamUrlAsync();
        if (string.IsNullOrWhiteSpace(url))
        {
            ReportText.Text = Localizer.T("DeviceTools.AvNoStream");
            StatusText.Text = Localizer.T("DeviceTools.AvNoStream");
            return;
        }

        var device = _device;
        await RunExclusiveAsync(async () =>
        {
            try
            {
                var (user, pass) = _devices.GetRtspCredentials(device.Id);
                var probeUrl = string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)
                    ? url
                    : RtspUri.WithCredentials(url!, user, pass);

                var info = await Task.Run(() =>
                    StreamProbe.Probe(probeUrl, timeout: TimeSpan.FromSeconds(12)));

                var summary = string.Format(
                    Localizer.T("DeviceTools.AvDone"),
                    info.Width, info.Height, info.VideoCodec, info.AudioCodec ?? "-", info.Fps);
                ReportText.Text = summary;
                Record(DeviceOperationTypes.AvTest, DeviceOperationResults.Ok, null);
                StatusText.Text = summary;
            }
            catch (Exception ex)
            {
                ReportText.Text = string.Format(Localizer.T("DeviceTools.AvFailed"), ex.Message);
                Record(DeviceOperationTypes.AvTest, DeviceOperationResults.Failed, ex.Message);
                StatusText.Text = string.Format(Localizer.T("DeviceTools.AvFailed"), ex.Message);
            }
        });
    }

    /// <summary>遠端重啟（破壞性）：管理員、兩段式確認。</summary>
    private async void OnRebootClicked(object sender, RoutedEventArgs e)
    {
        if (_device is null)
        {
            return;
        }

        if (!SessionContext.IsAdmin)
        {
            StatusText.Text = Localizer.T("DeviceTools.AdminOnly");
            return;
        }

        if (!_rebootArm)
        {
            // 兩段式：第一次只改按鈕文字，第二次才真的送命令——重啟會中斷現場畫面，誤觸代價高。
            _rebootArm = true;
            RebootButton.Content = Localizer.T("DeviceTools.RebootConfirm");
            StatusText.Text = Localizer.T("DeviceTools.RebootConfirmHint");
            return;
        }

        _rebootArm = false;
        RebootButton.Content = Localizer.T("DeviceTools.Reboot");

        await RunExclusiveAsync(async () =>
        {
            try
            {
                using var service = CreateService(_device);
                var message = await service.RebootAsync();
                Record(DeviceOperationTypes.Reboot, DeviceOperationResults.Ok, message);
                var summary = string.Format(Localizer.T("DeviceTools.RebootDone"), message);
                ReportText.Text = summary;
                StatusText.Text = summary;
            }
            catch (Exception ex)
            {
                Record(DeviceOperationTypes.Reboot, DeviceOperationResults.Failed, ex.Message);
                var summary = string.Format(Localizer.T("DeviceTools.RebootFailed"), ex.Message);
                ReportText.Text = summary;
                StatusText.Text = summary;
            }
        });
    }

    /// <summary>韌體遠端升級（破壞性、不可逆）：管理員、選檔、明確確認。</summary>
    private async void OnFirmwareClicked(object sender, RoutedEventArgs e)
    {
        if (_device is null)
        {
            return;
        }

        if (!SessionContext.IsAdmin)
        {
            StatusText.Text = Localizer.T("DeviceTools.AdminOnly");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Localizer.T("DeviceTools.FirmwarePickTitle"),
            Filter = Localizer.T("DeviceTools.FirmwareFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        byte[] firmware;
        try
        {
            firmware = await Task.Run(() => File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Localizer.T("DeviceTools.FirmwareFailed"), ex.Message);
            return;
        }

        var fileName = Path.GetFileName(dialog.FileName);
        var confirmed = MessageBox.Show(
            this,
            string.Format(Localizer.T("DeviceTools.FirmwareConfirm"), fileName, _device.Name),
            Localizer.T("DeviceTools.Firmware"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var device = _device;
        await RunExclusiveAsync(async () =>
        {
            try
            {
                using var service = CreateService(device);
                await service.StartFirmwareUpgradeAsync(firmware);
                Record(DeviceOperationTypes.FirmwareUpgrade, DeviceOperationResults.Ok, fileName);
                var summary = string.Format(Localizer.T("DeviceTools.FirmwareDone"), fileName);
                ReportText.Text = summary + Environment.NewLine + Localizer.T("DeviceTools.FirmwareRebootHint");
                StatusText.Text = summary;
            }
            catch (Exception ex)
            {
                Record(DeviceOperationTypes.FirmwareUpgrade, DeviceOperationResults.Failed, ex.Message);
                var summary = string.Format(Localizer.T("DeviceTools.FirmwareFailed"), ex.Message);
                ReportText.Text = summary;
                StatusText.Text = summary;
            }
        });
    }

    /// <summary>主碼流位址：優先用設備已存的裸位址，否則以 ONVIF GetStreamUri 取得。</summary>
    private async Task<string?> ResolveStreamUrlAsync()
    {
        if (_device is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_device.MainStreamUrl))
        {
            return _device.MainStreamUrl;
        }

        try
        {
            using var service = CreateService(_device);
            var profiles = await service.GetProfilesAsync();
            return profiles.Select(p => p.StreamUri).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>建立 ONVIF 服務端點；密碼以 DPAPI 解密後使用。</summary>
    private static OnvifDeviceService CreateService(DeviceRecord device)
    {
        var host = $"http://{device.Ip}:{device.Port}/onvif/device_service";
        var password = string.IsNullOrEmpty(device.PasswordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);
        return new OnvifDeviceService(host, device.Username, password);
    }

    private void Record(string opType, string result, string? detail)
    {
        try
        {
            _operations.Record(_deviceId, opType, null, result, detail, Actor());
        }
        catch (Exception)
        {
            // 紀錄失敗不應蓋掉命令本身的結果（命令可能已送達設備）。
        }

        ReloadHistory();
    }

    private void ReloadHistory()
    {
        var rows = _operations.List(_deviceId, 50)
            .Select(o => new HistoryRow(
                o.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                OpLabel(o.OpType),
                ResultLabel(o.Result),
                o.Detail ?? o.Param ?? string.Empty))
            .ToList();

        HistoryList.ItemsSource = rows;
    }

    private static string OpLabel(string opType) => Localizer.T(opType switch
    {
        DeviceOperationTypes.Reboot => "DeviceTools.OpReboot",
        DeviceOperationTypes.FirmwareUpgrade => "DeviceTools.OpFirmware",
        DeviceOperationTypes.Diagnostics => "DeviceTools.OpDiagnostics",
        DeviceOperationTypes.AvTest => "DeviceTools.OpAvTest",
        _ => "DeviceTools.OpOther",
    });

    private static string ResultLabel(string result) => Localizer.T(
        result == DeviceOperationResults.Ok ? "DeviceTools.ResultOk" : "DeviceTools.ResultFailed");

    /// <summary>命令期間停用按鈕並顯示「執行中」，避免同一命令被連按兩次。</summary>
    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetCommandsEnabled(false);
        StatusText.Text = Localizer.T("DeviceTools.StatusRunning");
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            if (_device is not null)
            {
                SetCommandsEnabled(true);
            }
        }
    }

    private void SetCommandsEnabled(bool enabled)
    {
        DiagnosticsButton.IsEnabled = enabled;
        AvTestButton.IsEnabled = enabled;
        RebootButton.IsEnabled = enabled;
        FirmwareButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    private static string Actor() => SessionContext.CurrentUser?.Username ?? "system";

    /// <summary>操作歷史一列（供清單繫結）。</summary>
    private sealed record HistoryRow(string Time, string Op, string Result, string Detail);
}
