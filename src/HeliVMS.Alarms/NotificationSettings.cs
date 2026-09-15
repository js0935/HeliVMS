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
    string? SmtpPassword,
    string? QuietStart,
    string? QuietEnd,
    bool QuietRetransmit = false,
    bool MqttEnabled = false,
    string? MqttHost = null,
    int MqttPort = 1883,
    string? MqttTopic = null,
    string? MqttUser = null,
    string? MqttPassword = null)
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
    public const string QuietStartKey = "notify.quiet.start";
    public const string QuietEndKey = "notify.quiet.end";
    public const string QuietRetransmitKey = "notify.quiet.retransmit";
    public const string MqttEnabledKey = "notify.mqtt.enabled";
    public const string MqttHostKey = "notify.mqtt.host";
    public const string MqttPortKey = "notify.mqtt.port";
    public const string MqttTopicKey = "notify.mqtt.topic";
    public const string MqttUserKey = "notify.mqtt.user";
    public const string MqttPasswordKey = "notify.mqtt.password";

    /// <summary>是否有任一外送通道已設定。</summary>
    public bool AnyChannelConfigured =>
        !string.IsNullOrWhiteSpace(WebhookUrl) ||
        (SmtpEnabled && !string.IsNullOrWhiteSpace(SmtpHost) && SmtpTo.Count > 0) ||
        (MqttEnabled && !string.IsNullOrWhiteSpace(MqttHost) && !string.IsNullOrWhiteSpace(MqttTopic));

    /// <summary>MQTT 通道是否可送（enabled＋host＋topic）。</summary>
    public bool HasMqttRoute => MqttEnabled && !string.IsNullOrWhiteSpace(MqttHost) && !string.IsNullOrWhiteSpace(MqttTopic);

    /// <summary>是否位於靜默時段（notify.quiet.*，本地 24h 制 "HH:mm"；支援跨午夜）。</summary>
    public bool IsInQuietHours(DateTime now)
    {
        if (string.IsNullOrWhiteSpace(QuietStart) || string.IsNullOrWhiteSpace(QuietEnd))
        {
            return false;
        }

        if (!TimeOnly.TryParseExact(QuietStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(QuietEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return false;
        }

        if (start == end)
        {
            return false;   // 起訖相同＝不啟用
        }

        var t = TimeOnly.FromDateTime(now);
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>本次（now 於靜默中）靜默結束的本地時刻（跨午夜含兩型），轉換為 UTC。</summary>
    public DateTime QuietEndUtc(DateTime now)
    {
        if (!IsInQuietHours(now) ||
            !TimeOnly.TryParseExact(QuietStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(QuietEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return DateTime.UtcNow;
        }

        DateTime localEnd;
        if (start < end)
        {
            localEnd = now.Date.Add(end.ToTimeSpan());
        }
        else if (TimeOnly.FromDateTime(now) >= start)
        {
            localEnd = now.Date.AddDays(1).Add(end.ToTimeSpan());   // 晚上段→明日天亮
        }
        else
        {
            localEnd = now.Date.Add(end.ToTimeSpan());               // 凌晨段→今日天亮
        }

        return TimeZoneInfo.ConvertTimeToUtc(localEnd);
    }

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
        var quietStart = Str(settings, QuietStartKey, "HELIVMS_NOTIFY_QUIET_START", null);
        var quietEnd = Str(settings, QuietEndKey, "HELIVMS_NOTIFY_QUIET_END", null);
        var quietRetransmit = Bool(
            Str(settings, QuietRetransmitKey, "HELIVMS_NOTIFY_QUIET_RETRANSMIT", "false")!,
            false);
        var mqttEnabled = Bool(Str(settings, MqttEnabledKey, "HELIVMS_MQTT_ENABLED", "false")!, false);
        var mqttHost = Str(settings, MqttHostKey, "HELIVMS_MQTT_HOST", null);
        var mqttPort = Int(Str(settings, MqttPortKey, "HELIVMS_MQTT_PORT", "1883")!, 1883);
        var mqttTopic = Str(settings, MqttTopicKey, "HELIVMS_MQTT_TOPIC", null);
        var mqttUser = Str(settings, MqttUserKey, "HELIVMS_MQTT_USER", null);
        var mqttPasswordStored = Str(settings, MqttPasswordKey, "HELIVMS_MQTT_PASSWORD", null);

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
            string.IsNullOrEmpty(passwordStored) ? null : SecretProtector.Unprotect(passwordStored),
            quietStart,
            quietEnd,
            quietRetransmit,
            mqttEnabled,
            mqttHost,
            mqttPort,
            mqttTopic,
            mqttUser,
            string.IsNullOrEmpty(mqttPasswordStored) ? null : SecretProtector.Unprotect(mqttPasswordStored));
    }
}