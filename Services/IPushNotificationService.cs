namespace HeliVMS.Services;

public interface IPushNotificationService {
    void ShowToast(string title, string message, string? cameraId = null);
}
