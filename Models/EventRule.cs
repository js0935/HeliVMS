using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class EventRule {
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("conditions")] public List<RuleCondition> Conditions { get; set; } = [];
    [JsonPropertyName("actions")] public List<RuleAction> Actions { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class RuleCondition {
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("cameraIds")] public List<string> CameraIds { get; set; } = [];
    [JsonPropertyName("scheduleCron")] public string? ScheduleCron { get; set; }
    [JsonPropertyName("timeStart")] public string? TimeStart { get; set; }
    [JsonPropertyName("timeEnd")] public string? TimeEnd { get; set; }
    [JsonPropertyName("daysOfWeek")] public List<int> DaysOfWeek { get; set; } = [];
}

public class RuleAction {
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("params")] public Dictionary<string, string> Params { get; set; } = [];
}
