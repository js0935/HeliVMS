using System.Net.Http;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// team+（互動資通）警示發送器（§18.5 通知平面）：以 <c>application/x-www-form-urlencoded</c>
/// POST 事件文字至 team+ 推播 API，欄位為 <c>account</c>／<c>api_key</c>／<c>chat_sn</c>／
/// <c>Content_type</c>／<c>msg_content</c>，訊息送達 <c>chat_sn</c> 指定的聊天室。
/// team+ API 為互動資通的選配模組，規格需向該公司取得；此實作依其實際串接範例
/// （API URL ＋ 帳號 ＋ API Key ＋ 聊天室編號）之欄位命名。單次送達判定（重試在
/// <see cref="NotificationService"/> 層執行）；任何未設定或網路失敗一律回 false，不丟例外。
/// </summary>
public sealed class TeamPlusNotifier
{
    /// <summary>team+ 訊息內容型別（1＝純文字）。</summary>
    internal const string ContentTypeText = "1";

    private readonly HttpClient _http;

    public TeamPlusNotifier(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>送出一則事件至 team+。需 TeamPlusEnabled＋api_url＋account＋api_key＋chat_sn。</summary>
    public async Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord record)
    {
        if (!cfg.HasTeamPlusRoute)
        {
            return false;
        }

        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["account"] = cfg.TeamPlusAccount!,
                ["api_key"] = cfg.TeamPlusApiKey!,
                ["chat_sn"] = cfg.TeamPlusChatSn!,
                ["Content_type"] = ContentTypeText,
                ["msg_content"] = ComposeMessage(record),
            });

            using var resp = await _http.PostAsync(cfg.TeamPlusApiUrl, form);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 組事件文字訊息（與 SMTP 通知同格式；純文字，team+ API 不支援快照附件）。
    /// 訊息內容沿用 SMTP 通知的中文標籤，屬伺服器端內容，不經 i18n（比照 <see cref="SmtpNotifier"/>）。
    /// </summary>
    internal static string ComposeMessage(AlarmEventRecord record)
    {
        var text =
            $"[HeliVMS] 事件類型：{record.EventType}；頻道：{record.ChannelId}；發生時間：{record.StartUtc:yyyy-MM-dd HH:mm:ss} UTC";
        if (!string.IsNullOrWhiteSpace(record.Detail))
        {
            text += $"；明細：{record.Detail}";
        }

        return text;
    }
}
