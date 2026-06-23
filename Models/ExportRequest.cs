using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class ExportRequest {
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("startTime")] public DateTime StartTime { get; set; }
    [JsonPropertyName("endTime")] public DateTime EndTime { get; set; }
    [JsonPropertyName("cameraIds")] public List<string> CameraIds { get; set; } = [];
    [JsonPropertyName("outputPath")] public string OutputPath { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "mp4";
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("progress")] public double Progress { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}
