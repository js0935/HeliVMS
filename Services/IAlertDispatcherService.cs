using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IAlertDispatcherService {
    void Enqueue(AlertNotification notification);
    void Start();
    void Stop();
    event Action<AlertNotification>? NotificationSent;
    event Action<AlertNotification, string>? NotificationFailed;
}
