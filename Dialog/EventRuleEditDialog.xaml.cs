using System.Windows;
using HeliVMS.Models;
using HeliVMS.Services;

namespace HeliVMS.Dialog;

public partial class EventRuleEditDialog : Window {
    public EventRule Rule { get; private set; }

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
            ConditionsList.Items.Add($"{c.Type}{(c.CameraIds.Count > 0 ? " [" + string.Join(",", c.CameraIds) + "]" : " [全部攝影機]")}");

        ActionsList.ItemsSource = null;
        ActionsList.Items.Clear();
        foreach (var a in Rule.Actions)
            ActionsList.Items.Add($"{a.Type}");
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
            RefreshLists();
        }
    }

    private void AddAction_Click(object sender, RoutedEventArgs e) {
        var dlg = new InputDialog("新增動作", "動作類型\n(LogEvent / HttpWebhook):", "LogEvent");
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
