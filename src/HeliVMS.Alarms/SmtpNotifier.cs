using System.Net;
using System.Net.Mail;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// SMTP 寄送器（§18.5 P1）：事件郵件文字主旨含事件明細。
/// 使用 BCL 的 <see cref="SmtpClient"/>（csproj 已 NoWarn CS0618，避免新增 MailKit 相依破壞 lock 檔）。
/// 單次送達判定（重試在 <see cref="NotificationService"/> 層執行）。快照附件 M23。
/// </summary>
public sealed class SmtpNotifier
{
    public async Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord record)
    {
        if (!cfg.SmtpEnabled ||
            string.IsNullOrWhiteSpace(cfg.SmtpHost) ||
            string.IsNullOrWhiteSpace(cfg.SmtpFrom) ||
            cfg.SmtpTo.Count == 0)
        {
            return false;
        }

        try
        {
            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.SmtpPort == 465,
                Timeout = 10_000,
            };

            if (!string.IsNullOrWhiteSpace(cfg.SmtpUser))
            {
                client.Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword ?? string.Empty);
            }

            var subject = $"HeliVMS 事件（頻道 {record.ChannelId}）：{record.EventType}";
            var body = $"事件類型：{record.EventType}\n" +
                       $"頻道：{record.ChannelId}\n" +
                       $"發生時間：{record.StartUtc:yyyy-MM-dd HH:mm:ss} UTC\n" +
                       (record.EndUtc.HasValue ? $"結束時間：{record.EndUtc:yyyy-MM-dd HH:mm:ss} UTC\n" : string.Empty) +
                       (!string.IsNullOrWhiteSpace(record.Detail) ? $"明細：{record.Detail}\n" : string.Empty) +
                       (!string.IsNullOrWhiteSpace(record.SnapshotPath) ? $"快照：{record.SnapshotPath}\n" : string.Empty);

            using var msg = new MailMessage(
                cfg.SmtpFrom,
                string.Join(",", cfg.SmtpTo),
                subject,
                body);
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}