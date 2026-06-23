using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IEventService {
    void LogEvent(SystemEvent evt);
    void Log(string severity, string category, string source, string message, string detail = "", string user = "");
    void LogInfo(string category, string source, string message, string detail = "");
    void LogWarning(string category, string source, string message, string detail = "");
    void LogError(string category, string source, string message, string detail = "");
    List<SystemEvent> GetRecentEvents(int count = 200);
    List<SystemEvent> QueryEvents(string? category = null, string? severity = null, string? keyword = null, int count = 200);
    void ClearEvents();
}
