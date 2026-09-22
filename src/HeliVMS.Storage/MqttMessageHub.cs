namespace HeliVMS.Storage;

/// <summary>
/// 單次 Pump 結果（M115）：Handled＞0＝本輪派送處理筆數；TimedOut＝接收逾時；ConnectionLost＝
/// 連線失效（ReceiveMessage Fail）；Ignored 控制封包時全欄 false。
/// </summary>
public sealed record MqttPumpResult(
    bool TimedOut,
    bool ConnectionLost,
    int Handled,
    string? Error)
{
    public static MqttPumpResult HandledCount(int count) => new(false, false, count, null);
    public static MqttPumpResult Timeout() => new(true, false, 0, null);
    public static MqttPumpResult Ignored() => new(false, false, 0, null);
    public static MqttPumpResult Lost(string error) => new(false, true, 0, error);
}

/// <summary>
/// MQTT 訂閱數據面掛載（M115，§14.7 #15 收尾）：把 <see cref="MqttClient.ReceiveMessage"/> 的能力
/// 化為常駐派送——<see cref="Register"/> 以 topic filter 註冊處理函式後，<see cref="SubscribeAll"/>
/// 對 broker 送出控制面 SUBSCRIBE，再由 <see cref="Pump"/>（或 <see cref="Run"/> 常駐迴圈）逐一收
/// 訊並以 <see cref="MqttTopicFilter.Matches"/> 路由到相符處理函式。斷線時 ConnectionLost=true，
/// 由呼叫端決定重連。同 filter 可註冊多個處理函式。
/// </summary>
public sealed class MqttMessageHub
{
    private readonly MqttClient _client;
    private readonly Dictionary<string, List<Action<MqttInboundMessage>>> _handlers = new();

    public MqttMessageHub(MqttClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IReadOnlyCollection<string> Filters => _handlers.Keys;

    public void Register(string filter, Action<MqttInboundMessage> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(filter, out var list))
        {
            list = new List<Action<MqttInboundMessage>>();
            _handlers[filter] = list;
        }

        list.Add(handler);
    }

    /// <summary>對 broker 逐一送出控制面 SUBSCRIBE；回傳成功數。</summary>
    public int SubscribeAll()
    {
        var ok = 0;
        foreach (var filter in _handlers.Keys)
        {
            if (_client.Subscribe(filter).Ok)
            {
                ok++;
            }
        }

        return ok;
    }

    /// <summary>單輪接收＋派送：至多處理一筆 PUBLISH。</summary>
    public MqttPumpResult Pump(TimeSpan receiveTimeout)
    {
        var result = _client.ReceiveMessage(receiveTimeout);
        if (!result.Ok)
        {
            return result.TimedOut ? MqttPumpResult.Timeout() : MqttPumpResult.Lost(result.Error ?? "接收失敗");
        }

        if (result.Message is null)
        {
            return MqttPumpResult.Ignored();
        }

        var message = result.Message;
        var handled = 0;
        foreach (var pair in _handlers)
        {
            if (!MqttTopicFilter.Matches(pair.Key, message.Topic))
            {
                continue;
            }

            foreach (var handler in pair.Value)
            {
                handler(message);
                handled++;
            }
        }

        return MqttPumpResult.HandledCount(handled);
    }

    /// <summary>常駐迴圈：持續 Pump，直到被取消或連線失效（ConnectionLost）。</summary>
    public Task Run(TimeSpan receiveTimeout, CancellationToken cancellationToken)
    {
        if (receiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(receiveTimeout));
        }

        return Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = Pump(receiveTimeout);
                if (result.ConnectionLost)
                {
                    return;
                }
            }
        }, cancellationToken);
    }
}