using System.Net.Http;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// Webhook 寄送器（§18.5 通知平面核心）：POST JSON
/// <c>{type, channel, ts, data}</c> 至第三方端點。單次送達判定（重試在
/// <see cref="NotificationService"/> 層執行）。
/// </summary>
public sealed class WebhookNotifier
{
    private readonly HttpClient _http;

    public WebhookNotifier(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<bool> SendAsync(string url, AlarmEventRecord record)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = record.EventType,
                channel = record.ChannelId,
                ts = record.StartUtc.ToString("O"),
                data = new
                {
                    id = record.Id,
                    end = record.EndUtc?.ToString("O"),
                    snapshot = record.SnapshotPath,
                    detail = record.Detail,
                },
            });

            using var resp = await _http.PostAsync(
                url,
                new StringContent(payload, Encoding.UTF8, "application/json"));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}