using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>錄影排程管理（M10）：依頻道／星期／每日時段設定自動錄影規則。</summary>
public partial class SchedulingWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly RecordingScheduleRepository _repo;
    private long _editingId;

    public SchedulingWindow(SqliteStore store)
    {
        InitializeComponent();
        _store = store;
        _channels = new ChannelRepository(store);
        _repo = new RecordingScheduleRepository(store);
        ApplyI18n();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Schedule.Title");
        HeaderTitle.Text = Localizer.T("Schedule.Title");
        CloseButton.ToolTip = Localizer.T("Schedule.CloseTip");
        ScheduleFormHeader.Text = Localizer.T("Schedule.FormHeader");
        ScheduleChannelLabel.Text = Localizer.T("Schedule.Channel");
        ScheduleWeekdayLabel.Text = Localizer.T("Schedule.Weekday");
        ScheduleStartLabel.Text = Localizer.T("Schedule.StartLabel");
        ScheduleEndLabel.Text = Localizer.T("Schedule.EndLabel");
        SunBox.Content = Localizer.T("Schedule.DaySun");
        SunBox.ToolTip = Localizer.T("Schedule.DaySunTip");
        MonBox.Content = Localizer.T("Schedule.DayMon");
        MonBox.ToolTip = Localizer.T("Schedule.DayMonTip");
        TueBox.Content = Localizer.T("Schedule.DayTue");
        TueBox.ToolTip = Localizer.T("Schedule.DayTueTip");
        WedBox.Content = Localizer.T("Schedule.DayWed");
        WedBox.ToolTip = Localizer.T("Schedule.DayWedTip");
        ThuBox.Content = Localizer.T("Schedule.DayThu");
        ThuBox.ToolTip = Localizer.T("Schedule.DayThuTip");
        FriBox.Content = Localizer.T("Schedule.DayFri");
        FriBox.ToolTip = Localizer.T("Schedule.DayFriTip");
        SatBox.Content = Localizer.T("Schedule.DaySat");
        SatBox.ToolTip = Localizer.T("Schedule.DaySatTip");
        EnabledBox.Content = Localizer.T("Schedule.Enable");
        EnabledBox.ToolTip = Localizer.T("Schedule.EnableTip");
        AddButton.Content = Localizer.T("Schedule.Add");
        AddButton.ToolTip = Localizer.T("Schedule.AddTip");
        UpdateButton.Content = Localizer.T("Schedule.Update");
        UpdateButton.ToolTip = Localizer.T("Schedule.UpdateTip");
        ClearButton.Content = Localizer.T("Schedule.Clear");
        ClearButton.ToolTip = Localizer.T("Schedule.ClearTip");
        HintText.Text = Localizer.T("Schedule.Hint");
        ScheduleListHeader.Text = Localizer.T("Schedule.ListHeader");
        ScheduleColChannel.Header = Localizer.T("Schedule.ColChannel");
        ScheduleColDays.Header = Localizer.T("Schedule.ColWeekday");
        ScheduleColTime.Header = Localizer.T("Schedule.ColTime");
        ScheduleColState.Header = Localizer.T("Schedule.ColState");
        ToggleButton.Content = Localizer.T("Schedule.Toggle");
        ToggleButton.ToolTip = Localizer.T("Schedule.ToggleTip");
        DeleteButton.Content = Localizer.T("Schedule.Delete");
        DeleteButton.ToolTip = Localizer.T("Schedule.DeleteTip");
    }

    private sealed class ScheduleRow
    {
        public long Id { get; init; }

        public string ChannelName { get; init; } = "";

        public string DaysLabel { get; init; } = "";

        public string TimeLabel { get; init; } = "";

        public bool Enabled { get; init; }

        public string StateLabel => Enabled ? Localizer.T("Schedule.Enabled") : Localizer.T("Schedule.Disabled");

        public Brush StateBrush => Enabled ? Brushes.LightGreen : Brushes.Gray;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var list = _channels.List();
        ChannelCombo.ItemsSource = list;
        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        ChannelCombo.SelectedIndex = list.Count > 0 ? 0 : -1;
        RefreshList();
    }

    private void RefreshList()
    {
        var byId = _channels.List().ToDictionary(c => c.Id);
        ScheduleList.ItemsSource = _repo.List()
            .Select(s => new ScheduleRow
            {
                Id = s.Id,
                ChannelName = byId.TryGetValue(s.ChannelId, out var c) ? c.Name : $"#{s.ChannelId}",
                DaysLabel = FormatDays(s.DaysMask),
                TimeLabel = $"{FormatMinute(s.StartMinute)} – {FormatMinute(s.EndMinute)}",
                Enabled = s.Enabled,
            })
            .ToList();
    }

    private static string FormatDays(int mask)
    {
        if (mask == 127)
        {
            return Localizer.T("Schedule.Everyday");
        }

        var days = new[]
        {
            Localizer.T("Schedule.DaySun"),
            Localizer.T("Schedule.DayMon"),
            Localizer.T("Schedule.DayTue"),
            Localizer.T("Schedule.DayWed"),
            Localizer.T("Schedule.DayThu"),
            Localizer.T("Schedule.DayFri"),
            Localizer.T("Schedule.DaySat"),
        };
        var sep = Localizer.T("Schedule.DaySep");
        var parts = new List<string>();
        for (var i = 0; i < days.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                parts.Add(days[i]);
            }
        }

        return parts.Count > 0 ? string.Join(sep, parts) : Localizer.T("Schedule.NoneDay");
    }

    private static string FormatMinute(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private int CollectMask()
    {
        var mask = 0;
        if (SunBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Sunday; }
        if (MonBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Monday; }
        if (TueBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Tuesday; }
        if (WedBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Wednesday; }
        if (ThuBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Thursday; }
        if (FriBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Friday; }
        if (SatBox.IsChecked == true) { mask |= 1 << (int)DayOfWeek.Saturday; }
        return mask == 0 ? 127 : mask;
    }

    private void ApplyMask(int mask)
    {
        SunBox.IsChecked = (mask & (1 << (int)DayOfWeek.Sunday)) != 0;
        MonBox.IsChecked = (mask & (1 << (int)DayOfWeek.Monday)) != 0;
        TueBox.IsChecked = (mask & (1 << (int)DayOfWeek.Tuesday)) != 0;
        WedBox.IsChecked = (mask & (1 << (int)DayOfWeek.Wednesday)) != 0;
        ThuBox.IsChecked = (mask & (1 << (int)DayOfWeek.Thursday)) != 0;
        FriBox.IsChecked = (mask & (1 << (int)DayOfWeek.Friday)) != 0;
        SatBox.IsChecked = (mask & (1 << (int)DayOfWeek.Saturday)) != 0;
    }

    private static bool TryParseTime(string text, out int minute)
    {
        minute = 0;
        if (!TimeOnly.TryParse(text.Trim(), out var t))
        {
            return false;
        }

        minute = t.Hour * 60 + t.Minute;
        return true;
    }

    private RecordingScheduleRecord? BuildFromForm()
    {
        if (ChannelCombo.SelectedItem is not ChannelInfo ch)
        {
            HintText.Text = Localizer.T("Schedule.PickChannel");
            return null;
        }

        if (!TryParseTime(StartBox.Text, out var start) ||
            !TryParseTime(EndBox.Text, out var end))
        {
            HintText.Text = Localizer.T("Schedule.TimeFormat");
            return null;
        }

        if (start == end)
        {
            // IsActiveAt 用閉區間：起訖相同＝只在「那一分鐘」內有效，排程器 30 秒才跑一輪，
            // 這條規則實際上錄不到東西——等於靜靜地什麼都沒發生。擋下來說清楚，
            // 全天請用 00:00–23:59，跨午夜把結束設得比開始小。
            HintText.Text = Localizer.T("Schedule.SameTime");
            return null;
        }

        return new RecordingScheduleRecord
        {
            Id = _editingId,
            ChannelId = ch.Id,
            DaysMask = CollectMask(),
            StartMinute = start,
            EndMinute = end,
            Enabled = EnabledBox.IsChecked != false,
        };
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var rec = BuildFromForm();
        if (rec is null)
        {
            return;
        }

        _editingId = 0;
        _repo.Upsert(rec with { Id = 0 });
        HintText.Text = Localizer.T("Schedule.Added");
        RefreshList();
        ClearForm();
    }

    private void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_editingId <= 0)
        {
            return;
        }

        var rec = BuildFromForm();
        if (rec is null)
        {
            return;
        }

        _repo.Upsert(rec with { Id = _editingId });
        HintText.Text = Localizer.T("Schedule.Updated");
        ClearForm();
        RefreshList();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e) => ClearForm();

    private void ClearForm()
    {
        _editingId = 0;
        AddButton.IsEnabled = true;
        UpdateButton.IsEnabled = false;
        EnabledBox.IsChecked = true;

        // 預設一整天而不是 00:00–00:00：起訖相同的規則在 IsActiveAt 的閉區間語意下
        // 只在「那一分鐘」有效，等於按了新增卻什麼都不會錄（排程器 30 秒一輪根本踩不到）。
        StartBox.Text = "00:00";
        EndBox.Text = "23:59";
        ApplyMask(127);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ToggleButton.IsEnabled = ScheduleList.SelectedItem is ScheduleRow;
        DeleteButton.IsEnabled = ScheduleList.SelectedItem is ScheduleRow;
        if (ScheduleList.SelectedItem is ScheduleRow row)
        {
            LoadToForm(row);
        }
    }

    private void LoadToForm(ScheduleRow row)
    {
        var rec = _repo.List().FirstOrDefault(s => s.Id == row.Id);
        if (rec is null)
        {
            return;
        }

        _editingId = rec.Id;
        foreach (var item in ChannelCombo.Items.OfType<ChannelInfo>())
        {
            if (item.Id == rec.ChannelId)
            {
                ChannelCombo.SelectedItem = item;
                break;
            }
        }

        ApplyMask(rec.DaysMask);
        StartBox.Text = FormatMinute(rec.StartMinute);
        EndBox.Text = FormatMinute(rec.EndMinute);
        EnabledBox.IsChecked = rec.Enabled;
        AddButton.IsEnabled = false;
        UpdateButton.IsEnabled = true;
        HintText.Text = Localizer.T("Schedule.Editing");
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleRow row)
        {
            return;
        }

        var rec = _repo.List().FirstOrDefault(s => s.Id == row.Id);
        if (rec is not null)
        {
            _repo.Upsert(rec with { Enabled = !rec.Enabled });
        }

        RefreshList();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleRow row)
        {
            return;
        }

        _repo.Delete(row.Id);
        if (_editingId == row.Id)
        {
            ClearForm();
        }

        HintText.Text = Localizer.T("Schedule.Deleted");
        RefreshList();
    }

    /// <summary>無邊框視窗沒有系統標題列：關閉鈕（與 Esc）是唯一出口。</summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}