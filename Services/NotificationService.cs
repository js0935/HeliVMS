using Serilog;

namespace HeliVMS.Services;

public sealed class NotificationService : INotificationService
{
    public event Action<(string Message, string Severity)>? NotificationReceived;

    public void Show(string message, string severity = "INFO")
    {
        Log.Debug("[HeliVMS] Notification: [{Severity}] {Message}", severity, message);
        NotificationReceived?.Invoke((message, severity));
    }
}
