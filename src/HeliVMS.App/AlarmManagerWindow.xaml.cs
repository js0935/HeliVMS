using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 警報管理器（M47 §14.7 #3）：分診面板——狀態／優先序／負責人／處理時限／逾期。
/// </summary>
public partial class AlarmManagerWindow : Window
{
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _events;
    private readonly AlarmTriageRepository _triage;
    private readonly Dictionary<int, string> _channelNames = new();

    private sealed record Option(string Value, string Label);

    private sealed class BoardRow
    {
        public long EventId { get; init; }

        public string StartLabel { get; init; } = string.Empty;

        public string ChannelLabel { get; init; } = string.Empty;

        public string TypeLabel { get; init; } = string.Empty;

        public string Status { get; init; } = AlarmEventStatus.Pending;

        public string Priority { get; init; } = AlarmPriority.Normal;

        public string StatusLabel { get; init; } = string.Empty;

        public string PriorityLabel { get; init; } = string.Empty;

        public string OwnerLabel { get; init; } = string.Empty;

        public string DueLabel { get; init; } = string.Empty;

        public string OverdueLabel { get; init; } = string.Empty;
    }

    public AlarmManagerWindow(SqliteStore store)
    {
        _store = store;
        _events = new AlarmEventRepository(store);
        _triage = new AlarmTriageRepository(store);
        InitializeComponent();

        foreach (var ch in new ChannelRepository(store).List())
        {
            _channelNames[ch.Id] = ch.Name;
        }

        DispositionCombo.ItemsSource = AlarmEventStatus.All
            .Select(s => new Option(s, AlarmEventStatus.Label(s)))
            .ToList();
        PriorityCombo.ItemsSource = AlarmPriority.All
            .Select(p => new Option(p, AlarmPriority.Label(p)))
            .ToList();

        Refresh();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Refresh();
        ManagerStatusText.Text = "已重新整理。";
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        var summary = _triage.Summarize(now);
        SummaryText.Text = $"待處理 {summary.Pending}／已確認 {summary.Acknowledged}／" +
                           $"已處理 {summary.Actioned}／誤報 {summary.FalseAlarm}／逾期 {summary.Overdue}";

        var rows = _triage.ListBoard(now).Select(r => new BoardRow
        {
            EventId = r.EventId,
            Status = r.Status,
            Priority = r.Priority,
            StartLabel = r.StartUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ChannelLabel = _channelNames.TryGetValue(r.ChannelId, out var name) ? name : $"#{r.ChannelId}",
            TypeLabel = r.EventType,
            StatusLabel = AlarmEventStatus.Label(r.Status),
            PriorityLabel = AlarmPriority.Label(r.Priority),
            OwnerLabel = r.Owner ?? r.AssignedTo ?? string.Empty,
            DueLabel = r.DueUtc is { } due
                ? due.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty,
            OverdueLabel = r.IsOverdue ? "是" : string.Empty,
        }).ToList();

        BoardList.ItemsSource = rows;
        if (rows.Count > 0 && BoardList.SelectedIndex < 0)
        {
            BoardList.SelectedIndex = 0;
        }
    }

    private void OnBoardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoardList.SelectedItem is not BoardRow row)
        {
            return;
        }

        DispositionCombo.SelectedValue = row.Status;
        PriorityCombo.SelectedValue = row.Priority;

        var triage = _triage.Get(row.EventId);
        OwnerBox.Text = triage?.Owner ?? string.Empty;
        NoteBox.Text = string.Empty;
        if (triage?.DueUtc is { } due)
        {
            DueDate.SelectedDate = due.ToLocalTime().Date;
            DueTime.Text = due.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            DueDate.SelectedDate = null;
            DueTime.Text = "00:00:00";
        }
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (BoardList.SelectedItem is not BoardRow row)
        {
            ManagerStatusText.Text = "請先於清單選取事件。";
            return;
        }

        var status = DispositionCombo.SelectedValue as string ?? AlarmEventStatus.Pending;
        var priority = PriorityCombo.SelectedValue as string ?? AlarmPriority.Normal;
        var owner = string.IsNullOrWhiteSpace(OwnerBox.Text) ? null : OwnerBox.Text.Trim();
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

        DateTime? dueUtc = null;
        if (DueDate.SelectedDate is { } date)
        {
            if (!TimeSpan.TryParse(DueTime.Text, CultureInfo.InvariantCulture, out var time))
            {
                time = TimeSpan.Zero;
            }

            dueUtc = date.Add(time).ToUniversalTime();
        }

        var now = DateTime.UtcNow;
        _events.SetDisposition(row.EventId, status, owner, note, now);
        _triage.SetTriage(row.EventId, priority, dueUtc, owner, now);

        ManagerStatusText.Text =
            $"已更新事件 #{row.EventId}（狀態={AlarmEventStatus.Label(status)}、優先序={AlarmPriority.Label(priority)}）。";
        Refresh();
    }
}