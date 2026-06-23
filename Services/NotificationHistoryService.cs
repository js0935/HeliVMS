using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class NotificationHistoryService {
    private readonly List<NotificationEntry> _entries = [];
    private const int MaxEntries = 500;

    public IReadOnlyList<NotificationEntry> Entries => _entries.AsReadOnly();

    public event Action? Updated;

    public void Add(string title, string message, string severity = "INFO") {
        if (_entries.Count >= MaxEntries) {
            _entries.RemoveAt(0);
        }
        _entries.Add(new NotificationEntry {
            Title = title,
            Message = message,
            Severity = severity,
        });
        Updated?.Invoke();
    }

    public void MarkAsRead(Guid id) {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is not null) {
            entry.IsRead = true;
            Updated?.Invoke();
        }
    }

    public void MarkAllAsRead() {
        foreach (var e in _entries) e.IsRead = true;
        Updated?.Invoke();
    }

    public int UnreadCount => _entries.Count(e => !e.IsRead);

    public void Clear() {
        _entries.Clear();
        Updated?.Invoke();
    }
}
