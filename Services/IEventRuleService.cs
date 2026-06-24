using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IEventRuleService {
    void AddRule(EventRule rule);
    void UpdateRule(EventRule rule);
    void DeleteRule(string ruleId);
    List<EventRule> GetAllRules();
    EventRule? GetRuleById(string ruleId);
    void Evaluate(string eventType, string cameraId, Dictionary<string, string>? context = null);
    void TestRule(string ruleId);
    event Action<EventRule, RuleAction, string>? ActionExecuted;
}
