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

        public string EnabledText => Rule.Enabled ? "啟用" : "停用";

        public string EnabledColor => Rule.Enabled ? "#8AE08A" : "#E0B0B0";

        public string ExpressionSummary => Shorten(Rule.ExpressionJson, 60);

        public string ActionsSummary => Shorten(Rule.ActionsJson, 40);

        public string ToggleText => Rule.Enabled ? "停用" : "啟用";
    }

    public RulesWindow(SqliteStore store)
    {
        _store = store;
        _rules = new RuleRepository(store);
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        var rows = _rules.List().Select(r => new RuleRowViewModel { Rule = r }).ToList();
        RulesList.ItemsSource = rows;
        RulesStatusText.Text = $"共 {rows.Count} 條規則（啟用 {rows.Count(x => x.Rule.Enabled)}）。";
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

        RulesEvalText.Text = lines.Count == 0 ? "（無命中）" : string.Join("\n", lines);
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
                RulesStatusText.Text = "名稱與條件 JSON 不可為空。";
                return;
            }

            if (RuleExpressionNode.Parse(expr) is null)
            {
                RulesStatusText.Text = "條件 JSON 無法解析。";
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
            RulesStatusText.Text = $"已新增規則 #{id}。";
        }
        catch (Exception ex)
        {
            RulesStatusText.Text = $"新增失敗：{ex.Message}";
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