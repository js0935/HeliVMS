namespace HeliVMS.Models;

public class NotificationEntry {
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "INFO";
    public bool IsRead { get; set; }
}
