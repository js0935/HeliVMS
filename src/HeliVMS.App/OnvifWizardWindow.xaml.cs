using System.Windows;
using System.Windows.Controls;
using HeliVMS.Devices.Onvif;
using HeliVMS.Media;
using HeliVMS.Shared;

namespace HeliVMS.App;

/// <summary>
/// ONVIF 設備探索／設定嚮導（M2 驗收前置：自動發現 IPC、探測 Profile、取 RTSP 加入頻道）。
/// 多廠牌相容：手動位址會探測各廠牌常見 device service 路徑與埠，多播被封鎖時以單播 WS-Discovery 回退。
/// 關閉時若點「加入頻道」，DialogResult=True 並回傳 ChannelName／StreamUrl。
/// </summary>
public partial class OnvifWizardWindow : Window
{
private IReadOnlyList<DiscoveredDevice> _devices = [];
    private readonly List<OnvifProfile> _profiles = [];
    private string _deviceLabel = string.Empty;
    private string? _deviceXAddr;
    private OnvifDeviceService? _service;
    private OnvifProfile? _selectedProfile;
    private bool _busy;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 每個 profile 的實際解析度探測上限。多個 ffprobe 同時連線會互相搶占攝影機的 RTSP 連線數，
    /// 故給較寬裕的時間；逾時者保留 ONVIF 宣告值，不阻斷新增流程。
    /// 探測為並行（Task.WhenAll），故此上限只影響最慢的那一個 profile，不會成倍數累加。
    /// 實測最慢的攝影機（220.130.205.226）單一 profile 探測需 11.6～55.3 秒且波動很大；
    /// 若上限過短，所有 profile 都會探測失敗並靜默退回 ONVIF 宣告值，
    /// 而實測有 21 個 profile 中的 11 個宣告值與實際輸出不符，等同失去這項校正。
    /// </summary>
    private static readonly TimeSpan StreamProbeTimeout = TimeSpan.FromSeconds(45);

    public OnvifWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>加入之頻道名稱（空白表示取消）。</summary>
    public string ChannelName { get; private set; } = string.Empty;

    /// <summary>加入之 RTSP 串流位址（不含帳密，維持乾淨的裸位址）。</summary>
    public string StreamUrl { get; private set; } = string.Empty;

    /// <summary>加入之攝影機 IP；無法解析時為空字串（呼叫端須略過建立設備記錄）。</summary>
    public string DeviceIp { get; private set; } = string.Empty;

    /// <summary>攝影機 ONVIF 埠（DeviceIp 有效時才有意義）。</summary>
    public int DevicePort { get; private set; }

    /// <summary>攝影機帳號；為 null 表示未填，呼叫端不應建立設備記錄。</summary>
    public string? DeviceUsername { get; private set; }

    /// <summary>攝影機密碼（明文，僅供呼叫端寫入 devices.password_encrypted 後丟棄）。</summary>
    public string? DevicePassword { get; private set; }

    private string? Username => string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();

    private string? Password => PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;

private async void OnDiscoverClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // 使用與設備載入相同的取消機制，讓「取消」按鈕對探索也能生效。
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        SetBusy(true);
        SetProfiles([]);
        AddButton.IsEnabled = false;

        try
        {
            if (string.IsNullOrWhiteSpace(AddressBox.Text))
            {
                SetBusyHint("多播探索中…（若所在網路封鎖多播，請改填設備 IP）");
                _devices = await DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(5)).WaitAsync(token);
            }
            else
            {
                // 手動位址：多廠牌路徑/埠候選探測，失敗時以單播 WS-Discovery 回退
                var address = AddressBox.Text.Trim();
                SetBusyHint($"連線 {address} 探測 ONVIF 服務…");
                var xAddr = await DiscoveryClient.DiscoverByIpAsync(address, Username, Password).WaitAsync(token);
                if (xAddr is null)
                {
                    _devices = [];
                    DeviceList.ItemsSource = null;
                    WizardHint.Text = $"找不到 {address} 的 ONVIF 服務（請確認位址、埠與帳密）。";
                    return;
                }

                var host = OnvifEndpointResolver.ExtractHost(xAddr) ?? address;
                _devices =
                [
                    new DiscoveredDevice
                    {
                        EndpointAddress = $"urn:manual:{host}",
                        XAddrs = [xAddr],
                    },
                ];
            }

            DeviceList.ItemsSource = _devices;
            WizardHint.Text = _devices.Count > 0
                ? $"發現 {_devices.Count} 台設備，請選取以取得串流 Profile。"
                : "未發現任何 ONVIF 設備（多播可能被網路封鎖，請改填設備 IP）。";
        }
        catch (OperationCanceledException)
        {
            WizardHint.Text = "操作已取消。";
        }
catch (Exception ex)
        {
            WizardHint.Text = $"探索失敗：{Describe(ex)}";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }
    }

private async void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        SetProfiles([]);
        AddButton.IsEnabled = false;

        if (DeviceList.SelectedItem is not DiscoveredDevice device || device.HttpXAddr is null)
        {
            return;
        }

        // 換選設備時先取消仍在進行中的載入（避免兩個連載入交錯覆寫 UI）；新操作接管。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        SetBusy(true);

        var service = new OnvifDeviceService(device.HttpXAddr, Username, Password);
        _service?.Dispose();
        _service = service;
        _deviceXAddr = device.HttpXAddr;

        SetBusyHint($"連線 {device.HttpXAddr}…");
        try
        {
            var info = await service.GetInfoAsync(token);
            _deviceLabel = info.DisplayName;

            var profiles = await service.GetProfilesAsync(token);
            token.ThrowIfCancellationRequested();
            _deviceLabel = profiles.Count > 0 ? $"{_deviceLabel}（{profiles.Count} 流）" : _deviceLabel;

            try
            {
                await service.EnsurePtzCapabilityAsync(token);
                _deviceLabel += service.HasPtz ? " · PTZ：支援" : " · PTZ：不支援";
            }
            catch (OnvifException)
            {
                _deviceLabel += " · PTZ：未知";
            }

            profiles = await ProbeResolutionsAsync(profiles, token, (done, total) =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    SetBusyHint($"探測 Profile 解析度 {done}/{total}…");
                }
            });

            token.ThrowIfCancellationRequested();
            SetProfiles(profiles);
            SetBusyHint(_deviceLabel);

            // 預選主碼流（多 profile／NVR 多機箱時取最佳影像 profile）
            var main = OnvifProfileSelection.SelectMain(profiles);
            if (main is not null)
            {
                ProfileList.SelectedItem = _profiles.FirstOrDefault(p => p.Token == main.Token);
            }
        }
        catch (OperationCanceledException)
        {
            SetBusyHint("操作已取消。");
        }
catch (Exception ex)
        {
            SetBusyHint($"探測失敗：{Describe(ex)}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }
    }

    /// <summary>
    /// 以實際串流探測（ffprobe）校正各 profile 的解析度。
    /// 實測多款攝影機的 ONVIF VideoEncoderConfiguration/Resolution 與實際輸出不符（21 個 profile 中 11 個），
    /// 故以探測值為準；探測失敗則保留 ONVIF 宣告值，逾時上限見 <see cref="StreamProbeTimeout"/>。
    /// </summary>
private async Task<IReadOnlyList<OnvifProfile>> ProbeResolutionsAsync(
        IReadOnlyList<OnvifProfile> profiles,
        CancellationToken token,
        Action<int, int>? reportProgress = null)
    {
        var targets = profiles.Where(p => p.StreamUri.Length > 0).ToList();
        if (targets.Count == 0)
        {
            return profiles;
        }

        var username = Username;
        var password = Password;
        var probed = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);

        // 各 probe 的其中一段在背景執行緒，但 async lambda 的接續會回到 UI thread，
        // 因此對 Dictionary 的寫入與進度回呼都是序列化的，無需額外鎖定。
        var completed = 0;
        await Task.WhenAll(targets.Select(async profile =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var url = RtspUri.WithCredentials(profile.StreamUri, username, password);

                // ffprobe 依全專案慣例自 PATH 取得；不存在時 Process.Start 失敗，保留 ONVIF 宣告值。
                // 逾時或取消都直接交給 StreamProbe 處理，才能確實終止 ffprobe 進程（避免孤兒進程）。
                var info = await Task.Run(
                    () => StreamProbe.Probe(url, timeout: StreamProbeTimeout, cancellationToken: token),
                    token);

                if (info.Width > 0 && info.Height > 0)
                {
                    probed[profile.Token] = (info.Width, info.Height);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 探測失敗不阻斷新增流程：保留 ONVIF 宣告值。
            }
            finally
            {
                reportProgress?.Invoke(Interlocked.Increment(ref completed), targets.Count);
            }
        }));

        if (probed.Count == 0)
        {
            return profiles;
        }

        // 探測值較 ONVIF 宣告值可信，故清除原本依宣告值判定的角色後重新分類，
        // 否則 Classify 會沿用既有角色而忽略新的解析度。
        var corrected = OnvifProfileSelection.Classify(profiles.Select(p =>
            probed.TryGetValue(p.Token, out var size)
                ? p.WithProbedResolution(size.Width, size.Height).WithRole(OnvifStreamRole.Unknown)
                : p));

        return OnvifProfileSelection.Order(corrected);
    }

private async void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        var profile = ProfileList.SelectedItem as OnvifProfile;
        _selectedProfile = profile;
        AddButton.IsEnabled = profile is not null;
        if (profile is null || profile.StreamUri.Length > 0 || _service is null)
        {
            return;
        }

        // 串流位址未於 GetProfiles 預算內解析者（多 profile 設備），選取時即時取得；
        // 與設備載入共用取消機制，讓視窗關閉或換選 profile 能中止等待中的 ONVIF 呼叫。
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        SetBusy(true);
        SetBusyHint($"取得 {profile.DisplayLabel} 串流位址…");
        try
        {
            var uri = await _service.GetStreamUriAsync(profile.Token);
            token.ThrowIfCancellationRequested();
            if (uri is null)
            {
                SetBusyHint("此 Profile 未取得串流位址（設備可能不支援 RTSP 或 profile 已停用）。");
                return;
            }

            var index = _profiles.FindIndex(p => p.Token == profile.Token);
            if (index < 0)
            {
                return;
            }

            _profiles[index] = _profiles[index].WithStreamUri(uri);
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = _profiles;
            ProfileList.SelectedItem = _profiles[index];
            SetBusyHint(_deviceLabel);
        }
        catch (OperationCanceledException)
        {
            SetBusyHint("操作已取消。");
        }
catch (Exception ex)
        {
            SetBusyHint($"取得串流位址失敗：{Describe(ex)}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var profile = _selectedProfile;
        if (profile is null)
        {
            WizardHint.Text = "請先選取 Profile。";
            return;
        }

        if (profile.StreamUri.Length == 0)
        {
            WizardHint.Text = "此 Profile 未取得串流位址，請重新探索後再試。";
            return;
        }

        StreamUrl = profile.StreamUri;
        ChannelName = string.IsNullOrWhiteSpace(_deviceLabel)
            ? profile.DisplayLabel
            : $"{_deviceLabel} ・ {profile.DisplayLabel}";

        // 供呼叫端建立／連結 devices 記錄，讓 RTSP 與 PTZ 共用同一份加密憑證。
        (DeviceIp, DevicePort) = ParseHostPort(_deviceXAddr);
        DeviceUsername = Username;
        DevicePassword = Password;

        DialogResult = true;
        Close();
    }

    /// <summary>自 ONVIF XAddr 取出主機與埠；無法解析時回傳空字串與 0。</summary>
    private static (string Ip, int Port) ParseHostPort(string? xaddr)
    {
        if (string.IsNullOrWhiteSpace(xaddr) || !Uri.TryCreate(xaddr, UriKind.Absolute, out var uri))
        {
            return (string.Empty, 0);
        }

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return (string.Empty, 0);
        }

        // uri.Port 對未知 scheme 會回 -1 或預設值，需保留 XAddr 明載的埠。
        var port = uri.IsDefaultPort || uri.Port <= 0 ? 80 : uri.Port;
        return (host, port);
    }

private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void OnWindowClosed(object sender, EventArgs e)
    {
        // 中斷可能仍在進行的探索／探測／呼叫，避免關閉視窗後留下孤兒 ffprobe 或懸掛連線。
        // 故意不 Dispose：取消中的 async 接續可能仍持有 token，Dispose 會讓 Register 拋
        // ObjectDisposedException。
        _cts?.Cancel();
        _cts = null;

        _service?.Dispose();
        _service = null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DiscoverButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // 操作期間鎖住輸入，避免改用不同帳密的同時還送著舊帳密的連線。
        UserBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
    }

    private void SetBusyHint(string text) => WizardHint.Text = text;

    private void SetProfiles(IReadOnlyList<OnvifProfile> profiles)
    {
        _profiles.Clear();
        _profiles.AddRange(profiles);
        _selectedProfile = null;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _profiles;
    }

    private static string Describe(Exception exception) => exception switch
    {
        OnvifException onvif => onvif.Message,
        OperationCanceledException => "操作已取消。",
        _ => exception.Message,
    };
}
