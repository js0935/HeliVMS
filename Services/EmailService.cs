using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class EmailService : IEmailService {
    private readonly string _configPath;
    private NotificationSettings _settings;

    public EmailService() {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "notification_settings.json");
        _settings = LoadFromDisk();
    }

    public NotificationSettings LoadSettings() => _settings;

    public void SaveSettings(NotificationSettings settings) {
        _settings = settings;
        SaveToDisk();
    }

    public async Task<bool> SendAsync(string to, string subject, string body) {
        if (!_settings.Email.Enabled)
            return false;

        try {
            using var client = new SmtpClient(_settings.Email.SmtpHost, _settings.Email.SmtpPort) {
                EnableSsl = _settings.Email.UseSsl,
                Credentials = string.IsNullOrEmpty(_settings.Email.Username)
                    ? null
                    : new NetworkCredential(_settings.Email.Username, _settings.Email.Password),
                Timeout = 10000,
            };

            using var msg = new MailMessage(_settings.Email.FromAddress, to, subject, body);
            await client.SendMailAsync(msg).ConfigureAwait(false);
            return true;
        } catch {
            return false;
        }
    }

    public async Task<bool> SendToRecipientsAsync(string subject, string body) {
        var recipients = _settings.Email.DefaultRecipients;
        if (string.IsNullOrWhiteSpace(recipients))
            return false;

        var toList = recipients.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (toList.Length == 0)
            return false;

        var success = true;
        foreach (var to in toList) {
            if (!await SendAsync(to, subject, body).ConfigureAwait(false))
                success = false;
        }
        return success;
    }

    public async Task<bool> TestConnectionAsync() {
        if (string.IsNullOrWhiteSpace(_settings.Email.SmtpHost))
            return false;

        try {
            using var client = new SmtpClient(_settings.Email.SmtpHost, _settings.Email.SmtpPort) {
                EnableSsl = _settings.Email.UseSsl,
                Credentials = string.IsNullOrEmpty(_settings.Email.Username)
                    ? null
                    : new NetworkCredential(_settings.Email.Username, _settings.Email.Password),
                Timeout = 5000,
            };
            using var msg = new MailMessage(_settings.Email.FromAddress,
                _settings.Email.FromAddress, "HeliVMS 測試", "此為 HeliVMS 發送的測試郵件");
            await client.SendMailAsync(msg).ConfigureAwait(false);
            return true;
        } catch {
            return false;
        }
    }

    private NotificationSettings LoadFromDisk() {
        try {
            if (!File.Exists(_configPath)) return new NotificationSettings();
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<NotificationSettings>(json) ?? new NotificationSettings();
        } catch {
            return new NotificationSettings();
        }
    }

    private void SaveToDisk() {
        try {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        } catch { }
    }
}
