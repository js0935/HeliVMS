using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class LayoutTab {
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "新佈局";
    [JsonPropertyName("slotCount")] public int SlotCount { get; set; } = 4;
    [JsonPropertyName("cameras")] public List<string?> CameraIds { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
}
