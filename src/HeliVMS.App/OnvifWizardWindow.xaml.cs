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
    /// <summary>清單上的一列設備；勾選＝批次加入的對象。</summary>
    public sealed class DeviceRow
    {
        public DeviceRow(DiscoveredDevice device)
        {
            Device = device;
            NameHint = device.NameHint ?? string.Empty;
            Address = device.HttpXAddr ?? device.EndpointAddress;
        }

        public DiscoveredDevice Device { get; }

        /// <summary>設備自報名稱（WS-Discovery scope 或掃描時讀到的名稱）；未知為空。</summary>
        public string NameHint { get; }

        /// <summary>device_service 位址。</summary>
        public string Address { get; }

        /// <summary>是否勾選（清單樣板雙向繫結；批次加入讀這一個）。</summary>
        public bool IsChecked { get; set; }
    }

    private List<DeviceRow> _rows = [];
    private List<OnvifEnrollmentCandidate> _candidates = [];
    private readonly List<OnvifProfile> _profiles = [];
    private string _deviceLabel = string.Empty;
    private string _deviceName = string.Empty;
    private string? _deviceXAddr;
    private OnvifDeviceService? _service;
    private OnvifProfile? _mainProfile;
    private OnvifProfile? _subProfile;
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

    private readonly bool _streamsOnly;

    public OnvifWizardWindow()
        : this(streamsOnly: false, address: null, username: null, password: null)
    {
    }

    /// <summary>
    /// <paramref name="streamsOnly"/>＝只重新取得一台已知攝影機的碼流（頻道管理「重新取得攝影機碼流」）。
    /// 預填位址與帳密並自動探索：這台攝影機已經在清單裡，操作員指定過，不需要再找一次。
    /// 視窗不最大化——它只是用來確認兩條網址，不是初次加入攝影機的完整流程。
    /// </summary>
    public OnvifWizardWindow(bool streamsOnly, string? address, string? username, string? password)
    {
        _streamsOnly = streamsOnly;
        InitializeComponent();
        ApplyI18n();

        if (!streamsOnly)
        {
            return;
        }

        WindowState = WindowState.Normal;
        AddButton.Content = Localizer.T("Onvif.UseStreams");
        OnvifDesc.Text = Localizer.T("Onvif.UseStreamsDesc");
        if (!string.IsNullOrWhiteSpace(address))
        {
            AddressBox.Text = address.Trim();
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            UserBox.Text = username;
        }

        if (!string.IsNullOrEmpty(password))
        {
            PasswordBox.Password = password;
        }

        Loaded += OnStreamsOnlyLoaded;
    }

    /// <summary>
    /// 視窗顯示後才自動探索：在建構子直接跑會在下一次配置前就開始網路 I/O，
    /// 進度條與提示文字都還沒畫出來，操作員只看到一個凍住的空視窗。
    /// </summary>
    private void OnStreamsOnlyLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnStreamsOnlyLoaded;
        OnDiscoverClicked(DiscoverButton, new RoutedEventArgs());
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Onvif.Title");
        OnvifHeader.Text = Localizer.T("Onvif.Header");
        OnvifDesc.Text = Localizer.T("Onvif.Desc");
        OnvifAddressLabel.Text = Localizer.T("Onvif.AddressLabel");
        OnvifAddressHint.Text = Localizer.T("Onvif.AddressHint");
        AddressBox.ToolTip = Localizer.T("Onvif.AddressTip");
        BatchAddButton.Content = Localizer.T("Onvif.BatchAdd");
        BatchAddButton.ToolTip = Localizer.T("Onvif.BatchAddTip");
        BatchAddButton.Visibility = _streamsOnly ? Visibility.Collapsed : Visibility.Visible;
        SelectAllButton.ToolTip = Localizer.T("Onvif.SelectAllTip");
        UpdateBatchButtons();
        OnvifUserLabel.Text = Localizer.T("Onvif.UserLabel");
        UserBox.ToolTip = Localizer.T("Onvif.UserTip");
        OnvifPasswordLabel.Text = Localizer.T("Onvif.PasswordLabel");
        PasswordBox.ToolTip = Localizer.T("Onvif.PasswordTip");
        DiscoverButton.Content = Localizer.T("Onvif.Discover");
        DiscoverButton.ToolTip = Localizer.T("Onvif.DiscoverTip");
        CancelButton.Content = Localizer.T("Onvif.Cancel");
        CancelButton.ToolTip = Localizer.T("Onvif.CancelTip");
        AddButton.Content = Localizer.T("Onvif.Add");
        AddButton.ToolTip = Localizer.T("Onvif.AddTip");
        CloseButton.ToolTip = Localizer.T("Onvif.CloseTip");
        OnvifDeviceLabel.Text = Localizer.T("Onvif.DeviceLabel");
        OnvifProfilesLabel.Text = Localizer.T("Onvif.ProfilesLabel");
        WizardHint.Text = Localizer.T("Onvif.Hint");
    }

    /// <summary>
    /// 要加入的攝影機（單台是長度 1 的清單）。對話結果為 true 時至少一台。
    /// </summary>
    /// <remarks>
    /// 單台與批次共用同一個出口：呼叫端只有一條「逐台命名＋寫入」的路，
    /// 不會出現「批次加進去的沒有 device 記錄」這種只在批次路徑才會踩到的缺口。
    /// </remarks>
    public IReadOnlyList<OnvifEnrollmentCandidate> Candidates => _candidates;

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
            var expansion = DiscoveryAddressRange.Expand(AddressBox.Text);
            if (expansion.Addresses.Count > 1)
            {
                await RunSweepAsync(expansion, cts, token);
            }
            else if (expansion.Addresses.Count == 1)
            {
                // 手動位址：多廠牌路徑/埠候選探測，失敗時以單播 WS-Discovery 回退
                var address = expansion.Addresses[0];
                SetBusyHint(string.Format(Localizer.T("Onvif.ConnectingTarget"), address));
                var xAddr = await DiscoveryClient.DiscoverByIpAsync(address, Username, Password).WaitAsync(token);
                if (xAddr is null)
                {
                    _rows = [];
                    DeviceList.ItemsSource = null;
                    WizardHint.Text = string.Format(Localizer.T("Onvif.ServiceNotFound"), address);
                    return;
                }

                var host = OnvifEndpointResolver.ExtractHost(xAddr) ?? address;
                SetRows(
                [
                    new DiscoveredDevice
                    {
                        EndpointAddress = $"urn:manual:{host}",
                        XAddrs = [xAddr],
                    },
                ]);
            }
            else
            {
                SetBusyHint(Localizer.T("Onvif.Discovering"));
                SetRows(await DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(5)).WaitAsync(token));
            }

            WizardHint.Text = _rows.Count > 0
                ? string.Format(Localizer.T("Onvif.FoundDevices"), _rows.Count)
                : Localizer.T("Onvif.NotFound");

            // 重新取得碼流時目標是已綁定的那台攝影機，通常只有一筆；直接選取省一次點擊。
            // 多筆（區網探索回傳很多台）仍留給操作員挑：選錯設備就是把別台的碼流寫進這個頻道。
            if (_streamsOnly && _rows.Count == 1)
            {
                DeviceList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            WizardHint.Text = Localizer.T("Onvif.Canceled");
        }
        catch (Exception ex)
        {
            WizardHint.Text = string.Format(Localizer.T("Onvif.DiscoverFailed"), Describe(ex));
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
    /// 網段掃描：把整段位址並行探測，找到的設備一次列出來。
    /// </summary>
    /// <remarks>
    /// 進度用「已完成／總數」而不用百分比：操作員要判斷的是「還剩幾個位址」，
    /// 而掃描速度取決於當下有沒有閒置主機（沒有回應的位址要等逾時）。
    /// </remarks>
    private async Task RunSweepAsync(
        DiscoveryAddressRange.Expansion expansion,
        CancellationTokenSource cts,
        CancellationToken token)
    {
        var total = expansion.Addresses.Count;
        SetBusyHint(string.Format(Localizer.T("Onvif.Sweeping"), 0, total));

        // Progress 在 UI 執行緒回呼（捕捉建立時的 SynchronizationContext），可直接寫控制項。
        var progress = new Progress<(int Done, int Total)>(report =>
        {
            if (ReferenceEquals(_cts, cts))
            {
                SetBusyHint(string.Format(Localizer.T("Onvif.Sweeping"), report.Done, report.Total));
            }
        });

        var found = await DiscoverySweep.RunAsync(
            expansion.Addresses,
            (address, ct) => DiscoveryClient.ProbeAddressAsync(address, Username, Password, ct),
            progress: progress,
            cancellationToken: token);

        SetRows(found);

        if (expansion.Truncated)
        {
            // 明確說出「只掃了一部分」：否則操作員會以為整段都掃過了、只是沒設備。
            WizardHint.Text = string.Format(
                Localizer.T("Onvif.SweepTruncated"), total, DiscoveryAddressRange.MaxAddresses);
        }
    }

    /// <summary>更新設備清單（每次重設列，勾選狀態不沿用上一輪）。</summary>
    private void SetRows(IReadOnlyList<DiscoveredDevice> devices)
    {
        _rows = [.. devices.Select(d => new DeviceRow(d))];
        DeviceList.ItemsSource = null;
        DeviceList.ItemsSource = _rows;
        UpdateBatchButtons();
    }

    private async void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        SetProfiles([]);
        AddButton.IsEnabled = false;

        if (DeviceList.SelectedItem is not DeviceRow row || row.Device.HttpXAddr is null)
        {
            return;
        }

        var device = row.Device;

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

        SetBusyHint(string.Format(Localizer.T("Onvif.Connecting"), device.HttpXAddr));
        try
        {
            var info = await service.GetInfoAsync(token);
            _deviceName = info.DisplayName;
            _deviceLabel = info.DisplayName;

            var profiles = await service.GetProfilesAsync(token);
            token.ThrowIfCancellationRequested();
            _deviceLabel = profiles.Count > 0
                ? string.Format(Localizer.T("Onvif.StreamsSuffix"), _deviceLabel, profiles.Count)
                : _deviceLabel;

            try
            {
                await service.EnsurePtzCapabilityAsync(token);
                _deviceLabel += service.HasPtz ? Localizer.T("Onvif.PtzYes") : Localizer.T("Onvif.PtzNo");
            }
            catch (OnvifException)
            {
                _deviceLabel += Localizer.T("Onvif.PtzUnknown");
            }

            profiles = await ProbeResolutionsAsync(profiles, token, (done, total) =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    SetBusyHint(string.Format(Localizer.T("Onvif.ProbingRes"), done, total));
                }
            });

            token.ThrowIfCancellationRequested();
            SetProfiles(profiles);
            SetBusyHint(_deviceLabel);

            // 使用者不需挑碼流：自動選用最佳主碼流與子碼流（錄影／單畫面用主碼流、多分割用子碼流）。
            _mainProfile = OnvifProfileSelection.SelectMain(profiles);
            var sub = OnvifProfileSelection.SelectSub(profiles);
            _subProfile = sub is not null && _mainProfile is not null && sub.Token != _mainProfile.Token
                ? sub
                : null;

            if (_mainProfile is not null)
            {
                // 於清單顯示目前選用之主碼流；OnProfileSelected 會補齊尚未解析的位址。
                ProfileList.SelectedItem = _profiles.FirstOrDefault(p => p.Token == _mainProfile.Token);
            }
        }
        catch (OperationCanceledException)
        {
            SetBusyHint(Localizer.T("Onvif.Canceled"));
        }
        catch (Exception ex)
        {
            SetBusyHint(string.Format(Localizer.T("Onvif.ProbeFailed"), Describe(ex)));
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
        SetBusyHint(string.Format(Localizer.T("Onvif.GettingStream"), profile.DisplayLabel));
        try
        {
            var uri = await _service.GetStreamUriAsync(profile.Token);
            token.ThrowIfCancellationRequested();
            if (uri is null)
            {
                SetBusyHint(Localizer.T("Onvif.NoStreamUri"));
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
            SetBusyHint(Localizer.T("Onvif.Canceled"));
        }
        catch (Exception ex)
        {
            SetBusyHint(string.Format(Localizer.T("Onvif.GetStreamFailed"), Describe(ex)));
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
    /// 加入頻道：不需使用者挑選碼流，自動帶入主碼流（錄影／單畫面）與子碼流（多分割）。
    /// 多 profile 設備的位址可能是延後解析的，故在此補齊後才回傳。
    /// </summary>
    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var main = _mainProfile ?? OnvifProfileSelection.SelectMain(_profiles);
        if (main is null)
        {
            WizardHint.Text = Localizer.T("Onvif.NoUsableStream");
            return;
        }

        SetBusy(true);
        SetBusyHint(Localizer.T("Onvif.GettingStreamUri"));
        try
        {
            var mainUri = await ResolveStreamUriAsync(main, CancellationToken.None);
            if (mainUri.Length == 0)
            {
                WizardHint.Text = Localizer.T("Onvif.NoMainUri");
                return;
            }

            var sub = _subProfile;
            var subUri = sub is null ? string.Empty : await ResolveStreamUriAsync(sub, CancellationToken.None);
            if (string.Equals(subUri, mainUri, StringComparison.Ordinal))
            {
                subUri = string.Empty;
            }

            // 供呼叫端建立／連結 devices 記錄，讓 RTSP 與 PTZ 共用同一份加密憑證。
            var (ip, port) = ParseHostPort(_deviceXAddr);
            _candidates =
            [
                new OnvifEnrollmentCandidate(
                    string.IsNullOrWhiteSpace(_deviceName) ? main.DisplayLabel : _deviceName,
                    mainUri,
                    subUri.Length > 0 ? subUri : null,
                    ip,
                    port,
                    Username,
                    Password),
            ];

            DialogResult = true;
            Close();
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 批次加入：把勾選的設備逐台連線、解析主／子碼流，再一次交給呼叫端寫入。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不做解析度探測</b>：單台的流程會對每個 profile 跑 ffprobe（上限 45 秒），
    /// 十幾台加起來就是十幾分鐘，而批次要的是「先把機器都加進來」。解析度由監看畫面
    /// 第一張影格自行校正，不影響錄影與回放。
    /// </para>
    /// <para>
    /// <b>單台失敗不中止整批</b>：一台連不上不該讓另外十一台重新做一次；成功的照樣交出去，
    /// 失敗的連原因一起回報（呼叫端會把兩邊的數量都寫在畫面上）。
    /// </para>
    /// </remarks>
    private async void OnBatchAddClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var targets = _rows.Where(r => r.IsChecked).ToList();
        if (targets.Count == 0)
        {
            WizardHint.Text = Localizer.T("Onvif.BatchNoneSelected");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        SetBusy(true);
        _candidates = [];
        var failures = new List<string>();

        try
        {
            // 解析邏輯在 HeliVMS.Devices.OnvifEnrollmentPlanner（行為測試在那裡，不必開 UI）。
            var progress = new Progress<(int Done, int Total, string Address)>(report =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    SetBusyHint(string.Format(
                        Localizer.T("Onvif.BatchProgress"), report.Done, report.Total, report.Address));
                }
            });

            var results = await new OnvifEnrollmentPlanner().RunAsync(
                [.. targets.Select(r => r.Device)],
                Username,
                Password,
                progress,
                token);

            foreach (var result in results)
            {
                if (result.Candidate is { } candidate)
                {
                    _candidates.Add(candidate);
                }
                else
                {
                    failures.Add(
                        $"{result.Device.HttpXAddr ?? result.Device.EndpointAddress}：{result.Error ?? Localizer.T("Onvif.NoUsableStream")}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            WizardHint.Text = Localizer.T("Onvif.Canceled");
            _candidates = [];
            return;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }

        if (_candidates.Count == 0)
        {
            WizardHint.Text = string.Format(Localizer.T("Onvif.BatchAllFailed"), string.Join("；", failures));
            return;
        }

        if (failures.Count > 0)
        {
            // 仍然把成功的交出去；失敗清單由呼叫端在加入結果裡一起顯示。
            WizardHint.Text = string.Format(
                Localizer.T("Onvif.BatchPartial"), _candidates.Count, string.Join("；", failures));
        }

        DialogResult = true;
        Close();
    }

    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        // 全部已勾選時這顆按鈕的意義是「取消全選」，否則就是「全選」。
        var check = _rows.Any(r => !r.IsChecked);
        foreach (var row in _rows)
        {
            row.IsChecked = check;
        }

        DeviceList.ItemsSource = null;
        DeviceList.ItemsSource = _rows;
        UpdateBatchButtons();
    }

    /// <summary>批次按鈕與全選按鈕的可用狀態與文字（沒有設備時整排收起）。</summary>
    private void UpdateBatchButtons()
    {
        var hasRows = _rows.Count > 0;
        BatchAddButton.Visibility = hasRows && !_streamsOnly ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Visibility = hasRows && !_streamsOnly ? Visibility.Visible : Visibility.Collapsed;
        // 忙碌中一律停用：批次加入自己會擋重入，但按鈕看起來可按會讓人以為沒在跑。
        BatchAddButton.IsEnabled = !_busy && hasRows && _rows.Any(r => r.IsChecked);
        SelectAllButton.Content = _rows.All(r => r.IsChecked) && hasRows
            ? Localizer.T("Onvif.SelectNone")
            : Localizer.T("Onvif.SelectAll");
    }

    /// <summary>取得 profile 的 RTSP 位址；尚未解析者即時向設備查詢（失敗回傳空字串）。</summary>
    private async Task<string> ResolveStreamUriAsync(OnvifProfile profile, CancellationToken token)
    {
        if (profile.StreamUri.Length > 0)
        {
            return profile.StreamUri;
        }

        if (_service is null)
        {
            return string.Empty;
        }

        try
        {
            var uri = await _service.GetStreamUriAsync(profile.Token);
            token.ThrowIfCancellationRequested();
            if (uri is null)
            {
                return string.Empty;
            }

            var index = _profiles.FindIndex(p => p.Token == profile.Token);
            if (index >= 0)
            {
                _profiles[index] = _profiles[index].WithStreamUri(uri);
                ProfileList.ItemsSource = null;
                ProfileList.ItemsSource = _profiles;
            }

            return uri;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
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
        AddressBox.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;

        // 加入鈕的可用性由「有沒有可用的主碼流」決定（選設備時載入 profile）；忙碌時一律停用。
        AddButton.IsEnabled = !busy && (_mainProfile ?? OnvifProfileSelection.SelectMain(_profiles)) is not null;
        UpdateBatchButtons();
    }

    private void SetBusyHint(string text) => WizardHint.Text = text;

    private void SetProfiles(IReadOnlyList<OnvifProfile> profiles)
    {
        _profiles.Clear();
        _profiles.AddRange(profiles);
        _mainProfile = null;
        _subProfile = null;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _profiles;
    }

    private static string Describe(Exception exception) => exception switch
    {
        OnvifException onvif => onvif.Message,
        OperationCanceledException => Localizer.T("Onvif.Canceled"),
        _ => exception.Message,
    };

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }
}
