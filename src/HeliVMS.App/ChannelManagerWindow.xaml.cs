using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 頻道管理（與「攝影機管理」分離）。
///
/// <para>
/// 頻道是監看／錄影的來源；攝影機（devices）是可重複使用的碼流來源。
/// 分成兩個視窗後，監控中心（<see cref="MainWindow"/>）維持純監看、不再顯示串流位址，
/// 也避免操作員在監看畫面誤刪頻道（channels 為 CASCADE 父表，誤刪會連帶清除錄影索引與事件）。
/// </para>
///
/// <para>
/// 契約測試 <c>ChannelAdminContractTests</c> 掃描本檔的 <c>ChannelList</c>、
/// <c>ApplyChannelEditButton</c>、<c>DeleteChannelButton</c> 接線與 <c>ReloadChannels</c> 的清空行為，
/// 改名或改接線前務必同步更新該測試。
/// </para>
/// </summary>
public partial class ChannelManagerWindow : Window
{
    private sealed record ChannelRow(
        int Id,
        string Name,
        string DeviceLabel,
        string MainStreamUrl,
        string RecordingModeLabel,
        string MotionLabel,
        string CellLabel);

    private sealed record SourceDevice(int Id, string Name);

    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly LicenseService _license;
    private readonly Action? _channelsChanged;
    private int? _channelEditId;
    private bool _channelDeleteArm;

    public ChannelManagerWindow(SqliteStore store, Action? channelsChanged = null)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _devices = new DeviceRepository(store, new AuditLogRepository(store));
        // 顯示格位選項依授權路數產生，故唯讀評估目前授權（不寫 DB、不記稽核）。
        _license = new LicenseService(store);
        // 頻道異動後通知主視窗立即重載（否則監看區會停在舊畫面／繼續串流已刪除的頻道）。
        _channelsChanged = channelsChanged;

        InitializeComponent();
        ApplyI18n();
        ReloadDevices();
        ReloadChannels();
    }

    /// <summary>
    /// 頻道新增／修改／刪除後呼叫，請主視窗重載清單並重新連線。
    /// 缺少這個通知時，監看區只會在「設定中心」關閉後才更新（見 <c>MainWindow.ReloadChannelsIfChangedAsync</c>），
    /// 刪除頻道期間監控中心會一直殘留已刪除攝影機的畫面。
    /// </summary>
    private void NotifyChannelsChanged() => _channelsChanged?.Invoke();

    private void ApplyI18n()
    {
        Title = Localizer.T("Channels.Title");
        ChannelHeader.Text = Localizer.T("Channels.Header");
        ChannelDesc.Text = Localizer.T("Channels.Desc");
        ChannelAddHeader.Text = Localizer.T("Channels.AddHeader");
        ChannelSourceLabel.Text = Localizer.T("Channels.SourceLabel");
        DeviceSourceCombo.ToolTip = Localizer.T("Channels.SourceTip");
        OnvifWizardButton.Content = Localizer.T("Channels.OnvifWizard");
        OnvifWizardButton.ToolTip = Localizer.T("Channels.OnvifWizardTip");
        RefreshChannelsButton.Content = Localizer.T("Channels.Refresh");
        RefreshChannelsButton.ToolTip = Localizer.T("Channels.RefreshTip");
        ChannelNameLabel.Text = Localizer.T("Channels.NameLabel");
        AddChannelNameBox.ToolTip = Localizer.T("Channels.NameTip");
        ChannelMainLabel.Text = Localizer.T("Channels.MainLabel");
        AddChannelUrlBox.ToolTip = Localizer.T("Channels.MainTip");
        ChannelSubLabel.Text = Localizer.T("Channels.SubLabel");
        AddChannelSubUrlBox.ToolTip = Localizer.T("Channels.SubTip");
        AddChannelButton.Content = Localizer.T("Channels.Add");
        AddChannelButton.ToolTip = Localizer.T("Channels.AddTip");
        ChannelColName.Header = Localizer.T("Channels.ColName");
        ChannelColDevice.Header = Localizer.T("Channels.ColDevice");
        ChannelColMain.Header = Localizer.T("Channels.ColMain");
        ChannelColRecording.Header = Localizer.T("Channels.ColRecording");
        ChannelColMotion.Header = Localizer.T("Channels.ColMotion");
        ChannelColCell.Header = Localizer.T("Channels.ColCell");
        ChannelEditHeader.Text = Localizer.T("Channels.EditHeader");
        ChannelEditHintText.Text = Localizer.T("Channels.EditHint");
        ChannelEditNameLabel.Text = Localizer.T("Channels.NameLabel");
        ChannelEditNameBox.ToolTip = Localizer.T("Channels.NameTip");
        ChannelEditMainLabel.Text = Localizer.T("Channels.MainLabel");
        ChannelEditMainUrlBox.ToolTip = Localizer.T("Channels.MainTip");
        ChannelEditSubLabel.Text = Localizer.T("Channels.SubLabel");
        ChannelEditSubUrlBox.ToolTip = Localizer.T("Channels.SubTip");
        ChannelEditRecordingLabel.Text = Localizer.T("Channels.EditRecordingLabel");
        ChannelEditModeCombo.ToolTip = Localizer.T("Channels.EditRecordingTip");
        var modeKeys = new[] { "Channels.ModeAlways", "Channels.ModeSchedule", "Channels.ModeEvent", "Channels.ModeManual" };
        for (var i = 0; i < ChannelEditModeCombo.Items.Count && i < modeKeys.Length; i++)
        {
            if (ChannelEditModeCombo.Items[i] is ComboBoxItem item)
            {
                item.Content = Localizer.T(modeKeys[i]);
            }
        }

        ChannelEditMotionCheck.Content = Localizer.T("Channels.MotionCheck");
        ChannelEditSensitivityLabel.Text = Localizer.T("Channels.SensitivityLabel");
        ChannelEditSensitivityBox.ToolTip = Localizer.T("Channels.SensitivityTip");
        ChannelEditCellLabel.Text = Localizer.T("Channels.EditCellLabel");
        ChannelEditCellCombo.ToolTip = Localizer.T("Channels.EditCellTip");
        ApplyChannelEditButton.Content = Localizer.T("Channels.Apply");
        ApplyChannelEditButton.ToolTip = Localizer.T("Channels.ApplyTip");
        DeleteChannelButton.Content = Localizer.T("Channels.Delete");
        DeleteChannelButton.ToolTip = Localizer.T("Channels.DeleteTip");
        CloseButton.Content = Localizer.T("Channels.Close");
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 重建「顯示格位」下拉：第一項「自動」，其後為 01..授權路數（封頂於監看區最大格數）。
    /// </summary>
    /// <param name="displayCell">目前選取頻道的格位（0 基；null＝自動）。</param>
    /// <remarks>
    /// 選項隨授權路數而變（匯入新授權後可能增減），故每次載入頻道都重建，不能只在建構時建一次。
    /// </remarks>
    private void PopulateCellOptions(int? displayCell)
    {
        ChannelEditCellCombo.Items.Clear();
        ChannelEditCellCombo.Items.Add(new ComboBoxItem { Content = Localizer.T("Channels.CellAuto") });

        var selectable = ChannelLayout.SelectableCells(_license.Evaluate(DateTime.UtcNow).MaxCameras);
        var target = displayCell is int cell ? cell + 1 : 0;

        // 一個格子只能給一台攝影機：已被其他頻道佔用的格位反灰不可選（編輯自己時排除自己）。
        var taken = ChannelLayout.TakenCells(
            _channels.List().Select(c => (c.Id, c.DisplayCell)),
            _channelEditId);

        for (var n = 1; n <= selectable; n++)
        {
            AddCellOption(n, selected: n == target, busy: taken.Contains(n - 1) && n != target);
        }

        // 授權縮水或手改 DB 時，既有格位可能超出可選範圍；仍列為一個選項，
        // 否則它會被顯示成「自動」，使用者一按套用就把原本的設定悄悄清掉了。
        if (target > selectable)
        {
            AddCellOption(target, selected: true, busy: false);
        }

        if (target == 0)
        {
            ChannelEditCellCombo.SelectedIndex = 0;
        }
    }

    private void AddCellOption(int value, bool selected, bool busy)
    {
        var label = value.ToString("00", CultureInfo.InvariantCulture);
        var item = new ComboBoxItem
        {
            // 反灰的項目仍標「已使用」，否則使用者只看到選項變灰、不知道原因。
            Content = busy ? label + Localizer.T("Channels.CellTaken") : label,
            Tag = value,
            IsEnabled = !busy,
        };
        ChannelEditCellCombo.Items.Add(item);
        if (selected)
        {
            ChannelEditCellCombo.SelectedItem = item;
        }
    }

    /// <summary>填入「來源攝影機」下拉：第一項為手動輸入，其餘為已加入攝影機（Tag＝設備 Id）。</summary>
    private void ReloadDevices()
    {
        DeviceSourceCombo.Items.Clear();
        DeviceSourceCombo.Items.Add(new ComboBoxItem { Content = Localizer.T("Channels.ManualItem") });

        foreach (var device in _devices.List().OrderBy(d => d.Id))
        {
            DeviceSourceCombo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        }

        DeviceSourceCombo.SelectedIndex = 0;
    }

    /// <summary>選到攝影機時把其主／次碼流帶入新增欄位；選回手動項則只清空預填。</summary>
    private void OnDeviceSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSourceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int deviceId)
        {
            return;
        }

        if (_devices.Get(deviceId) is not { } device)
        {
            return;
        }

        AddChannelUrlBox.Text = device.MainStreamUrl ?? string.Empty;
        AddChannelSubUrlBox.Text = device.SubStreamUrl ?? string.Empty;
        if (AddChannelNameBox.Text.Trim().Length == 0)
        {
            AddChannelNameBox.Text = device.Name;
        }
    }

    private void OnRefreshChannelsClicked(object sender, RoutedEventArgs e) => ReloadChannels();

    private void ReloadChannels(int? reselect = null)
    {
        var deviceNames = _devices.List().ToDictionary(d => d.Id, d => d.Name);

        var rows = _channels.List()
            .Select(c => new ChannelRow(
                c.Id,
                c.Name,
                c.DeviceId is int deviceId && deviceNames.TryGetValue(deviceId, out var deviceName)
                    ? deviceName
                    : Localizer.T("Channels.Unbound"),
                c.MainStreamUrl,
                c.RecordingMode.ToString(),
                c.MotionEnabled ? Localizer.T("Channels.MotionOn") : Localizer.T("Channels.MotionOff"),
                c.DisplayCell is int cell
                    ? string.Format(Localizer.T("Channels.CellLabel"), cell + 1)
                    : Localizer.T("Channels.CellAuto")))
            .ToList();
        ChannelList.ItemsSource = rows;

        // 重新載入等於捨棄目前選取；編輯欄位若不清空，會讓人以為改的是清單第一列。
        _channelEditId = null;
        _channelDeleteArm = false;
        DeleteChannelButton.Content = Localizer.T("Channels.Delete");
        ChannelEditNameBox.Text = string.Empty;
        ChannelEditMainUrlBox.Text = string.Empty;
        ChannelEditSubUrlBox.Text = string.Empty;
        ChannelEditModeCombo.SelectedIndex = 0;
        ChannelEditMotionCheck.IsChecked = false;
        ChannelEditSensitivityBox.Text = string.Empty;
        PopulateCellOptions(null);
        ChannelEditHintText.Text = Localizer.T("Channels.EditHint");

        if (reselect is int target)
        {
            var match = rows.FirstOrDefault(r => r.Id == target);
            if (match is not null)
            {
                ChannelList.SelectedItem = match;
            }
        }
    }

    private void OnChannelSelected(object sender, SelectionChangedEventArgs e)
    {
        _channelDeleteArm = false;
        DeleteChannelButton.Content = Localizer.T("Channels.Delete");

        if (ChannelList.SelectedItem is not ChannelRow row)
        {
            _channelEditId = null;
            return;
        }

        var channel = _channels.Get(row.Id);
        if (channel is null)
        {
            _channelEditId = null;
            return;
        }

        _channelEditId = channel.Id;
        ChannelEditNameBox.Text = channel.Name;
        ChannelEditMainUrlBox.Text = channel.MainStreamUrl;
        ChannelEditSubUrlBox.Text = channel.SubStreamUrl;
        ChannelEditModeCombo.SelectedIndex = (int)channel.RecordingMode;
        ChannelEditMotionCheck.IsChecked = channel.MotionEnabled;
        ChannelEditSensitivityBox.Text = channel.MotionSensitivity.ToString("0.##", CultureInfo.InvariantCulture);
        PopulateCellOptions(channel.DisplayCell);
        ChannelEditHintText.Text = string.Format(Localizer.T("Channels.Editing"), channel.Name, channel.Id);
    }

    private void OnApplyChannelEditClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.AdminOnly");
            return;
        }

        if (_channelEditId is not int id)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.SelectFirst");
            return;
        }

        var name = ChannelEditNameBox.Text.Trim();
        var main = ChannelEditMainUrlBox.Text.Trim();
        if (name.Length == 0 || main.Length == 0)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.NameMainRequired");
            return;
        }

        var sensitivity = 0.5;
        var sensitivityText = ChannelEditSensitivityBox.Text.Trim();
        if (sensitivityText.Length > 0
            && !double.TryParse(sensitivityText, NumberStyles.Float, CultureInfo.InvariantCulture, out sensitivity))
        {
            ChannelEditHintText.Text = Localizer.T("Channels.SensitivityRange");
            return;
        }

        // 顯示格位：未選＝自動（依建立順序），否則為下拉對應的 1 基格號。
        // 選項本已依授權路數產生，仍守一次上限，避免自 DB 保留的超界值被寫回。
        int? displayCell = null;
        if (ChannelEditCellCombo.SelectedItem is ComboBoxItem { Tag: int cellNumber })
        {
            if (cellNumber < 1 || cellNumber > ChannelLayout.MaxCells)
            {
                ChannelEditHintText.Text = string.Format(Localizer.T("Channels.CellRange"), ChannelLayout.MaxCells);
                return;
            }

            displayCell = cellNumber - 1;
        }

        // 指定／變更監看格位時，讓「自動命名」的頻道名稱跟著該格號（自訂名稱不動），
        // 否則把頻道排到別的格子後名稱會與實際位置對不上。
        if (displayCell is int assignedCell)
        {
            name = NameForAssignedCell(name, assignedCell);
        }

        var current = _channels.Get(id);
        if (current is null)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.Gone");
            return;
        }

        try
        {
            // Update 覆寫整列，未編輯的欄位（編碼／音訊／裝置綁定）必須原值帶回，否則會被清成預設。
            _channels.Update(new ChannelInfo
            {
                Id = id,
                DeviceId = current.DeviceId,
                Name = name,
                MainStreamUrl = main,
                SubStreamUrl = ChannelEditSubUrlBox.Text.Trim(),
                Codec = current.Codec,
                AudioEnabled = current.AudioEnabled,
                AudioEncoder = current.AudioEncoder,
                MotionEnabled = ChannelEditMotionCheck.IsChecked == true,
                MotionSensitivity = Math.Clamp(sensitivity, 0, 1),
                RecordingMode = (RecordingMode)Math.Clamp(ChannelEditModeCombo.SelectedIndex, 0, 3),
                Enabled = current.Enabled,
                DisplayCell = displayCell,
            });

            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.Updated"), name);
            ReloadChannels(id);
            NotifyChannelsChanged();
        }
        catch (Exception ex)
        {
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.UpdateFailed"), ex.Message);
        }
    }

    private void OnDeleteChannelClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.AdminOnly");
            return;
        }

        if (_channelEditId is not int id || ChannelList.SelectedItem is not ChannelRow row)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.SelectFirst");
            return;
        }

        if (!_channelDeleteArm)
        {
            _channelDeleteArm = true;
            DeleteChannelButton.Content = Localizer.T("Channels.ConfirmDelete");
            ChannelEditHintText.Text =
                string.Format(Localizer.T("Channels.DeleteConfirm"), row.Name, id);
            return;
        }

        try
        {
            _channels.Delete(id);
            _channelDeleteArm = false;
            DeleteChannelButton.Content = Localizer.T("Channels.Delete");
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.Deleted"), row.Name, id);
            ReloadChannels();
            NotifyChannelsChanged();
        }
        catch (Exception ex)
        {
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.DeleteFailed"), ex.Message);
        }
    }

    private void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.AdminOnly");
            return;
        }

        var url = AddChannelUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.UrlRequired");
            return;
        }

        var subUrl = AddChannelSubUrlBox.Text.Trim();

        // 這台新頻道會落在第幾格：用與監看牆相同的補位規則算出，作為預設名稱與監看格位。
        var cell = ChannelLayout.NextFreeCell(_channels.List().Select(c => c.DisplayCell).ToList());

        var name = AddChannelNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = DefaultChannelNameForCell(cell);
        }

        // 從「來源攝影機」建立時綁定 device_id：PTZ／RTSP 憑證都靠這個關聯共用，
        // 純手動輸入則維持未綁定。
        var deviceId = DeviceSourceCombo.SelectedItem is ComboBoxItem { Tag: int selectedDeviceId }
            ? selectedDeviceId
            : (int?)null;

        try
        {
            // 一併寫入 display_cell：名稱「攝影機NN」對應的格子就此定住，
            // 之後刪掉別的頻道也不會整體往前擠，導致名稱與實際格子對不上。
            var id = _channels.Add(name, url, subUrl.Length == 0 ? null : subUrl, deviceId, displayCell: cell);
            AddChannelUrlBox.Text = string.Empty;
            AddChannelSubUrlBox.Text = string.Empty;
            AddChannelNameBox.Text = string.Empty;
            DeviceSourceCombo.SelectedIndex = 0;
            ReloadChannels(id);
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.Added"), name, id);
            NotifyChannelsChanged();
        }
        catch (Exception ex)
        {
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.AddFailed"), ex.Message);
        }
    }

    /// <summary>
    /// 未輸入名稱時的預設「攝影機01、攝影機02…」：序號取這台頻道將佔用的監看格位（0 基 +1）。
    /// </summary>
    /// <remarks>
    /// 名稱直接對應監看牆上的格子，符合「攝影機名稱依分配到的格子命名」。
    /// 若算出的名稱已被別的頻道佔用（例如曾手動改名成「攝影機03」而它其實不在第 3 格），
    /// 退回取第一個未使用的序號（<see cref="ChannelNaming.NextDefault"/>），避免出現同名頻道。
    /// </remarks>
    private string DefaultChannelNameForCell(int cell)
    {
        var format = Localizer.T("Channels.DefaultName");
        var candidate = string.Format(format, cell + 1);
        var existing = _channels.List().Select(c => c.Name).ToList();
        return existing.Contains(candidate, StringComparer.Ordinal)
            ? ChannelNaming.NextDefault(existing, format)
            : candidate;
    }

    /// <summary>
    /// 指定監看格位後，讓「自動命名」的頻道名稱跟著該格號（攝影機{格號+1}）。
    /// </summary>
    /// <remarks>
    /// 只改自動命名的名稱；操作員自取的名稱（例如「大門」）原樣保留（見 <see cref="ChannelNaming.MatchesDefault"/>）。
    /// 撞名（例如別台已叫「攝影機03」）時退回第一個未使用序號，避免監看牆出現同名頻道。
    /// </remarks>
    private string NameForAssignedCell(string currentName, int cell)
    {
        var format = Localizer.T("Channels.DefaultName");
        if (!ChannelNaming.MatchesDefault(currentName, format))
        {
            return currentName;
        }

        var candidate = string.Format(format, cell + 1);
        var others = _channels.List()
            .Where(c => c.Id != _channelEditId)
            .Select(c => c.Name)
            .ToList();
        return others.Contains(candidate, StringComparer.Ordinal)
            ? ChannelNaming.NextDefault(others, format)
            : candidate;
    }

    private void OnOnvifWizardClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            ChannelEditHintText.Text = Localizer.T("Channels.AdminOnly");
            return;
        }

        var wizard = new OnvifWizardWindow
        {
            Owner = this,
        };

        if (wizard.ShowDialog() != true || wizard.StreamUrl.Length == 0)
        {
            return;
        }

        try
        {
            var id = ChannelEnrollment.Enroll(
                _store,
                wizard.ChannelName,
                wizard.StreamUrl,
                wizard.DeviceIp,
                wizard.DevicePort,
                wizard.DeviceUsername,
                wizard.DevicePassword,
                wizard.SubStreamUrl);
            ReloadDevices();
            ReloadChannels(id);
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.Enrolled"), wizard.ChannelName, id);
            NotifyChannelsChanged();
        }
        catch (Exception ex)
        {
            ChannelEditHintText.Text = string.Format(Localizer.T("Channels.AddFailed"), ex.Message);
        }
    }
}
