using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HeliVMS.App.Services;
using HeliVMS.Shared;
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
    private readonly DispatcherTimer _autoTimer;
    private readonly AlertFeed? _feed;

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

    public AlarmManagerWindow(SqliteStore store, AlertBroadcastHub? alerts = null)
    {
        _store = store;
        _events = new AlarmEventRepository(store);
        _triage = new AlarmTriageRepository(store);
        InitializeComponent();
        _feed = alerts is null ? null : new AlertFeed(alerts, Dispatcher, Refresh);
        ApplyI18n();

        foreach (var ch in new ChannelRepository(store).List())
        {
            _channelNames[ch.Id] = ch.Name;
        }

        DispositionCombo.ItemsSource = AlarmEventStatus.All
            .Select(s => new Option(s, LocalizedStatus(s)))
            .ToList();
        PriorityCombo.ItemsSource = AlarmPriority.All
            .Select(p => new Option(p, LocalizedPriority(p)))
            .ToList();

        Refresh();

        // 15 秒輪詢退為漏接推播時的保險（與主視窗未確認徽章同節奏）；刷新後維持目前選取。
        _autoTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            var selected = (BoardList.SelectedItem as BoardRow)?.EventId;
            Refresh();
            if (selected is { } id &&
                BoardList.Items.OfType<BoardRow>().FirstOrDefault(r => r.EventId == id) is { } match)
            {
                BoardList.SelectedItem = match;
            }
        }, Dispatcher);
        _autoTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoTimer.Stop();
        _feed?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>依現況語言套用標題／欄位與按鈕文字（M57）。新開視窗以新語言顯示。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("AlarmManager.Title");
        HeadingText.Text = Localizer.T("AlarmManager.Heading");
        RefreshButton.Content = Localizer.T("AlarmManager.Refresh");
        RefreshButton.ToolTip = Localizer.T("AlarmManager.RefreshTip");
        StatusFieldLabel.Text = Localizer.T("AlarmManager.FieldStatus");
        PriorityFieldLabel.Text = Localizer.T("AlarmManager.FieldPriority");
        OwnerFieldLabel.Text = Localizer.T("AlarmManager.FieldOwner");
        DueFieldLabel.Text = Localizer.T("AlarmManager.FieldDue");
        NoteFieldLabel.Text = Localizer.T("AlarmManager.FieldNote");
        ApplyButton.Content = Localizer.T("AlarmManager.Apply");
        ApplyButton.ToolTip = Localizer.T("AlarmManager.ApplyTip");

        if (BoardList.View is GridView grid && grid.Columns.Count >= 8)
        {
            grid.Columns[0].Header = Localizer.T("AlarmManager.ColTime");
            grid.Columns[1].Header = Localizer.T("AlarmManager.ColChannel");
            grid.Columns[2].Header = Localizer.T("AlarmManager.ColType");
            grid.Columns[3].Header = Localizer.T("AlarmManager.ColStatus");
            grid.Columns[4].Header = Localizer.T("AlarmManager.ColPriority");
            grid.Columns[5].Header = Localizer.T("AlarmManager.ColOwner");
            grid.Columns[6].Header = Localizer.T("AlarmManager.ColDue");
            grid.Columns[7].Header = Localizer.T("AlarmManager.ColOverdue");
        }
    }

    /// <summary>狀態代碼轉現況語言標籤；非已知代碼原樣顯示（與 AlarmEventStatus.Label 同語意但可多語）。</summary>
    private static string LocalizedStatus(string status)
        => AlarmEventStatus.IsValid(status) ? Localizer.T("AlarmStatus." + status) : status;

    /// <summary>優先序代碼轉現況語言標籤；非已知代碼原樣顯示。</summary>
    private static string LocalizedPriority(string priority)
        => AlarmPriority.IsValid(priority) ? Localizer.T("AlarmPriority." + priority) : priority;

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Refresh();
        ManagerStatusText.Text = Localizer.T("AlarmManager.Refreshed");
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        var summary = _triage.Summarize(now);
        SummaryText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("AlarmManager.Summary"),
            summary.Pending,
            summary.Acknowledged,
            summary.Actioned,
            summary.FalseAlarm,
            summary.Overdue,
            now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        Title = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("AlarmManager.TitleWithPending"), summary.Pending);

        var rows = _triage.ListBoard(now).Select(r => new BoardRow
        {
            EventId = r.EventId,
            Status = r.Status,
            Priority = r.Priority,
            StartLabel = r.StartUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ChannelLabel = _channelNames.TryGetValue(r.ChannelId, out var name) ? name : $"#{r.ChannelId}",
            TypeLabel = r.EventType,
            StatusLabel = LocalizedStatus(r.Status),
            PriorityLabel = LocalizedPriority(r.Priority),
            OwnerLabel = r.Owner ?? r.AssignedTo ?? string.Empty,
            DueLabel = r.DueUtc is { } due
                ? due.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty,
            OverdueLabel = r.IsOverdue ? Localizer.T("AlarmManager.OverdueYes") : string.Empty,
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
            ManagerStatusText.Text = Localizer.T("AlarmManager.NeedSelection");
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

        ManagerStatusText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("AlarmManager.Updated"),
            row.EventId,
            LocalizedStatus(status),
            LocalizedPriority(priority));
        Refresh();
    }
}