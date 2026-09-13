using System.Windows;
using System.Windows.Controls;
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
    }

    private sealed class ScheduleRow
    {
        public long Id { get; init; }

        public string ChannelName { get; init; } = "";

        public string DaysLabel { get; init; } = "";

        public string TimeLabel { get; init; } = "";

        public bool Enabled { get; init; }

        public string StateLabel => Enabled ? "啟用" : "停用";

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

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
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
            return "每天";
        }

        var days = new[] { "日", "一", "二", "三", "四", "五", "六" };
        var parts = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                parts.Add(days[i]);
            }
        }

        return parts.Count > 0 ? string.Join("", parts) : "（無）";
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
            HintText.Text = "請先選擇頻道。";
            return null;
        }

        if (!TryParseTime(StartBox.Text, out var start) ||
            !TryParseTime(EndBox.Text, out var end))
        {
            HintText.Text = "開始／結束時間格式須為 HH:mm。";
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
        HintText.Text = "已新增排程（服務將於下個檢查週期套用）。";
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
        HintText.Text = "已更新排程。";
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
        StartBox.Text = "00:00";
        EndBox.Text = "00:00";
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
        HintText.Text = "編輯中：修改後按「更新」。";
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

        HintText.Text = "已刪除排程。";
        RefreshList();
    }
}