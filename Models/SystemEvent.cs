using System.Text.Json.Serialization;

namespace HeliVMS.Models;

public class SystemEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "INFO";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public static class EventCategories
{
    public const string Operation = "操作";
    public const string Setting = "設定";
    public const string Connection = "連線";
    public const string Alarm = "警報";
    public const string Playback = "調閱";
    public const string Create = "新增";
    public const string Update = "修改";
    public const string Delete = "刪除";
    public const string Debug = "除錯";
    public const string Security = "安全";
    public const string System = "系統";
}
