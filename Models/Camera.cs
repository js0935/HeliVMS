using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class Camera
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("cameraId")]
    public string CameraId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 554;

    [JsonPropertyName("onvifPort")]
    public int OnvifPort { get; set; } = 80;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("rtspUrl")]
    public string RtspUrl { get; set; } = string.Empty;

    [JsonPropertyName("rtspUrlSub")]
    public string? RtspUrlSub { get; set; }

    [JsonPropertyName("onvifResolvedUrl")]
    public string? OnvifResolvedUrl { get; set; }

    [JsonPropertyName("onvifResolvedUrlSub")]
    public string? OnvifResolvedUrlSub { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; } = true;

    [JsonPropertyName("hasPTZ")]
    public bool HasPTZ { get; set; }

    [JsonPropertyName("isRecordingEnabled")]
    public bool IsRecordingEnabled { get; set; }

    [JsonPropertyName("isAlarmEnabled")]
    public bool IsAlarmEnabled { get; set; }

    [JsonPropertyName("isMotionDetectionEnabled")]
    public bool IsMotionDetectionEnabled { get; set; }

    [JsonPropertyName("recordingConfigJson")]
    public string? RecordingConfigJson { get; set; }

    [JsonPropertyName("channelNumber")]
    public int? ChannelNumber { get; set; }

    [JsonPropertyName("gridOrder")]
    public int GridOrder { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsConnected { get; set; }

    [JsonIgnore]
    public int DisconnectCount { get; set; }

    [JsonIgnore]
    public string? LastError { get; set; }

    [JsonIgnore]
    public DateTime? FirstConnectedAt { get; set; }

    [JsonIgnore]
    public DateTime? LastConnectedAt { get; set; }

    [JsonIgnore]
    public DateTime? LastDisconnectedAt { get; set; }

    [JsonIgnore]
    public TimeSpan TotalUptime => FirstConnectedAt.HasValue && IsConnected
        ? DateTime.Now - FirstConnectedAt.Value
        : TimeSpan.Zero;
}
