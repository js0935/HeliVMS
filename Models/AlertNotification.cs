using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class AlertNotification {
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("cameraId")] public string? CameraId { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("retryCount")] public int RetryCount { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("sentAt")] public DateTime? SentAt { get; set; }
}
