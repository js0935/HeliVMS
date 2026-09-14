using System.Globalization;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>通知中心組態（notify.* 鍵；優先序：DB → 環境變數 → 預設值）。</summary>
public sealed record NotificationSettings(
    bool Enabled,
    string? WebhookUrl,
    bool SmtpEnabled,
    string? SmtpHost,
    int SmtpPort,
    string? SmtpFrom,
    IReadOnlyList<string> SmtpTo,
    string? SmtpUser,
    string? SmtpPassword)
{
    public const string EnabledKey = "notify.enabled";
    public const string WebhookUrlKey = "notify.webhook.url";
    public const string SmtpEnabledKey = "notify.smtp.enabled";
    public const string SmtpHostKey = "notify.smtp.host";
    public const string SmtpPortKey = "notify.smtp.port";
    public const string SmtpFromKey = "notify.smtp.from";
    public const string SmtpToKey = "notify.smtp.to";
    public const string SmtpUserKey = "notify.smtp.user";
    public const string SmtpPasswordKey = "notify.smtp.password";

    /// <summary>是否有任一外送通道已設定。</summary>
    public bool AnyChannelConfigured =>
        !string.IsNullOrWhiteSpace(WebhookUrl) ||
        (SmtpEnabled && !string.IsNullOrWhiteSpace(SmtpHost) && SmtpTo.Count > 0);

    /// <summary>從 app_settings 讀取（DB → HELIVMS_* 環境變數 → 預設）。</summary>
    public static NotificationSettings Load(SettingsRepository settings)
    {
        static string? Str(SettingsRepository s, string key, string env, string? def)
        {
            var fromDb = s.Get(key);
            if (!string.IsNullOrWhiteSpace(fromDb))
            {
                return fromDb;
            }

            var fromEnv = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(fromEnv) ? def : fromEnv;
        }

        static bool Bool(string raw, bool def)
        {
            if (bool.TryParse(raw, out var v))
            {
                return v;
            }

            return def;
        }

        static int Int(string raw, int def)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            return def;
        }

        var enabled = Bool(Str(settings, EnabledKey, "HELIVMS_NOTIFY_ENABLED", "true")!, true);
        var webhook = Str(settings, WebhookUrlKey, "HELIVMS_WEBHOOK_URL", null);
        var smtpEnabled = Bool(Str(settings, SmtpEnabledKey, "HELIVMS_SMTP_ENABLED", "false")!, false);
        var host = Str(settings, SmtpHostKey, "HELIVMS_SMTP_HOST", null);
        var port = Int(Str(settings, SmtpPortKey, "HELIVMS_SMTP_PORT", "587")!, 587);
        var from = Str(settings, SmtpFromKey, "HELIVMS_SMTP_FROM", null);
        var toRaw = Str(settings, SmtpToKey, "HELIVMS_SMTP_TO", null);
        var user = Str(settings, SmtpUserKey, "HELIVMS_SMTP_USER", null);
        var passwordStored = Str(settings, SmtpPasswordKey, "HELIVMS_SMTP_PASSWORD", null);

        var to = (toRaw ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new NotificationSettings(
            enabled,
            webhook,
            smtpEnabled,
            host,
            port,
            from,
            to,
            user,
            string.IsNullOrEmpty(passwordStored) ? null : SecretProtector.Unprotect(passwordStored));
    }
}