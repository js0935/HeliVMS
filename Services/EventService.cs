using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public class EventService : IEventService {
    private readonly ConcurrentQueue<SystemEvent> _events = new();
    private readonly string _logPath;
    private readonly Lock _fileLock = new();

    public EventService() {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        _logPath = Path.Combine(logDir, "events.jsonl");
        Directory.CreateDirectory(logDir);
        LoadEvents();
    }

    public void LogEvent(SystemEvent evt) {
        _events.Enqueue(evt);
        AppendToFile(evt);
    }

    public void Log(string severity, string category, string source, string message, string detail = "", string user = "") {
        LogEvent(new SystemEvent {
            Severity = severity,
            Category = category,
            Source = source,
            Message = message,
            Detail = detail,
            User = user,
            Timestamp = DateTime.Now
        });
    }

    public void LogInfo(string category, string source, string message, string detail = "")
        => Log("INFO", category, source, message, detail);

    public void LogWarning(string category, string source, string message, string detail = "")
        => Log("WARN", category, source, message, detail);

    public void LogError(string category, string source, string message, string detail = "")
        => Log("ERROR", category, source, message, detail);

    public List<SystemEvent> GetRecentEvents(int count = 200) {
        var eventsArray = _events.ToArray();
        var takeCount = Math.Min(count, eventsArray.Length);
        var result = new List<SystemEvent>(takeCount);
        for (var i = eventsArray.Length - 1; i >= eventsArray.Length - takeCount; i--) {
            result.Add(eventsArray[i]);
        }
        return result;
    }

    public List<SystemEvent> QueryEvents(string? category = null, string? severity = null, string? keyword = null, int count = 200) {
        var kw = !string.IsNullOrEmpty(keyword) ? keyword.ToLowerInvariant() : null;
        var snapshot = _events.ToArray();
        var maxCount = Math.Min(count, snapshot.Length);
        var result = new List<SystemEvent>(maxCount);

        for (var i = snapshot.Length - 1; i >= 0 && result.Count < maxCount; i--) {
            var e = snapshot[i];
            if (!string.IsNullOrEmpty(category) && e.Category != category) { continue; }
            if (!string.IsNullOrEmpty(severity) && e.Severity != severity) { continue; }
            if (kw is not null &&
                !e.Message.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                !e.Source.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                !e.Detail.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                !e.User.Contains(kw, StringComparison.OrdinalIgnoreCase)) { continue; }
            result.Add(e);
        }
        return result;
    }

    public void ClearEvents() {
        _events.Clear();
        lock (_fileLock) {
            try { File.WriteAllText(_logPath, string.Empty); } catch (Exception ex) { Serilog.Log.Debug("[HeliVMS] EventService.ClearEvents failed: {Msg}", ex.Message); }
        }
    }

    private void AppendToFile(SystemEvent evt) {
        lock (_fileLock) {
            try {
                var line = JsonSerializer.Serialize(evt) + Environment.NewLine;
                File.AppendAllText(_logPath, line);
            } catch (Exception ex) { Serilog.Log.Debug("[HeliVMS] EventService.AppendToFile failed: {Msg}", ex.Message); }
        }
    }

    private void LoadEvents() {
        lock (_fileLock) {
            try {
                if (!File.Exists(_logPath)) { return; }
                var lines = File.ReadAllLines(_logPath);
                foreach (var line in lines) {
                    if (string.IsNullOrWhiteSpace(line)) { continue; }
                    var evt = JsonSerializer.Deserialize<SystemEvent>(line);
                    if (evt is not null) { _events.Enqueue(evt); }
                }
            } catch (Exception ex) { Serilog.Log.Debug("[HeliVMS] EventService.LoadEvents failed: {Msg}", ex.Message); }
        }
    }
}
