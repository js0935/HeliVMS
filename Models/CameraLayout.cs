using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class CameraLayout {
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("gridSize")] public int GridSize { get; set; } = 4;
    [JsonPropertyName("slots")] public List<string?> Slots { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
