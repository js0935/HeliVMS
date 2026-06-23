using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IEmailService {
    Task<bool> SendAsync(string to, string subject, string body);
    Task<bool> SendToRecipientsAsync(string subject, string body);
    NotificationSettings LoadSettings();
    void SaveSettings(NotificationSettings settings);
    Task<bool> TestConnectionAsync();
}
