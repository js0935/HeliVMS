using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// MQTT 事件通知（M108）：委派 Storage 層 <see cref="IMqttPublisher"/>（正式＝<see cref="MqttClient"/>）
/// 發送 CONNECT（含 MQTT user/password）/PUBLISH/DISCONNECT，單一 MQTT 協定實作；payload 維持
/// 既有 {channel_id,event_type,start_utc,detail} 契約。以極簡介面注入 fake 即可離線單測。
/// </summary>
public sealed class MqttNotifier
{
    private readonly IMqttPublisher _publisher;

    public MqttNotifier() : this(new MqttClient())
    {
    }

    public MqttNotifier(IMqttPublisher publisher) => _publisher = publisher;

    public async Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord e)
    {
        var host = cfg.MqttHost;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cfg.MqttTopic))
        {
            return false;
        }

        var port = cfg.MqttPort > 0 ? cfg.MqttPort : 1883;
        var clientId = $"helivms-{Guid.NewGuid():N}".Substring(0, 20);
        var payloadJson = JsonSerializer.Serialize(new
        {
            channel_id = e.ChannelId,
            event_type = e.EventType,
            start_utc = e.StartUtc.ToString("o"),
            detail = e.Detail,
        });
        var payload = Encoding.UTF8.GetBytes(payloadJson);

        var connected = _publisher.Connect(host, port, clientId, 30, cfg.MqttUser, cfg.MqttPassword);
        if (!connected.Ok)
        {
            return false;
        }

        try
        {
            var published = _publisher.Publish(cfg.MqttTopic!, payload);
            return published.Ok;
        }
        finally
        {
            _publisher.Disconnect();
        }
    }
}