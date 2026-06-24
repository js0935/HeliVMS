using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class EventRuleService : IEventRuleService {
    private readonly List<EventRule> _rules = [];
    private readonly string _filePath;
    private readonly IEventService _eventLog;
    private readonly IAlertDispatcherService _alertDispatcher;
    private readonly IPushNotificationService _push;
    private readonly IRecordingService _recording;
    private readonly ICameraService _cameraService;
    private readonly object _lock = new();

    public event Action<EventRule, RuleAction, string>? ActionExecuted;

    public EventRuleService(IEventService eventLog, IAlertDispatcherService alertDispatcher, IPushNotificationService push, IRecordingService recording, ICameraService cameraService) {
        _eventLog = eventLog;
        _alertDispatcher = alertDispatcher;
        _push = push;
        _recording = recording;
        _cameraService = cameraService;
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "event_rules.json");
        LoadFromDisk();
    }

    public void AddRule(EventRule rule) {
        lock (_lock) {
            _rules.Add(rule);
            SaveToDisk();
        }
        _eventLog.LogInfo("事件規則", "EventRule", $"新增規則「{rule.Name}」");
    }

    public void UpdateRule(EventRule rule) {
        lock (_lock) {
            var idx = _rules.FindIndex(r => r.Id == rule.Id);
            if (idx < 0) return;
            rule.UpdatedAt = DateTime.Now;
            _rules[idx] = rule;
            SaveToDisk();
        }
    }

    public void DeleteRule(string ruleId) {
        lock (_lock) {
            var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule is null) return;
            _rules.Remove(rule);
            SaveToDisk();
        }
    }

    public List<EventRule> GetAllRules() {
        lock (_lock) return [.. _rules];
    }

    public EventRule? GetRuleById(string ruleId) {
        lock (_lock) return _rules.FirstOrDefault(r => r.Id == ruleId);
    }

    public void TestRule(string ruleId) {
        var rule = GetRuleById(ruleId);
        if (rule is null) return;
        _eventLog.LogInfo("事件規則", "RuleTest", $"測試規則「{rule.Name}」");
        foreach (var action in rule.Actions)
            ExecuteAction(rule, action, rule.Conditions.FirstOrDefault()?.CameraIds.FirstOrDefault() ?? "", null);
    }

    public void Evaluate(string eventType, string cameraId, Dictionary<string, string>? context = null) {
        List<EventRule> matched;
        lock (_lock) {
            matched = _rules.Where(r => r.Enabled && ConditionsMatch(r, eventType, cameraId)).ToList();
        }
        foreach (var rule in matched) {
            foreach (var action in rule.Actions) {
                ExecuteAction(rule, action, cameraId, context);
            }
        }
    }

    private static bool ConditionsMatch(EventRule rule, string eventType, string cameraId) {
        if (rule.Conditions.Count == 0) return false;
        foreach (var c in rule.Conditions) {
            if (!string.IsNullOrEmpty(c.Type) && !c.Type.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                return false;
            if (c.CameraIds.Count > 0 && !c.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(c.TimeStart) || !string.IsNullOrEmpty(c.TimeEnd)) {
                var now = DateTime.Now.TimeOfDay;
                if (TimeSpan.TryParse(c.TimeStart, out var start) && now < start) return false;
                if (TimeSpan.TryParse(c.TimeEnd, out var end) && now > end) return false;
            }
            if (c.DaysOfWeek.Count > 0 && !c.DaysOfWeek.Contains((int)DateTime.Now.DayOfWeek))
                return false;
        }
        return true;
    }

    private void ExecuteAction(EventRule rule, RuleAction action, string cameraId, Dictionary<string, string>? context) {
        try {
            switch (action.Type.ToLowerInvariant()) {
                case "logevent":
                    _eventLog.LogWarning("事件規則", "RuleEngine",
                        $"規則「{rule.Name}」觸發：{cameraId} → {action.Type}");
                    break;
                case "httpwebhook":
                    FireWebhook(rule, action, cameraId, context);
                    break;
                case "email":
                    FireEmailNotification(rule, cameraId, context);
                    break;
                case "push":
                    FirePushNotification(rule, cameraId, context);
                    break;
                case "startrecording": {
                        var targetCam = _cameraService.GetCameraById(cameraId);
                        if (targetCam is not null) _recording.StartRecording(targetCam);
                        break;
                    }
                case "stoprecording":
                    _recording.StopRecording(cameraId);
                    break;
            }
            ActionExecuted?.Invoke(rule, action, cameraId);
        } catch (Exception ex) {
            Serilog.Log.Debug("[EventRule] ExecuteAction failed: {Msg}", ex.Message);
        }
    }

    private void FireEmailNotification(EventRule rule, string cameraId, Dictionary<string, string>? context) {
        _alertDispatcher.Enqueue(new Models.AlertNotification {
            Channel = "email",
            Type = rule.Name,
            CameraId = cameraId,
            Message = $"規則「{rule.Name}」觸發 | 攝影機：{cameraId} | 時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        });
    }

    private void FirePushNotification(EventRule rule, string cameraId, Dictionary<string, string>? context) {
        _push.ShowToast(rule.Name, $"Camera: {cameraId}", cameraId);
    }

    private static void FireWebhook(EventRule rule, RuleAction action, string cameraId, Dictionary<string, string>? context) {
        var url = action.Params.GetValueOrDefault("url", "");
        if (string.IsNullOrEmpty(url)) return;
        _ = Task.Run(async () => {
            try {
                var payload = JsonSerializer.Serialize(new {
                    rule = rule.Name,
                    cameraId,
                    eventType = context?.GetValueOrDefault("eventType", ""),
                    timestamp = DateTime.Now
                });
                using var client = new HttpClient();
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                await client.PostAsync(url, content).ConfigureAwait(false);
            } catch { }
        });
    }

    private void LoadFromDisk() {
        try {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<EventRule>>(json);
            if (list is not null) {
                lock (_lock) {
                    _rules.Clear();
                    _rules.AddRange(list);
                }
            }
        } catch (Exception ex) {
            Serilog.Log.Debug("[EventRule] Load failed: {Msg}", ex.Message);
        }
    }

    private void SaveToDisk() {
        try {
            var json = JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        } catch (Exception ex) {
            Serilog.Log.Debug("[EventRule] Save failed: {Msg}", ex.Message);
        }
    }
}
