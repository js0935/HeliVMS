using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HeliVMS.App.Services;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 攝影機管理（devices 表）：ONVIF 探索、憑證與主／次碼流的維護。
///
/// <para>
/// 與「頻道管理」分離：攝影機是可重複使用的設備來源，頻道是監看／錄影的實際來源。
/// 加入攝影機後由 <see cref="ChannelManagerWindow"/> 挑選其碼流建立頻道。
/// 主／次碼流保存在 devices 表，監控中心（<see cref="MainWindow"/>）不顯示位址。
/// </para>
///
/// <para>
/// devices.ip 為 UNIQUE；重複加入同一 IP 會沿用既有記錄並就地修正憑證，不另建一筆，
/// 否則會出現兩台「同一台攝影機」而憑證不一致。
/// </para>
/// </summary>
public partial class CameraManagerWindow : Window
{
    /// <summary>裝置連線狀態；顏色與文字都由此推導，不依賴當地語系化的字串比對。</summary>
    private enum CameraStatus
    {
        Online,
        Connecting,
        Offline,
        NoChannel,
    }

    /// <summary>
    /// 清單一列。狀態欄會隨連線變化即時更新，故為可變類別並在 <see cref="Status"/> 變更時通知，
    /// 避免重建 <c>ItemsSource</c> 而清掉選取與正在編輯的欄位。
    /// </summary>
    /// <remarks>
    /// <see cref="StatusText"/> 與 <see cref="StatusBrush"/> 都是衍生值，狀態一變就一起通知，
    /// 讓清單的顏色跟著狀態換（綠＝已連線、橘＝連線中、紅＝離線）而不必在 XAML 比對翻譯字串。
    /// </remarks>
    private sealed class DeviceRow : INotifyPropertyChanged
    {
        private CameraStatus _status;

        public DeviceRow(
            int id,
            string name,
            string ipPort,
            string vendor,
            string credentialLabel,
            string mainStreamUrl,
            CameraStatus status)
        {
            Id = id;
            Name = name;
            IpPort = ipPort;
            Vendor = vendor;
            CredentialLabel = credentialLabel;
            MainStreamUrl = mainStreamUrl;
            _status = status;
        }

        public int Id { get; }

        public string Name { get; }

        public string IpPort { get; }

        public string Vendor { get; }

        public string CredentialLabel { get; }

        public string MainStreamUrl { get; }

        public CameraStatus Status
        {
            get => _status;
            set
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
            }
        }

        public string StatusText => Localizer.T(_status switch
        {
            CameraStatus.Online => "Camera.StatusOnline",
            CameraStatus.Connecting => "Camera.StatusConnecting",
            CameraStatus.NoChannel => "Camera.StatusNoChannel",
            _ => "Camera.StatusOffline",
        });

        public Brush StatusBrush => _status switch
        {
            CameraStatus.Online => s_onlineBrush,
            CameraStatus.Connecting => s_connectingBrush,
            CameraStatus.NoChannel => s_noChannelBrush,
            _ => s_offlineBrush,
        };

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly Brush s_onlineBrush = MakeBrush(0x3F, 0xB9, 0x50);
    private static readonly Brush s_connectingBrush = MakeBrush(0xE8, 0xA3, 0x3D);
    private static readonly Brush s_offlineBrush = MakeBrush(0xE5, 0x53, 0x4B);
    private static readonly Brush s_noChannelBrush = MakeBrush(0x8A, 0x97, 0xA8);

    /// <summary>建立並凍結具名色的筆刷；凍結後可跨執行緒共用且不可被誤改。</summary>
    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly SqliteStore _store;
    private readonly DeviceRepository _devices;
    private readonly ChannelRepository _channels;
    private readonly ChannelManager? _manager;
    private readonly Action? _channelsChanged;
    private readonly DispatcherTimer _statusTimer;
    private int? _deviceEditId;
    private bool _deviceDeleteArm;

    public CameraManagerWindow(SqliteStore store, ChannelManager? manager = null, Action? channelsChanged = null)
    {
        _store = store;
        _devices = new DeviceRepository(store, new AuditLogRepository(store));
        _channels = new ChannelRepository(store);
        _manager = manager;
        // 刪除設備會 CASCADE 刪掉其頻道，故刪除後同樣要請主視窗重載監看區。
        _channelsChanged = channelsChanged;

        InitializeComponent();
        ApplyI18n();
        ReloadDevices();

        // 狀態取自 <see cref="ChannelManager"/> 的工作階段（監控中心連線後才有），
        // 以 2 秒輪詢更新；連線／斷線不會經過本視窗的任何事件。
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _statusTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Camera.Title");
        CameraHeader.Text = Localizer.T("Camera.Header");
        CameraDesc.Text = Localizer.T("Camera.Desc");
        OnvifWizardButton.Content = Localizer.T("Camera.OnvifWizard");
        OnvifWizardButton.ToolTip = Localizer.T("Camera.OnvifWizardTip");
        RefreshDevicesButton.Content = Localizer.T("Camera.Refresh");
        RefreshDevicesButton.ToolTip = Localizer.T("Camera.RefreshTip");
        CloseButton.Content = Localizer.T("Camera.Close");
        CameraColName.Header = Localizer.T("Camera.ColName");
        CameraColAddress.Header = Localizer.T("Camera.ColAddress");
        CameraColVendor.Header = Localizer.T("Camera.ColVendor");
        CameraColCredential.Header = Localizer.T("Camera.ColCredential");
        CameraColMainStream.Header = Localizer.T("Camera.ColMainStream");
        CameraColStatus.Header = Localizer.T("Camera.ColStatus");
        CameraSettingsHeader.Text = Localizer.T("Camera.SettingsHeader");
        DeviceHintText.Text = Localizer.T("Camera.DeviceHint");
        CameraNameLabel.Text = Localizer.T("Camera.NameLabel");
        DeviceNameBox.ToolTip = Localizer.T("Camera.NameTip");
        CameraPortLabel.Text = Localizer.T("Camera.Port");
        DevicePortBox.ToolTip = Localizer.T("Camera.PortTip");
        CameraUsernameLabel.Text = Localizer.T("Camera.UsernameLabel");
        DeviceUsernameBox.ToolTip = Localizer.T("Camera.UsernameTip");
        CameraPasswordLabel.Text = Localizer.T("Camera.PasswordLabel");
        DevicePasswordBox.ToolTip = Localizer.T("Camera.PasswordTip");
        CameraMainLabel.Text = Localizer.T("Camera.MainLabel");
        DeviceMainUrlBox.ToolTip = Localizer.T("Camera.MainTip");
        CameraSubLabel.Text = Localizer.T("Camera.SubLabel");
        DeviceSubUrlBox.ToolTip = Localizer.T("Camera.SubTip");
        SaveDeviceButton.Content = Localizer.T("Camera.Save");
        SaveDeviceButton.ToolTip = Localizer.T("Camera.SaveTip");
        DeleteDeviceButton.Content = Localizer.T("Camera.Delete");
        DeleteDeviceButton.ToolTip = Localizer.T("Camera.DeleteTip");
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e) => ReloadDevices();

    private void ReloadDevices(int? reselect = null)
    {
        var rows = _devices.List()
            .Select(d =>
            {
                var cred = _devices.GetRtspCredentials(d.Id);
                var label = string.IsNullOrWhiteSpace(cred.Username)
                    ? Localizer.T("Camera.NoCred")
                    : cred.Username + (string.IsNullOrEmpty(cred.Password)
                        ? Localizer.T("Camera.NoPasswordSuffix")
                        : Localizer.T("Camera.HasPasswordSuffix"));
                return new DeviceRow(
                    d.Id, d.Name, $"{d.Ip}:{d.Port}", d.Vendor, label, d.MainStreamUrl ?? string.Empty,
                    ComputeStatus(d.Id));
            })
            .ToList();

        DeviceList.ItemsSource = rows;

        // 清空編輯狀態：否則清單重載後欄位仍顯示上一台的資料，儲存時會改錯台。
        _deviceEditId = null;
        _deviceDeleteArm = false;
        DeleteDeviceButton.Content = Localizer.T("Camera.Delete");
        DeviceNameBox.Text = string.Empty;
        DeviceIpBox.Text = string.Empty;
        DevicePortBox.Text = "80";
        DeviceUsernameBox.Text = string.Empty;
        DevicePasswordBox.Password = string.Empty;
        DeviceMainUrlBox.Text = string.Empty;
        DeviceSubUrlBox.Text = string.Empty;
        DeviceHintText.Text = Localizer.T("Camera.DeviceHint");

        if (reselect is int target)
        {
            var match = rows.FirstOrDefault(r => r.Id == target);
            if (match is not null)
            {
                DeviceList.SelectedItem = match;
            }
        }
    }

    /// <summary>
    /// 依裝置綁定的頻道工作階段推導連線狀態：任一頻道串流中＝已連線；
    /// 否則任一頻道連線／重連中＝連線中；否則＝離線；未綁定頻道＝未設定頻道。
    /// </summary>
    /// <remarks>
    /// 攝影機（devices）本身沒有連線，真正拉流的是它底下的頻道；監控中心開機自動連線全部
    /// 頻道後，這裡才會有工作階段可查。未開啟監控或無 <see cref="ChannelManager"/> 時視為離線。
    /// 回傳列舉而非翻譯字串，才能讓顏色與文字各自衍生、不隨語系改變。
    /// </remarks>
    private CameraStatus ComputeStatus(int deviceId)
    {
        var channelIds = _channels.List()
            .Where(c => c.DeviceId == deviceId)
            .Select(c => c.Id)
            .ToList();

        if (channelIds.Count == 0)
        {
            return CameraStatus.NoChannel;
        }

        if (_manager is null)
        {
            return CameraStatus.Offline;
        }

        var states = channelIds
            .Select(id => _manager.ChannelSessionFor(id))
            .Where(s => s is { IsMonitoring: true })
            .Select(s => s!.Client.State)
            .ToList();

        if (states.Contains(RtspState.Streaming))
        {
            return CameraStatus.Online;
        }

        return states.Any(s => s is RtspState.Connecting or RtspState.Reconnecting)
            ? CameraStatus.Connecting
            : CameraStatus.Offline;
    }

    /// <summary>就地更新每列狀態；不重建 <c>ItemsSource</c>，保留選取與編輯中的欄位。</summary>
    private void RefreshStatuses()
    {
        if (DeviceList.ItemsSource is not IEnumerable<DeviceRow> rows)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.Status = ComputeStatus(row.Id);
        }
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        _deviceDeleteArm = false;
        DeleteDeviceButton.Content = Localizer.T("Camera.Delete");

        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            _deviceEditId = null;
            return;
        }

        var device = _devices.Get(row.Id);
        if (device is null)
        {
            _deviceEditId = null;
            return;
        }

        _deviceEditId = device.Id;
        DeviceNameBox.Text = device.Name;
        DeviceIpBox.Text = device.Ip;
        DevicePortBox.Text = device.Port.ToString();
        DeviceUsernameBox.Text = device.Username ?? string.Empty;
        DevicePasswordBox.Password = string.Empty;
        DeviceMainUrlBox.Text = device.MainStreamUrl ?? string.Empty;
        DeviceSubUrlBox.Text = device.SubStreamUrl ?? string.Empty;
        DeviceHintText.Text = string.Format(Localizer.T("Camera.Editing"), device.Name, device.Id);
    }

    private void OnSaveDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            DeviceHintText.Text = Localizer.T("Camera.AdminOnly");
            return;
        }

        var name = DeviceNameBox.Text.Trim();
        var ip = DeviceIpBox.Text.Trim();
        if (name.Length == 0 || ip.Length == 0)
        {
            DeviceHintText.Text = Localizer.T("Camera.NameIpRequired");
            return;
        }

        var portText = DevicePortBox.Text.Trim();
        var port = 80;
        if (portText.Length > 0
            && (!int.TryParse(portText, out port) || port <= 0 || port > 65535))
        {
            DeviceHintText.Text = Localizer.T("Camera.PortRange");
            return;
        }

        var main = DeviceMainUrlBox.Text.Trim();
        var sub = DeviceSubUrlBox.Text.Trim();
        var username = DeviceUsernameBox.Text.Trim();
        var password = DevicePasswordBox.Password;
        var actor = SessionContext.CurrentUser?.Username ?? "system";

        try
        {
            int id;
            if (_deviceEditId is int editId && _devices.Get(editId) is { } current)
            {
                _devices.Update(editId, name, ip, port, current.Vendor, current.Enabled, actor);

                // 與 API「僅覆寫有提供的欄位」一致：留空的欄位保留既有值，避免手誤清空憑證。
                if (username.Length > 0 || password.Length > 0)
                {
                    var existing = _devices.GetRtspCredentials(editId);
                    _devices.SetRtspCredentials(
                        editId,
                        username.Length > 0 ? username : existing.Username,
                        password.Length > 0 ? password : existing.Password,
                        actor);
                }

                _devices.SetStreamUrls(editId, main, sub, actor);
                id = editId;
            }
            else
            {
                id = _devices.Add(name, ip, port, username, password, "manual", actor, main, sub);
            }

            ReloadDevices(id);
            DeviceHintText.Text = string.Format(Localizer.T("Camera.Saved"), name, id);
        }
        catch (Exception ex)
        {
            DeviceHintText.Text = string.Format(Localizer.T("Camera.SaveFailed"), ex.Message);
        }
    }

    private void OnDeleteDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            DeviceHintText.Text = Localizer.T("Camera.AdminOnly");
            return;
        }

        if (_deviceEditId is not int id || DeviceList.SelectedItem is not DeviceRow row)
        {
            DeviceHintText.Text = Localizer.T("Camera.SelectFirst");
            return;
        }

        if (!_deviceDeleteArm)
        {
            _deviceDeleteArm = true;
            DeleteDeviceButton.Content = Localizer.T("Camera.ConfirmDelete");
            DeviceHintText.Text =
                string.Format(Localizer.T("Camera.DeleteConfirm"), row.Name, id);
            return;
        }

        try
        {
            _devices.Delete(id, SessionContext.CurrentUser?.Username ?? "system");
            _deviceDeleteArm = false;
            DeleteDeviceButton.Content = Localizer.T("Camera.Delete");
            ReloadDevices();
            DeviceHintText.Text = string.Format(Localizer.T("Camera.Deleted"), row.Name, id);
            // 該設備底下的頻道可能已被 CASCADE 刪除；通知主視窗重載，否則監看區會殘留其畫面。
            _channelsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DeviceHintText.Text = string.Format(Localizer.T("Camera.DeleteFailed"), ex.Message);
        }
    }

    /// <summary>ONVIF 探索：只加入攝影機（含主／次碼流），不建立頻道。</summary>
    private void OnOnvifWizardClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            DeviceHintText.Text = Localizer.T("Camera.AdminOnly");
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
            var id = ChannelEnrollment.EnrollDevice(
                _store,
                wizard.ChannelName,
                wizard.DeviceIp,
                wizard.DevicePort,
                wizard.DeviceUsername,
                wizard.DevicePassword,
                wizard.StreamUrl,
                wizard.SubStreamUrl);

            if (id == 0)
            {
                DeviceHintText.Text = Localizer.T("Camera.EnrollMissing");
                return;
            }

            ReloadDevices(id);
            DeviceHintText.Text = string.Format(Localizer.T("Camera.Enrolled"), wizard.ChannelName, id);
        }
        catch (Exception ex)
        {
            DeviceHintText.Text = string.Format(Localizer.T("Camera.EnrollFailed"), ex.Message);
        }
    }
}
