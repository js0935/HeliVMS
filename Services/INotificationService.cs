namespace HeliVMS.Services;

public interface INotificationService
{
    void Show(string message, string severity = "INFO");
    event Action<(string Message, string Severity)>? NotificationReceived;
}
