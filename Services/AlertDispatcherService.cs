using System.Collections.Concurrent;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class AlertDispatcherService : IAlertDispatcherService, IDisposable {
    private readonly IEmailService _email;
    private readonly IPushNotificationService _push;
    private readonly INotificationService _toast;
    private readonly ConcurrentQueue<AlertNotification> _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event Action<AlertNotification>? NotificationSent;
    public event Action<AlertNotification, string>? NotificationFailed;

    public AlertDispatcherService(IEmailService email, IPushNotificationService push, INotificationService toast) {
        _email = email;
        _push = push;
        _toast = toast;
    }

    public void Enqueue(AlertNotification notification) {
        _queue.Enqueue(notification);
    }

    public void Start() {
        if (_worker is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => DispatchLoop(_cts.Token));
    }

    public void Stop() {
        _cts?.Cancel();
        _worker = null;
    }

    private async Task DispatchLoop(CancellationToken ct) {
        var settings = _email.LoadSettings();

        while (!ct.IsCancellationRequested) {
            while (_queue.TryDequeue(out var notification)) {
                if (ct.IsCancellationRequested) return;

                var ok = await DispatchOneAsync(notification, settings).ConfigureAwait(false);
                if (ok) {
                    notification.Status = "sent";
                    notification.SentAt = DateTime.Now;
                    NotificationSent?.Invoke(notification);
                } else if (notification.RetryCount < settings.RetryMaxAttempts) {
                    notification.RetryCount++;
                    notification.Status = $"retrying ({notification.RetryCount}/{settings.RetryMaxAttempts})";
                    await Task.Delay(settings.RetryDelaySeconds * 1000, ct).ConfigureAwait(false);
                    _queue.Enqueue(notification);
                } else {
                    notification.Status = "failed";
                    notification.Error = "超過最大重試次數";
                    NotificationFailed?.Invoke(notification, notification.Error);
                }
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> DispatchOneAsync(AlertNotification notification, NotificationSettings settings) {
        try {
            switch (notification.Channel.ToLowerInvariant()) {
                case "email":
                    var ok = await _email.SendToRecipientsAsync(notification.Type, notification.Message).ConfigureAwait(false);
                    if (ok) {
                        _toast.Show($"郵件已送出：{notification.Type}", "INFO");
                        return true;
                    }
                    notification.Error = "郵件發送失敗";
                    return false;

                case "push":
                    _push.ShowToast(notification.Type, notification.Message, notification.CameraId);
                    _toast.Show($"推送通知：{notification.Type}", "INFO");
                    return true;

                default:
                    notification.Error = $"不支援的通知通道：{notification.Channel}";
                    return false;
            }
        } catch (Exception ex) {
            notification.Error = ex.Message;
            return false;
        }
    }

    public void Dispose() {
        Stop();
        _cts?.Dispose();
    }
}
