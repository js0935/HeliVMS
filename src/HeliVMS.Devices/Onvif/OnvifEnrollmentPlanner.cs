namespace HeliVMS.Devices.Onvif;

/// <summary>
/// 探測到的設備解析成「可以直接加入」的攝影機：主／子碼流位址與連線資訊。
/// </summary>
/// <remarks>
/// 各欄位依序為設備自報名稱（呼叫端可另行命名）、主碼流、子碼流（無則 null）、
/// 設備 IP、ONVIF 埠、帳號、密碼。
/// </remarks>
public sealed record OnvifEnrollmentCandidate(
    string SuggestedName,
    string MainStreamUrl,
    string? SubStreamUrl,
    string DeviceIp,
    int DevicePort,
    string? Username,
    string? Password);

/// <summary>批次加入的單台結果；<see cref="Error"/> 為例外訊息，沒有可用碼流時為 null。</summary>
public sealed record OnvifBatchAddResult(
    DiscoveredDevice Device,
    OnvifEnrollmentCandidate? Candidate,
    string? Error)
{
    public bool Success => Candidate is not null;
}

/// <summary>
/// 批次加入：把探索（區網搜尋或網段掃描）到的設備逐台連線、挑出主／子碼流，
/// 變成一批可寫入資料庫的資料。
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="FirmwareUpgradeBatch"/> 同一種形狀：逐台**循序**處理、單台失敗不中止整批、
/// 每台回報一次進度。循序的理由一樣是資源與可讀性——同時對十幾台攝影機開 ONVIF 連線會
/// 擠掉正在監看／錄影的連線，而且操作員需要看得懂「現在輪到哪一台」。
/// </para>
/// <para>
/// <b>刻意不做解析度探測</b>：單台加入的流程會對每個 profile 跑一次 ffprobe（上限 45 秒），
/// 十幾台就是十幾分鐘，而批次要的是「先把機器都加進來」。解析度由監看畫面第一張影格
/// 自行校正，不影響錄影與回放。
/// </para>
/// </remarks>
public sealed class OnvifEnrollmentPlanner
{
    private readonly Func<string, string?, string?, OnvifDeviceService> _serviceFactory;

    public OnvifEnrollmentPlanner(
        Func<string, string?, string?, OnvifDeviceService>? serviceFactory = null)
    {
        _serviceFactory = serviceFactory
            ?? ((xAddr, username, password) => new OnvifDeviceService(xAddr, username, password));
    }

    /// <summary>
    /// 逐台解析；呼叫端主動取消則往上拋（<see cref="OperationCanceledException"/>）。
    /// </summary>
    /// <param name="devices">要解析的設備（探索結果）。</param>
    /// <param name="username">ONVIF 帳號（同一批共用）。</param>
    /// <param name="password">ONVIF 密碼。</param>
    /// <param name="progress">每完成一台回報一次（已完成, 總數, 該台位址）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public async Task<IReadOnlyList<OnvifBatchAddResult>> RunAsync(
        IReadOnlyList<DiscoveredDevice> devices,
        string? username = null,
        string? password = null,
        IProgress<(int Done, int Total, string Address)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var results = new List<OnvifBatchAddResult>(devices.Count);
        var done = 0;
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OnvifBatchAddResult result;
            try
            {
                var candidate = await ResolveAsync(device, username, password, cancellationToken)
                    .ConfigureAwait(false);
                result = new OnvifBatchAddResult(device, candidate, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new OnvifBatchAddResult(device, null, ex.Message);
            }

            results.Add(result);
            progress?.Report((Interlocked.Increment(ref done), devices.Count, AddressOf(device)));
        }

        return results;
    }

    private static string AddressOf(DiscoveredDevice device)
        => device.HttpXAddr ?? device.EndpointAddress;

    private async Task<OnvifEnrollmentCandidate?> ResolveAsync(
        DiscoveredDevice device,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (device.HttpXAddr is not { } xAddr)
        {
            return null;
        }

        using var service = _serviceFactory(xAddr, username, password);
        var info = await service.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await service.GetProfilesAsync(cancellationToken).ConfigureAwait(false);

        var main = OnvifProfileSelection.SelectMain(profiles);
        if (main is null)
        {
            return null;
        }

        var mainUri = main.StreamUri.Length > 0
            ? main.StreamUri
            : await service.GetStreamUriAsync(main.Token, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (mainUri.Length == 0)
        {
            return null;
        }

        var sub = OnvifProfileSelection.SelectSub(profiles);
        var subUri = string.Empty;
        if (sub is not null && sub.Token != main.Token)
        {
            subUri = sub.StreamUri.Length > 0
                ? sub.StreamUri
                : await service.GetStreamUriAsync(sub.Token, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }

        // 同一條位址就不是「子碼流」：留著會讓多分割監看拉兩次同一條串流。
        if (string.Equals(subUri, mainUri, StringComparison.Ordinal))
        {
            subUri = string.Empty;
        }

        var (host, port) = OnvifEndpointResolver.ExtractHostAndPort(xAddr);
        return new OnvifEnrollmentCandidate(
            string.IsNullOrWhiteSpace(info.DisplayName) ? host : info.DisplayName.Trim(),
            mainUri,
            subUri.Length > 0 ? subUri : null,
            host,
            port,
            username,
            password);
    }
}
