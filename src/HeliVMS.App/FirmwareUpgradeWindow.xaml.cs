using System.ComponentModel;
using System.IO;
using System.Windows;
using HeliVMS.Devices.Onvif;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Win32;

namespace HeliVMS.App;

/// <summary>
/// 批次韌體升級（§7.4）：一次對多台攝影機「檢查版本 → 推送映像 → （可選）重啟」。
///
/// <para>
/// 編排邏輯在 <see cref="FirmwareUpgradeBatch"/>（Devices，可單元測試）；這支視窗只負責
/// 挑設備、挑檔案、把結果回填到清單並逐台寫入 <c>device_operations</c>。升級是破壞性且
/// 不可逆的命令，因此需要管理員且有明確確認（確認框會列出檔名與台數）。
/// </para>
///
/// <para>
/// 「檢查版本」是唯讀的，任何角色都可執行，方便非管理員先盤點現況。
/// </para>
/// </summary>
public partial class FirmwareUpgradeWindow : Window
{
    private readonly DeviceRepository _devices;
    private readonly DeviceOperationRepository _operations;
    private readonly List<FirmwareRow> _rows = [];
    private bool _busy;
    private string? _firmwarePath;

    public FirmwareUpgradeWindow(SqliteStore store)
    {
        var audit = new AuditLogRepository(store);
        _devices = new DeviceRepository(store, audit);
        _operations = new DeviceOperationRepository(store, audit);

        InitializeComponent();
        ApplyI18n();
        Reload();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Firmware.Title");
        FirmwareHeader.Text = Localizer.T("Firmware.Header");
        FirmwareDesc.Text = Localizer.T("Firmware.Desc");
        FileLabel.Text = Localizer.T("Firmware.File");
        PickFirmwareButton.Content = Localizer.T("Firmware.Pick");
        PickFirmwareButton.ToolTip = Localizer.T("Firmware.PickTip");
        CheckButton.Content = Localizer.T("Firmware.Check");
        CheckButton.ToolTip = Localizer.T("Firmware.CheckTip");
        UpgradeButton.Content = Localizer.T("Firmware.Upgrade");
        UpgradeButton.ToolTip = Localizer.T("Firmware.UpgradeTip");
        RebootAfterCheck.Content = Localizer.T("Firmware.RebootAfter");
        RebootAfterCheck.ToolTip = Localizer.T("Firmware.RebootAfterTip");
        RefreshButton.Content = Localizer.T("Firmware.Refresh");
        RefreshButton.ToolTip = Localizer.T("Firmware.RefreshTip");
        CloseButton.Content = Localizer.T("Firmware.Close");
        CloseButton.ToolTip = Localizer.T("Firmware.CloseTip");
        ColSelect.Header = Localizer.T("Firmware.ColSelect");
        ColName.Header = Localizer.T("Firmware.ColName");
        ColAddress.Header = Localizer.T("Firmware.ColAddress");
        ColVersion.Header = Localizer.T("Firmware.ColVersion");
        ColResult.Header = Localizer.T("Firmware.ColResult");
        StatusText.Text = Localizer.T("Firmware.StatusIdle");
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var device in _devices.List().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new FirmwareRow
            {
                DeviceId = device.Id,
                Name = device.Name,
                Address = $"{device.Ip}:{device.Port}",
                // 只預勾啟用中的設備——停用的設備通常是停機或替換中，不該被無聲刷機。
                IsSelected = device.Enabled,
                Version = string.Empty,
                Result = string.Empty,
            });
        }

        DeviceList.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            StatusText.Text = Localizer.T("Firmware.NoDevices");
            SetCommandsEnabled(false);
        }
        else
        {
            StatusText.Text = Localizer.T("Firmware.StatusIdle");
            SetCommandsEnabled(true);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Reload();

    private void OnPickFirmwareClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.T("Firmware.PickTitle"),
            Filter = Localizer.T("Firmware.PickFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _firmwarePath = dialog.FileName;
        FileText.Text = dialog.FileName;
        StatusText.Text = string.Format(
            Localizer.T("Firmware.FilePicked"), Path.GetFileName(dialog.FileName));
    }

    /// <summary>批次檢查版本（唯讀）。</summary>
    private async void OnCheckClicked(object sender, RoutedEventArgs e)
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = Localizer.T("Firmware.NoSelection");
            return;
        }

        await RunExclusiveAsync(async () =>
        {
            try
            {
                var batch = new FirmwareUpgradeBatch();
                var progress = new Progress<FirmwareCheckResult>(r => ApplyCheck(r));
                var results = await batch.CheckAsync(targets, progress);

                var ok = results.Count(r => r.Success);
                StatusText.Text = string.Format(Localizer.T("Firmware.CheckDone"), ok, results.Count);
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(Localizer.T("Firmware.Failed"), ex.Message);
            }
        });
    }

    /// <summary>批次升級（破壞性、不可逆）：管理員＋明確確認。</summary>
    private async void OnUpgradeClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            StatusText.Text = Localizer.T("Firmware.AdminOnly");
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = Localizer.T("Firmware.NoSelection");
            return;
        }

        if (string.IsNullOrEmpty(_firmwarePath))
        {
            StatusText.Text = Localizer.T("Firmware.NoFile");
            return;
        }

        var fileName = Path.GetFileName(_firmwarePath);
        var confirmed = MessageBox.Show(
            this,
            string.Format(Localizer.T("Firmware.Confirm"), targets.Count, fileName),
            Localizer.T("Firmware.Upgrade"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        byte[] firmware;
        try
        {
            firmware = await Task.Run(() => File.ReadAllBytes(_firmwarePath!));
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Localizer.T("Firmware.Failed"), ex.Message);
            return;
        }

        var rebootAfter = RebootAfterCheck.IsChecked == true;
        var actor = Actor();

        await RunExclusiveAsync(async () =>
        {
            try
            {
                var batch = new FirmwareUpgradeBatch();
                var progress = new Progress<FirmwareUpgradeResult>(r => ApplyUpgrade(r));
                var results = await batch.UpgradeAsync(targets, firmware, rebootAfter, progress);

                // 逐台寫入操作歷史：失敗也照記，這是「哪幾台刷失敗了」的唯一軌跡。
                foreach (var result in results)
                {
                    var detail = string.Format(
                        Localizer.T("Firmware.RecordDetail"), fileName, result.PreviousVersion);
                    if (!result.Success)
                    {
                        detail += $" — {result.Error}";
                    }

                    Record(result.Target.DeviceId, result.Success, detail, actor);
                }

                var ok = results.Count(r => r.Success);
                var summary = string.Format(
                    Localizer.T("Firmware.Done"), ok, results.Count - ok, results.Count);
                if (!rebootAfter)
                {
                    summary += " " + Localizer.T("Firmware.RebootHint");
                }

                StatusText.Text = summary;
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(Localizer.T("Firmware.Failed"), ex.Message);
            }
        });
    }

    private void ApplyCheck(FirmwareCheckResult result)
    {
        var row = Row(result.Target.DeviceId);
        if (row is null)
        {
            return;
        }

        row.Version = result.Success
            ? result.Version
            : Localizer.T("Firmware.VersionUnknown");
        row.Result = result.Success
            ? Localizer.T("Firmware.ResultOk")
            : string.Format(Localizer.T("Firmware.ResultFailed"), result.Error);
    }

    private void ApplyUpgrade(FirmwareUpgradeResult result)
    {
        var row = Row(result.Target.DeviceId);
        if (row is null)
        {
            return;
        }

        if (result.Success && !string.IsNullOrEmpty(result.PreviousVersion))
        {
            row.Version = result.PreviousVersion;
        }

        row.Result = result.Success
            ? Localizer.T("Firmware.ResultUpgraded")
            : string.Format(Localizer.T("Firmware.ResultFailed"), result.Error);
    }

    private FirmwareRow? Row(int deviceId) =>
        _rows.FirstOrDefault(r => r.DeviceId == deviceId);

    private List<FirmwareUpgradeTarget> SelectedTargets()
    {
        var targets = new List<FirmwareUpgradeTarget>();
        foreach (var row in _rows)
        {
            if (!row.IsSelected)
            {
                continue;
            }

            var device = _devices.Get(row.DeviceId);
            if (device is null)
            {
                // 設備可能已被刪除；略過，不對不存在的設備下命令。
                continue;
            }

            var password = string.IsNullOrEmpty(device.PasswordEncrypted)
                ? null
                : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);

            targets.Add(new FirmwareUpgradeTarget(
                device.Id,
                device.Name,
                $"http://{device.Ip}:{device.Port}/onvif/device_service",
                device.Username,
                password));
        }

        return targets;
    }

    private void Record(int deviceId, bool success, string? detail, string actor)
    {
        try
        {
            _operations.Record(
                deviceId,
                DeviceOperationTypes.FirmwareUpgrade,
                null,
                success ? DeviceOperationResults.Ok : DeviceOperationResults.Failed,
                detail,
                actor);
        }
        catch (Exception)
        {
            // 紀錄失敗不應蓋掉命令本身的結果（命令可能已送達設備）。
        }
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetCommandsEnabled(false);
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            if (_rows.Count > 0)
            {
                SetCommandsEnabled(true);
            }
        }
    }

    private void SetCommandsEnabled(bool enabled)
    {
        CheckButton.IsEnabled = enabled;
        UpgradeButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        // 韌體檔選擇在執行期間仍可更換不影響進行中的批次，故不列入停用。
    }

    private static string Actor() => SessionContext.CurrentUser?.Username ?? "system";

    /// <summary>設備列（供清單繫結與執行期回填）。</summary>
    private sealed class FirmwareRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _version = string.Empty;
        private string _result = string.Empty;

        public int DeviceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Address { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string Version
        {
            get => _version;
            set
            {
                _version = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
            }
        }

        public string Result
        {
            get => _result;
            set
            {
                _result = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
