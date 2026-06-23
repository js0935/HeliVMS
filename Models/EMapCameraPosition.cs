namespace HeliVMS.Models;

public class EMapCameraPosition {
    public string CameraId { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
}

public class EMapData {
    public string? BackgroundImagePath { get; set; }
    public double ZoomLevel { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public List<EMapCameraPosition> Cameras { get; set; } = [];
}
