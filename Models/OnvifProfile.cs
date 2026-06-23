namespace HeliVMS.Models;

/// <summary>ONVIF media profile</summary>
public class OnvifProfile {
    public string Token { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RtspUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameRate { get; set; }
    public string Encoding { get; set; } = string.Empty;
}

/// <summary>ONVIF discovery result</summary>
public class OnvifDiscoveryResult {
    public string? IpAddress { get; set; }
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public List<OnvifProfile> Profiles { get; set; } = [];
    public int ProbedPort { get; set; } = 80;
}

/// <summary>ONVIF PTZ preset position</summary>
public class OnvifPreset {
    public string Token { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>ONVIF PTZ real-time status</summary>
public class OnvifPTZStatus {
    public float Pan { get; set; }
    public float Tilt { get; set; }
    public float Zoom { get; set; }
}

/// <summary>ONVIF PTZ configuration</summary>
public class OnvifPTZConfiguration {
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}
