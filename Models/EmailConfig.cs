using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class EmailConfig {
    [JsonPropertyName("smtpHost")] public string SmtpHost { get; set; } = "";
    [JsonPropertyName("smtpPort")] public int SmtpPort { get; set; } = 587;
    [JsonPropertyName("useSsl")] public bool UseSsl { get; set; } = true;
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("fromAddress")] public string FromAddress { get; set; } = "";
    [JsonPropertyName("defaultRecipients")] public string DefaultRecipients { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public class PushConfig {
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("showCameraName")] public bool ShowCameraName { get; set; } = true;
}

public class NotificationSettings {
    [JsonPropertyName("email")] public EmailConfig Email { get; set; } = new();
    [JsonPropertyName("push")] public PushConfig Push { get; set; } = new();
    [JsonPropertyName("retryMaxAttempts")] public int RetryMaxAttempts { get; set; } = 3;
    [JsonPropertyName("retryDelaySeconds")] public int RetryDelaySeconds { get; set; } = 30;
}
