using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class EMapCameraPosition {
    [JsonPropertyName("cameraId")] public string CameraId { get; set; } = string.Empty;
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

public class EMapFloor {
    [JsonPropertyName("name")] public string Name { get; set; } = "1F";
    [JsonPropertyName("backgroundImagePath")] public string? BackgroundImagePath { get; set; }
    [JsonPropertyName("zoomLevel")] public double ZoomLevel { get; set; } = 1.0;
    [JsonPropertyName("offsetX")] public double OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double OffsetY { get; set; }
    [JsonPropertyName("cameras")] public List<EMapCameraPosition> Cameras { get; set; } = [];
}

public class EMapData {
    [JsonPropertyName("floors")] public List<EMapFloor> Floors { get; set; } = [new()];
    [JsonPropertyName("activeFloorIndex")] public int ActiveFloorIndex { get; set; }

    [JsonPropertyName("backgroundImagePath")]
    public string? BackgroundImagePath {
        get => Floors.Count > 0 ? Floors[0].BackgroundImagePath : null;
        set { if (Floors.Count > 0) Floors[0].BackgroundImagePath = value; }
    }
    [JsonPropertyName("zoomLevel")]
    public double ZoomLevel {
        get => Floors.Count > 0 ? Floors[ActiveFloorIndex].ZoomLevel : 1.0;
        set { if (Floors.Count > 0) Floors[ActiveFloorIndex].ZoomLevel = value; }
    }
    [JsonPropertyName("offsetX")]
    public double OffsetX {
        get => Floors.Count > 0 ? Floors[ActiveFloorIndex].OffsetX : 0;
        set { if (Floors.Count > 0) Floors[ActiveFloorIndex].OffsetX = value; }
    }
    [JsonPropertyName("offsetY")]
    public double OffsetY {
        get => Floors.Count > 0 ? Floors[ActiveFloorIndex].OffsetY : 0;
        set { if (Floors.Count > 0) Floors[ActiveFloorIndex].OffsetY = value; }
    }
    [JsonPropertyName("cameras")]
    public List<EMapCameraPosition> Cameras {
        get => Floors.Count > 0 ? Floors[ActiveFloorIndex].Cameras : [];
        set { if (Floors.Count > 0) Floors[ActiveFloorIndex].Cameras = value; }
    }
}
