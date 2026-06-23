using System.Linq;
using System.Windows;
using HeliVMS.Models;
using HeliVMS.Services;

namespace HeliVMS.Dialog;

public partial class EventRuleEditDialog : Window {
    public EventRule Rule { get; private set; }
    private int _selectedConditionIndex = -1;

    public EventRuleEditDialog(EventRule rule, ICameraService cameraService) {
        InitializeComponent();
        Rule = rule;
        RuleNameBox.Text = rule.Name;
        DescBox.Text = rule.Description;
        EnabledCheck.IsChecked = rule.Enabled;
        RefreshLists();
    }

    private void RefreshLists() {
        ConditionsList.ItemsSource = null;
        ConditionsList.Items.Clear();
        foreach (var c in Rule.Conditions)
            ConditionsList.Items.Add(FormatCondition(c));

        ActionsList.ItemsSource = null;
        ActionsList.Items.Clear();
        foreach (var a in Rule.Actions)
            ActionsList.Items.Add($"{a.Type}");
    }

    private static string FormatCondition(RuleCondition c) {
        var parts = new System.Collections.Generic.List<string> { string.IsNullOrEmpty(c.Type) ? "任意事件" : c.Type };
        if (c.CameraIds.Count > 0) parts.Add($"[{string.Join(",", c.CameraIds)}]");
        if (!string.IsNullOrEmpty(c.TimeStart) || !string.IsNullOrEmpty(c.TimeEnd))
            parts.Add($"{c.TimeStart ?? "00:00"}-{c.TimeEnd ?? "23:59"}");
        if (c.DaysOfWeek.Count > 0 && c.DaysOfWeek.Count < 7)
            parts.Add($"週{string.Join("", c.DaysOfWeek.Select(d => "日一二三四五六"[d]))}");
        return string.Join(" ", parts);
    }

    private void LoadTimeCondition(RuleCondition? c) {
        if (c is null) {
            TimeStartBox.Text = "08:00";
            TimeEndBox.Text = "18:00";
            DayMon.IsChecked = DayTue.IsChecked = DayWed.IsChecked = DayThu.IsChecked = DayFri.IsChecked = true;
            DaySat.IsChecked = DaySun.IsChecked = false;
            return;
        }
        TimeStartBox.Text = c.TimeStart ?? "08:00";
        TimeEndBox.Text = c.TimeEnd ?? "18:00";
        DayMon.IsChecked = c.DaysOfWeek.Contains(1);
        DayTue.IsChecked = c.DaysOfWeek.Contains(2);
        DayWed.IsChecked = c.DaysOfWeek.Contains(3);
        DayThu.IsChecked = c.DaysOfWeek.Contains(4);
        DayFri.IsChecked = c.DaysOfWeek.Contains(5);
        DaySat.IsChecked = c.DaysOfWeek.Contains(6);
        DaySun.IsChecked = c.DaysOfWeek.Contains(0);
    }

    private void ConditionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        _selectedConditionIndex = ConditionsList.SelectedIndex;
        if (_selectedConditionIndex >= 0 && _selectedConditionIndex < Rule.Conditions.Count)
            LoadTimeCondition(Rule.Conditions[_selectedConditionIndex]);
    }

    private void TimeCondition_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) {
        SaveTimeCondition();
    }

    private void DayCheck_Changed(object sender, RoutedEventArgs e) {
        SaveTimeCondition();
    }

    private void SaveTimeCondition() {
        if (_selectedConditionIndex < 0 || _selectedConditionIndex >= Rule.Conditions.Count) return;
        var c = Rule.Conditions[_selectedConditionIndex];
        c.TimeStart = string.IsNullOrWhiteSpace(TimeStartBox.Text) ? null : TimeStartBox.Text.Trim();
        c.TimeEnd = string.IsNullOrWhiteSpace(TimeEndBox.Text) ? null : TimeEndBox.Text.Trim();
        c.DaysOfWeek.Clear();
        if (DayMon.IsChecked == true) c.DaysOfWeek.Add(1);
        if (DayTue.IsChecked == true) c.DaysOfWeek.Add(2);
        if (DayWed.IsChecked == true) c.DaysOfWeek.Add(3);
        if (DayThu.IsChecked == true) c.DaysOfWeek.Add(4);
        if (DayFri.IsChecked == true) c.DaysOfWeek.Add(5);
        if (DaySat.IsChecked == true) c.DaysOfWeek.Add(6);
        if (DaySun.IsChecked == true) c.DaysOfWeek.Add(0);
        RefreshLists();
        ConditionsList.SelectedIndex = _selectedConditionIndex;
    }

    private void AddCondition_Click(object sender, RoutedEventArgs e) {
        var dlg = new InputDialog("新增條件", "事件類型\n(CameraDisconnected / CameraReconnected / MotionDetected / RecordingFailed):", "CameraDisconnected");
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            Rule.Conditions.Add(new RuleCondition { Type = dlg.Value.Trim() });
            RefreshLists();
        }
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e) {
        if (ConditionsList.SelectedIndex >= 0 && ConditionsList.SelectedIndex < Rule.Conditions.Count) {
            Rule.Conditions.RemoveAt(ConditionsList.SelectedIndex);
            _selectedConditionIndex = -1;
            RefreshLists();
        }
    }

    private void AddAction_Click(object sender, RoutedEventArgs e) {
        var dlg = new InputDialog("新增動作", "動作類型\n(LogEvent / HttpWebhook / Email / Push):", "LogEvent");
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            Rule.Actions.Add(new RuleAction { Type = dlg.Value.Trim() });
            RefreshLists();
        }
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e) {
        if (ActionsList.SelectedIndex >= 0 && ActionsList.SelectedIndex < Rule.Actions.Count) {
            Rule.Actions.RemoveAt(ActionsList.SelectedIndex);
            RefreshLists();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        Rule.Name = RuleNameBox.Text.Trim();
        Rule.Description = DescBox.Text.Trim();
        Rule.Enabled = EnabledCheck.IsChecked ?? true;
        Rule.UpdatedAt = DateTime.Now;
        DialogResult = !string.IsNullOrWhiteSpace(Rule.Name);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
