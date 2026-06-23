using System.Linq;
using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IAnalyticsPlugin {
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsEnabled { get; set; }
    AnalyticsResult? Analyze(string cameraId, string imageBase64);
}

public class AnalyticsResult {
    public string PluginId { get; set; } = "";
    public string CameraId { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public double Confidence { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class LprAnalyticsPlugin : IAnalyticsPlugin {
    public string Id => "heli.lpr";
    public string DisplayName => "車牌辨識 (LPR)";
    public string Description => "透過影像分析偵測車輛車牌號碼。";
    public bool IsEnabled { get; set; } = true;

    public AnalyticsResult? Analyze(string cameraId, string imageBase64) {
        return new AnalyticsResult {
            PluginId = Id,
            CameraId = cameraId,
            Label = "ABC-1234",
            Confidence = 0.0,
            Metadata = new() { ["note"] = "Stub — requires ML model deployment" },
        };
    }
}

public sealed class PosAnalyticsPlugin : IAnalyticsPlugin {
    public string Id => "heli.pos";
    public string DisplayName => "POS 交易辨識";
    public string Description => "從畫面疊加層擷取 POS 交易明細。";
    public bool IsEnabled { get; set; } = true;

    public AnalyticsResult? Analyze(string cameraId, string imageBase64) {
        return new AnalyticsResult {
            PluginId = Id,
            CameraId = cameraId,
            Label = "POS Transaction",
            Confidence = 0.0,
            Metadata = new() { ["note"] = "Stub — requires OCR model deployment" },
        };
    }
}

public sealed class AnalyticsPluginService {
    private readonly IEventService _events;
    private readonly List<IAnalyticsPlugin> _plugins;

    public IReadOnlyList<IAnalyticsPlugin> Plugins => _plugins;

    public AnalyticsPluginService(IEventService events) {
        _events = events;
        _plugins = [
            new LprAnalyticsPlugin(),
            new PosAnalyticsPlugin(),
        ];
    }

    public void RunAnalytics(string cameraId, string imageBase64) {
        foreach (var plugin in _plugins) {
            if (!plugin.IsEnabled) continue;
            try {
                var result = plugin.Analyze(cameraId, imageBase64);
                if (result is null || result.Confidence < 0.3) continue;

                _events.LogInfo("Analytics", plugin.Id,
                    $"[{plugin.DisplayName}] {result.Label} (conf={result.Confidence:P1})",
                    $"Camera={cameraId}, Meta={string.Join("; ", result.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
            } catch (Exception ex) {
                _events.LogError("Analytics", plugin.Id, $"Analytics error: {ex.Message}");
            }
        }
    }
}
