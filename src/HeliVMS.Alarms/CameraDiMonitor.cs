using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>設備上的一個數位輸入端子（ONVIF DigitalInput 的權杖／編號／名稱）。</summary>
public sealed record OnvifInputRef(string Token, int Id, string Name);

/// <summary>已解讀的數位輸入事件（權杖與作動狀態）。</summary>
/// <remarks>
/// ONVIF 通知的 XML 解析屬於裝置層（HeliVMS.Devices 的
/// <c>OnvifDeviceService.TryParseDigitalInput</c>，含其測試）；這一層只認「哪個端子、什麼狀態」，
/// 因此不需要相依 ONVIF 的 HTTP 堆疊，也讓規則鏈可以在單元測試裡餵假事件。
/// </remarks>
public sealed record CameraDiInput(string Token, bool Active);

/// <summary>
/// 攝影機乾接點輸入（DI）與系統埠設定的對應。
/// </summary>
/// <remarks>
/// ONVIF 事件只帶端子權杖（<c>DigitalInputToken_1</c>），系統的埠是接線端子號；兩者沒有
/// 標準對應，因此與繼電器輸出（<see cref="OnvifIoOutputWriter.RelayTokenFor"/>）採同一套
/// 三段規則：<b>(1)</b> 端子權杖完全相同、<b>(2)</b> 權杖／名稱尾數等於端子號、
/// <b>(3)</b> 端子號當 1 起算序號。對不到就不餵——寧可少一個事件，也不要讓 A 端子的訊號
/// 去觸發 B 端子的規則（那會寫出指向錯誤地點的警報）。
/// </remarks>
public static class CameraDiMapping
{
    /// <summary>建立「端子權杖 → 埠 Id」對照；同一權杖被多埠對上時，端子號較小者優先。</summary>
    public static IReadOnlyDictionary<string, long> Build(
        IReadOnlyList<IoPortRecord> diPorts,
        IReadOnlyList<OnvifInputRef> inputs)
    {
        ArgumentNullException.ThrowIfNull(diPorts);
        ArgumentNullException.ThrowIfNull(inputs);

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in diPorts.OrderBy(p => p.Number))
        {
            var token = TokenFor(port, inputs);
            if (token is not null && !map.ContainsKey(token))
            {
                map[token] = port.Id;
            }
        }

        return map;
    }

    /// <summary>這個埠對應哪個端子權杖；找不到回 <c>null</c>。</summary>
    public static string? TokenFor(IoPortRecord port, IReadOnlyList<OnvifInputRef> inputs)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(inputs);

        // 與繼電器輸出同一條規則：設定中心綁定的端子優先，綁定失效就回 null，
        // 不退回猜測——寧可少一個事件，也不要讓 A 端子的訊號觸發 B 端子的規則。
        if (!string.IsNullOrWhiteSpace(port.TerminalToken))
        {
            return inputs.Any(i => string.Equals(i.Token, port.TerminalToken, StringComparison.OrdinalIgnoreCase))
                ? port.TerminalToken
                : null;
        }

        foreach (var input in inputs)
        {
            if (string.Equals(input.Token, port.Name, StringComparison.OrdinalIgnoreCase))
            {
                return input.Token;
            }
        }

        foreach (var input in inputs)
        {
            if (OnvifIoOutputWriter.TrailingNumber(input.Token) == port.Number
                || OnvifIoOutputWriter.TrailingNumber(input.Name) == port.Number
                || input.Id == port.Number)
            {
                return input.Token;
            }
        }

        var index = port.Number - 1;
        return index >= 0 && index < inputs.Count ? inputs[index].Token : null;
    }
}

/// <summary>
/// 攝影機 DI 的事件監視器：訂閱 ONVIF PullPoint → 把數位輸入的變化餵進
/// <see cref="IoRuleHost"/>（規則鏈再決定要寫警報事件或切換繼電器）。
///
/// <para>
/// 為什麼需要它：ONVIF 沒有「讀取 DI 目前狀態」的操作，狀態只在事件裡；沒有這個監視器，
/// 攝影機的 DI 永遠不會進系統——規則與警報只能靠人工模擬觸發。既有的 Modbus
/// （<see cref="IoDeviceMonitor"/>）是另一條實體路徑（IO 模組），兩者互補。
/// </para>
/// <para>
/// 拉取／訂閱以委派注入（實際為 <c>OnvifDeviceService</c>），所以重試、逾時、對不到端子的
/// 行為都能在測試裡用假的事件來源釘住，不必真的接攝影機。
/// </para>
/// </summary>
public sealed class CameraDiMonitor : IDisposable
{
    /// <summary>取得設備的數位輸入端子清單（訂閱前用來建立權杖對照）。</summary>
    public delegate Task<IReadOnlyList<OnvifInputRef>> InputLister(CancellationToken cancellationToken);

    /// <summary>建立 PullPoint 訂閱並回傳訂閱位址。</summary>
    public delegate Task<Uri> Subscriber(CancellationToken cancellationToken);

    /// <summary>取得訂閱上待處理的數位輸入事件（已由裝置層解析）。</summary>
    public delegate Task<IReadOnlyList<CameraDiInput>> Puller(Uri subscription, CancellationToken cancellationToken);

    private readonly string _deviceName;
    private readonly IoPortRepository _ports;
    private readonly IoRuleHost _host;
    private readonly InputLister _listInputs;
    private readonly Subscriber _subscribe;
    private readonly Puller _pull;
    private readonly AuditLogRepository? _audit;
    private readonly TimeSpan _retryDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public CameraDiMonitor(
        string deviceName,
        IoPortRepository ports,
        IoRuleHost host,
        InputLister listInputs,
        Subscriber subscribe,
        Puller pull,
        AuditLogRepository? audit = null,
        TimeSpan? retryDelay = null)
    {
        _deviceName = deviceName;
        _ports = ports;
        _host = host;
        _listInputs = listInputs;
        _subscribe = subscribe;
        _pull = pull;
        _audit = audit;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>收到並解讀為數位輸入的通知筆數。</summary>
    public long NotificationCount { get; private set; }

    /// <summary>真的餵進規則鏈的次數。</summary>
    public long FedCount { get; private set; }

    /// <summary>對不到系統埠（尚未設定端子）的通知筆數——可用來提示「有訊號但沒接規則」。</summary>
    public long UnmappedCount { get; private set; }

    /// <summary>最近一次失敗原因（訂閱或拉取）；成功一輪後清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>狀態有更新（供介面刷新計數）。</summary>
    public event EventHandler? Updated;

    /// <summary>開始監視（重複呼叫無效）。</summary>
    public void Start()
    {
        if (_disposed || _loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>停止並等待背景迴圈結束（可在鎖內安全呼叫）。</summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var inputs = await _listInputs(cancellationToken).ConfigureAwait(false);
                var diPorts = _ports.List()
                    .Where(p => p.Kind == IoPortKind.Di)
                    .ToList();
                var map = CameraDiMapping.Build(diPorts, inputs);

                var subscription = await _subscribe(cancellationToken).ConfigureAwait(false);
                LastError = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var events = await _pull(subscription, cancellationToken).ConfigureAwait(false);
                    foreach (var input in events)
                    {
                        NotificationCount++;
                        if (!map.TryGetValue(input.Token, out var portId))
                        {
                            UnmappedCount++;
                            continue;
                        }

                        await _host.FeedAsync(portId, input.Active, cancellationToken).ConfigureAwait(false);
                        FedCount++;
                        Updated?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 連不上、認證失敗、訂閱過期：記下來等下一輪重新訂閱。
                // 不往外丟——監視器是最後一道，丟出去只會變成未觀察的工作例外。
                LastError = ex.Message;
                try
                {
                    _audit?.Record(
                        "system",
                        "camera.di.failed",
                        AuditCategories.Recording,
                        detail: $"{_deviceName}: {ex.Message}");
                }
                catch (Exception auditEx) when (auditEx is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // 稽核寫不進去不影響重試。
                }

                Updated?.Invoke(this, EventArgs.Empty);
                try
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 停止中的取消例外不影響處置。
        }

        _cts.Dispose();
    }
}
