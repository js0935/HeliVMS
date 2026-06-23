namespace HeliVMS.Models;

public class CameraGroup {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4CAF50";
    public List<string> CameraIds { get; set; } = [];
}

public class CameraGroupData {
    public List<CameraGroup> Groups { get; set; } = [];
}
