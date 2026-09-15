using System.IO;
using System.Net;
using System.Net.Mail;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// SMTP 寄送器（§18.5 P1）：事件郵件文字主旨含事件明細。
/// 使用 BCL 的 <see cref="SmtpClient"/>（csproj 已 NoWarn CS0618，避免新增 MailKit 相依破壞 lock 檔）。
/// 單次送達判定（重試在 <see cref="NotificationService"/> 層執行）。快照附件＋多筆合併 M24：
/// 單封郵件可含多筆事件（`int` 多載），並針對存在的快照檔加入附件（`.png`→image/png、
/// `.jpg/.jpeg`→image/jpeg）。
/// </summary>
public sealed class SmtpNotifier
{
    public Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord record)
        => SendAsync(cfg, new[] { record });

    public async Task<bool> SendAsync(NotificationSettings cfg, IReadOnlyList<AlarmEventRecord> records)
    {
        if (!cfg.SmtpEnabled ||
            string.IsNullOrWhiteSpace(cfg.SmtpHost) ||
            string.IsNullOrWhiteSpace(cfg.SmtpFrom) ||
            cfg.SmtpTo.Count == 0 ||
            records.Count == 0)
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

            var subject = records.Count == 1
                ? $"HeliVMS 事件（頻道 {records[0].ChannelId}）：{records[0].EventType}"
                : $"HeliVMS 事件（{records.Count} 則）";

            var body = new System.Text.StringBuilder();
            foreach (var record in records)
            {
                body.Append($"事件類型：{record.EventType}\n");
                body.Append($"頻道：{record.ChannelId}\n");
                body.Append($"發生時間：{record.StartUtc:yyyy-MM-dd HH:mm:ss} UTC\n");
                if (record.EndUtc.HasValue)
                {
                    body.Append($"結束時間：{record.EndUtc:yyyy-MM-dd HH:mm:ss} UTC\n");
                }

                if (!string.IsNullOrWhiteSpace(record.Detail))
                {
                    body.Append($"明細：{record.Detail}\n");
                }

                if (!string.IsNullOrWhiteSpace(record.SnapshotPath))
                {
                    body.Append($"快照：{record.SnapshotPath}\n");
                }

                body.Append('\n');
            }

            using var msg = new MailMessage(
                cfg.SmtpFrom,
                string.Join(",", cfg.SmtpTo),
                subject,
                body.ToString());

            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.SnapshotPath) || !File.Exists(record.SnapshotPath))
                {
                    continue;
                }

                var ext = Path.GetExtension(record.SnapshotPath).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    _ => System.Net.Mime.MediaTypeNames.Application.Octet,
                };
                msg.Attachments.Add(new Attachment(record.SnapshotPath, mime));
            }

            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}