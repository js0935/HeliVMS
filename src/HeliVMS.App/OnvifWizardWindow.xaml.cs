using System.Windows;
using System.Windows.Controls;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.App;

/// <summary>
/// ONVIF 設備探索／設定嚮導（M2 驗收前置：自動發現 IPC、探測 Profile、取 RTSP 加入頻道）。
/// 關閉時若點「加入頻道」，DialogResult=True 並回傳 ChannelName／StreamUrl。
/// </summary>
public partial class OnvifWizardWindow : Window
{
    private IReadOnlyList<DiscoveredDevice> _devices = [];
    private string _deviceLabel = string.Empty;

    public OnvifWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>加入之頻道名稱（空白表示取消）。</summary>
    public string ChannelName { get; private set; } = string.Empty;

    /// <summary>加入之 RTSP 串流位址。</summary>
    public string StreamUrl { get; private set; } = string.Empty;

    private async void OnDiscoverClicked(object sender, RoutedEventArgs e)
    {
        DiscoverButton.IsEnabled = false;
        WizardHint.Text = "探索中…";

        try
        {
            var username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text;
            var password = PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(AddressBox.Text))
            {
                _devices = await DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                var ip = AddressBox.Text.Trim();
                _devices = [new DiscoveredDevice
                {
                    EndpointAddress = $"urn:manual:{ip}",
                    XAddrs = [$"http://{ip}/onvif/device_service"],
                }];
            }

            DeviceList.ItemsSource = _devices;
            WizardHint.Text = $"發現 {_devices.Count} 台設備。";
        }
        catch (Exception ex)
        {
            WizardHint.Text = $"探索失敗：{ex.Message}";
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }

    private async void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        ProfileList.ItemsSource = null;
        AddButton.IsEnabled = false;

        if (DeviceList.SelectedItem is not DiscoveredDevice device || device.HttpXAddr is null)
        {
            return;
        }

        WizardHint.Text = $"連線 {device.HttpXAddr}…";
        var username = string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text;
        var password = PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;

        using var service = new OnvifDeviceService(device.HttpXAddr, username, password);
        try
        {
            var info = await service.GetInfoAsync();
            _deviceLabel = info.DisplayName;

            var profiles = await service.GetProfilesAsync();
            _deviceLabel = profiles.Count > 0 ? $"{_deviceLabel}（{profiles.Count} 流）" : _deviceLabel;
            ProfileList.ItemsSource = profiles;
            WizardHint.Text = _deviceLabel;
        }
        catch (Exception ex)
        {
            WizardHint.Text = $"探測失敗：{ex.Message}";
        }
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = ProfileList.SelectedItem is OnvifProfile;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not OnvifProfile profile || profile.StreamUri.Length == 0)
        {
            WizardHint.Text = "請先選取含有 RTSP 位址的 Profile。";
            return;
        }

        StreamUrl = profile.StreamUri;
        ChannelName = string.IsNullOrWhiteSpace(_deviceLabel)
            ? profile.Name
            : $"{_deviceLabel} ・ {profile.Name}";
        DialogResult = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}