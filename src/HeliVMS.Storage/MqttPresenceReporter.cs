using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// MQTT 在線狀態（presence）發布器（M111，§14.7 #15）：把 VMS 服務實體在線/離線狀態發布至
/// <c>&lt;prefix&gt;/status</c>，以 **retained（保留）** PUBLISH 交給 broker 保存最後狀態——新訂閱者
/// 加入即立刻收到目前狀態（MQTT 狀態保留語意），無須常駐連線。正式＝包 <see cref="MqttClient"/>；
/// 測試＝包 fake。
/// </summary>
public sealed class MqttPresenceReporter
{
    private readonly IMqttPublisher _publisher;
    private readonly string _prefix;
    private readonly string _clientId;

    public MqttPresenceReporter(IMqttPublisher publisher, string prefix, string clientId)
    {
        _publisher = publisher;
        _prefix = string.IsNullOrWhiteSpace(prefix) ? MqttEventRouter.DefaultPrefix : prefix;
        _clientId = clientId;
    }

    /// <summary>發布在線（retained）。</summary>
    public MqttPublishResult Online(DateTime atUtc)
        => Report("online", atUtc);

    /// <summary>發布離線（retained）。</summary>
    public MqttPublishResult Offline(DateTime atUtc)
        => Report("offline", atUtc);

    private MqttPublishResult Report(string status, DateTime atUtc)
    {
        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                status,
                client = _clientId,
                at = SqliteStore.Iso(atUtc),
            }));
        return _publisher.PublishRetained(MqttEventRouter.StatusTopic(_prefix), payload);
    }
}