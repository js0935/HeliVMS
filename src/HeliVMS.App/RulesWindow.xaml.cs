using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>複合事件規則視窗（M62，§5.10）：規則編輯器——列示／新增／啟用切換／刪除，附內建樣本序列評估。</summary>
public partial class RulesWindow : Window
{
    private readonly SqliteStore _store;
    private readonly RuleRepository _rules;

    private sealed class RuleRowViewModel
    {
        public required AiRuleRecord Rule { get; init; }

        public string Name => Rule.Name;

        public string EnabledText => Rule.Enabled ? Localizer.T("Rules.Enabled") : Localizer.T("Rules.Disabled");

        public string EnabledColor => Rule.Enabled ? "#8AE08A" : "#E0B0B0";

        public string ExpressionSummary => Shorten(Rule.ExpressionJson, 60);

        public string ActionsSummary => Shorten(Rule.ActionsJson, 40);

        public string ToggleText => Rule.Enabled ? Localizer.T("Rules.Disabled") : Localizer.T("Rules.Enabled");

        public string DeleteText => Localizer.T("Rules.Delete");
    }

    public RulesWindow(SqliteStore store)
    {
        _store = store;
        _rules = new RuleRepository(store);
        InitializeComponent();
        ApplyI18n();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Rules.Title");
        RulesDescText.Text = Localizer.T("Rules.Desc");
        RulesAddToggleButton.Content = Localizer.T("Rules.AddToggle");
        RulesAddToggleButton.ToolTip = Localizer.T("Rules.AddToggleTip");
        RulesRefreshButton.Content = Localizer.T("Rules.Refresh");
        RulesRefreshButton.ToolTip = Localizer.T("Rules.RefreshTip");
        RulesNameLabel.Text = Localizer.T("Rules.Name");
        RulesEnabledBox.Content = Localizer.T("Rules.Enable");
        RulesEnabledBox.ToolTip = Localizer.T("Rules.EnableTip");
        RulesExprLabel.Text = Localizer.T("Rules.ExprLabel");
        RulesActionsLabel.Text = Localizer.T("Rules.ActionsLabel");
        RulesConfirmButton.Content = Localizer.T("Rules.Confirm");
        RulesConfirmButton.ToolTip = Localizer.T("Rules.ConfirmTip");
        RulesCancelButton.Content = Localizer.T("Rules.Cancel");
        RulesCancelButton.ToolTip = Localizer.T("Rules.CancelTip");
        RulesColName.Header = Localizer.T("Rules.ColName");
        RulesColState.Header = Localizer.T("Rules.ColState");
        RulesColExpr.Header = Localizer.T("Rules.ColExpr");
        RulesColActions.Header = Localizer.T("Rules.ColActions");
        RulesEvalHeaderText.Text = Localizer.T("Rules.EvalHeader");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        var rows = _rules.List().Select(r => new RuleRowViewModel { Rule = r }).ToList();
        RulesList.ItemsSource = rows;
        RulesStatusText.Text = string.Format(Localizer.T("Rules.StatusCount"), rows.Count, rows.Count(x => x.Rule.Enabled));
        Eval();
    }

    private void Eval()
    {
        var eval = new RuleEvaluator(_rules.ListEnabled());
        var start = DateTime.UtcNow;
        var lines = new List<string>();
        var events = new (DateTime At, string Type)[]
        {
            (start.AddSeconds(0), "motion"),
            (start.AddSeconds(5), "ai_intrusion"),
            (start.AddSeconds(10), "offline"),
            (start.AddSeconds(20), "motion"),
            (start.AddSeconds(25), "motion"),
        };

        foreach (var (at, type) in events)
        {
            var evt = new AlarmEventRecord
            {
                StartUtc = at,
                EventType = type,
                ChannelId = 1,
            };
            foreach (var hit in eval.Feed(evt))
            {
                lines.Add($"RULE_MATCH:{hit.Name} @ t+{evt.StartUtc.Subtract(start).TotalSeconds:F0}s" +
                    (hit.Actions.Severity is null ? "" : $" (severity={hit.Actions.Severity})") +
                    (hit.Actions.Tag is null ? "" : $",tag={hit.Actions.Tag}"));
            }
        }

        RulesEvalText.Text = lines.Count == 0 ? Localizer.T("Rules.EvalNone") : string.Join("\n", lines);
    }

    private void OnAddToggleClicked(object sender, RoutedEventArgs e)
        => RulesEditPanel.Visibility = RulesEditPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnAddConfirmed(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = RulesNameBox.Text.Trim();
            var expr = RulesExprBox.Text.Trim();
            var actions = RulesActionsBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(expr))
            {
                RulesStatusText.Text = Localizer.T("Rules.NeedNameExpr");
                return;
            }

            if (RuleExpressionNode.Parse(expr) is null)
            {
                RulesStatusText.Text = Localizer.T("Rules.BadExpr");
                return;
            }

            var enabled = RulesEnabledBox.IsChecked == true;
            var id = _rules.Add(name, expr, actions, enabled);
            RulesNameBox.Clear();
            RulesExprBox.Clear();
            RulesActionsBox.Clear();
            RulesEnabledBox.IsChecked = true;
            RulesEditPanel.Visibility = Visibility.Collapsed;
            Refresh();
            RulesStatusText.Text = string.Format(Localizer.T("Rules.Added"), id);
        }
        catch (Exception ex)
        {
            RulesStatusText.Text = string.Format(Localizer.T("Rules.AddFailed"), ex.Message);
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleRowViewModel row })
        {
            _rules.SetEnabled(row.Rule.Id, !row.Rule.Enabled);
            Refresh();
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleRowViewModel row })
        {
            _rules.Delete(row.Rule.Id);
            Refresh();
        }
    }

    private static string Shorten(string json, int max)
    {
        var flat = json.Replace("\n", " ").Replace("\r", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}