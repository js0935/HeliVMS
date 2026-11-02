using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
using HeliVMS.App.Services;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>複合事件規則視窗（M62，§5.10）：規則編輯器——列示／新增／啟用切換／刪除，附內建樣本序列評估。</summary>
public partial class RulesWindow : Window
{
    private readonly SqliteStore _store;
    private readonly RuleRepository _rules;

    private readonly IoPortRepository _ioPorts;
    private readonly IoRuleRepository _ioRules;
    private readonly ChannelRepository _channels;

    /// <summary>是否處於簡易（表單）模式；false＝進階（直接寫 JSON）。</summary>
    private bool _simpleMode = true;

    /// <summary>IO 連動規則變更後的回呼（讓執行中的監視器重建引擎，不必重啟軟體）。</summary>
    private readonly Action? _ioRulesChanged;

    /// <summary>複合事件規則變更後的回呼（讓執行期求值器重建，不必重啟軟體）。</summary>
    private readonly Action? _eventRulesChanged;

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

    public RulesWindow(SqliteStore store, Action? ioRulesChanged = null, Action? eventRulesChanged = null)
    {
        _store = store;
        _rules = new RuleRepository(store);
        _ioPorts = new IoPortRepository(store);
        _ioRules = new IoRuleRepository(store);
        _channels = new ChannelRepository(store);
        _ioRulesChanged = ioRulesChanged;
        _eventRulesChanged = eventRulesChanged;
        InitializeComponent();
        ApplyI18n();
        ReloadIoRuleChoices();
        RefreshIoRules();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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

        // IO 連動（接點在設定中心 → IO 登記；這裡只設定觸發後做什麼）。
        IoRulesHeaderText.Text = Localizer.T("Rules.IoHeader");
        IoRulesAddToggleButton.Content = Localizer.T("Rules.IoAddToggle");
        IoRulesAddToggleButton.ToolTip = Localizer.T("Rules.IoAddToggleTip");
        IoRulesRefreshButton.Content = Localizer.T("Rules.IoRefresh");
        IoRulesRefreshButton.ToolTip = Localizer.T("Rules.IoRefreshTip");
        IoRuleInputLabel.Text = Localizer.T("Rules.IoInputLabel");
        IoRuleActionLabel.Text = Localizer.T("Rules.IoActionLabel");
        IoRuleActionAlarmItem.Content = Localizer.T("Rules.IoActionAlarm");
        IoRuleActionDoItem.Content = Localizer.T("Rules.IoActionDo");
        IoRuleEnabledBox.Content = Localizer.T("Rules.Enable");
        IoRuleEnabledBox.ToolTip = Localizer.T("Rules.IoEnableTip");
        IoRuleTargetLabel.Text = Localizer.T("Rules.IoTargetLabel");
        IoRuleRetriggerLabel.Text = Localizer.T("Rules.IoRetriggerLabel");
        IoRuleOutputSecLabel.Text = Localizer.T("Rules.IoOutputSecLabel");
        IoRuleOutputSecBox.ToolTip = Localizer.T("Rules.IoOutputSecTip");
        IoRuleResetLabel.Text = Localizer.T("Rules.IoResetLabel");
        IoRuleResetBox.ToolTip = Localizer.T("Rules.IoResetTip");
        IoRuleLinkLabel.Text = Localizer.T("Rules.IoLinkLabel");
        IoRuleFullscreenBox.Content = Localizer.T("Rules.IoFullscreen");
        IoRuleSnapshotBox.Content = Localizer.T("Rules.IoSnapshot");
        IoRuleMapBox.Content = Localizer.T("Rules.IoMap");
        IoRuleSoundBox.ToolTip = Localizer.T("Rules.IoSoundTip");
        IoRuleSoundBrowseButton.Content = Localizer.T("Talk.Browse");
        IoRuleConfirmButton.Content = Localizer.T("Rules.Confirm");
        IoRuleConfirmButton.ToolTip = Localizer.T("Rules.ConfirmTip");
        IoRuleCancelButton.Content = Localizer.T("Rules.Cancel");
        IoRuleColInput.Header = Localizer.T("Rules.IoColInput");
        IoRuleColAction.Header = Localizer.T("Rules.IoColAction");
        IoRuleColRetrigger.Header = Localizer.T("Rules.IoColRetrigger");
        IoRuleColState.Header = Localizer.T("Rules.IoColState");
        IoRuleColOps.Header = Localizer.T("Rules.IoColOps");

        // 簡易／進階模式與簡易表單。
        RulesModeLabel.Text = Localizer.T("Rules.ModeLabel");
        RulesModeSimple.Content = Localizer.T("Rules.ModeSimple");
        RulesModeAdvanced.Content = Localizer.T("Rules.ModeAdvanced");
        RulesSimpleEventLabel.Text = Localizer.T("Rules.SimpleEvent");
        RulesSimpleChannelLabel.Text = Localizer.T("Rules.SimpleChannel");
        RulesSimpleCountCheck.Content = Localizer.T("Rules.SimpleCount");
        RulesSimpleCountWithinLabel.Text = Localizer.T("Rules.SimpleCountWithin");
        RulesSimpleSecondsUnit.Text = Localizer.T("Rules.SimpleSecondsUnit");
        RulesSimpleTimeCheck.Content = Localizer.T("Rules.SimpleTime");
        RulesSimpleSeverityLabel.Text = Localizer.T("Rules.SimpleSeverity");
        RulesSimpleTagLabel.Text = Localizer.T("Rules.SimpleTag");
        RulesSimpleNotifyCheck.Content = Localizer.T("Rules.SimpleNotify");

        foreach (var severity in RuleSeverity.All)
        {
            RulesSimpleSeverityCombo.Items.Add(severity);
        }
        RulesSimpleSeverityCombo.SelectedIndex = 1;

        RulesSimpleEventCombo.Items.Clear();
        foreach (var type in EventTypeText.KnownTypes)
        {
            RulesSimpleEventCombo.Items.Add(new ComboBoxItem { Content = EventTypeText.Label(type), Tag = type });
        }
        RulesSimpleEventCombo.SelectedIndex = 0;
        ReloadSimpleChannels();
    }

    private void ReloadSimpleChannels()
    {
        RulesSimpleChannelCombo.Items.Clear();
        RulesSimpleChannelCombo.Items.Add(new ComboBoxItem
        {
            Content = Localizer.T("Rules.SimpleAnyChannel"),
            Tag = null,
        });
        foreach (var channel in _channels.List())
        {
            RulesSimpleChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"#{channel.Id} ({channel.Name})",
                Tag = channel.Id,
            });
        }
        RulesSimpleChannelCombo.SelectedIndex = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        ReloadIoRuleChoices();
        RefreshIoRules();
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

    /// <summary>切換簡易（表單）／進階（JSON）：兩種模式寫同一份規則，操作員不必學 JSON。</summary>
    private void OnRulesModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RulesSimplePanel is null || RulesAdvancedPanel is null)
        {
            return;   // InitializeComponent 建樹期間的第一次觸發。
        }

        _simpleMode = ReferenceEquals(RulesModeCombo.SelectedItem, RulesModeSimple);
        RulesSimplePanel.Visibility = _simpleMode ? Visibility.Visible : Visibility.Collapsed;
        RulesAdvancedPanel.Visibility = _simpleMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddConfirmed(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = RulesNameBox.Text.Trim();
            string? expr;
            string? actions;

            if (_simpleMode)
            {
                var eventType = (RulesSimpleEventCombo.SelectedItem as ComboBoxItem)?.Tag as string
                    ?? EventTypeText.KnownTypes[0];
                var channel = (RulesSimpleChannelCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
                var atLeast = RulesSimpleCountCheck.IsChecked == true
                    ? int.TryParse(RulesSimpleCountBox.Text.Trim(), out var n) && n > 1 ? n : 2
                    : (int?)null;
                var seconds = int.TryParse(RulesSimpleSecondsBox.Text.Trim(), out var s) && s > 0 ? s : 60;
                var timeStart = RulesSimpleTimeCheck.IsChecked == true && int.TryParse(RulesSimpleStartBox.Text.Trim(), out var t0) ? t0 : (int?)null;
                var timeEnd = RulesSimpleTimeCheck.IsChecked == true && int.TryParse(RulesSimpleEndBox.Text.Trim(), out var t1) ? t1 : (int?)null;

                expr = RuleExpressionBuilder.Build(eventType, channel, atLeast, seconds, timeStart, timeEnd);
                actions = RuleExpressionBuilder.BuildActions(
                    RulesSimpleSeverityCombo.SelectedItem as string,
                    RulesSimpleTagBox.Text,
                    RulesSimpleNotifyCheck.IsChecked == true);
            }
            else
            {
                expr = RulesExprBox.Text.Trim();
                actions = RulesActionsBox.Text.Trim();
            }

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
            var id = _rules.Add(name, expr!, actions!, enabled);
            RulesNameBox.Clear();
            RulesExprBox.Clear();
            RulesActionsBox.Clear();
            RulesEnabledBox.IsChecked = true;
            RulesEditPanel.Visibility = Visibility.Collapsed;
            Refresh();
            RulesStatusText.Text = string.Format(Localizer.T("Rules.Added"), id);
            _eventRulesChanged?.Invoke();
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

    _eventRulesChanged?.Invoke();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleRowViewModel row })
        {
            _rules.Delete(row.Rule.Id);
            Refresh();
        }

    _eventRulesChanged?.Invoke();
    }

    // ── IO 連動（io_rules）：接點在設定中心 → IO 登記，這裡只設定觸發後做什麼。
    // 對照 Home Assistant 的分工：裝置／實體（這裡的「接點」）與自動化（這裡的「連動」）分離，
    // 才不會出現「接點設定頁兼管規則」的雙頭馬車。
    private sealed class IoRuleRowViewModel
    {
        public required IoRuleRecord Rule { get; init; }

        public required string InputText { get; init; }

        public required string ActionText { get; init; }

        public string RetriggerText => Rule.RetriggerSec.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string EnabledText => Rule.Enabled ? Localizer.T("Rules.Enabled") : Localizer.T("Rules.Disabled");

        public string EnabledColor => Rule.Enabled ? "#8AE08A" : "#E0B0B0";

        public string ToggleText => Rule.Enabled ? Localizer.T("Rules.Disabled") : Localizer.T("Rules.Enabled");

        public string DeleteText => Localizer.T("Rules.Delete");
    }

    private void ReloadIoRuleChoices()
    {
        var ports = _ioPorts.List();

        // 只列登記過的接點：沒登記的端子在這裡選不到，規則才不會指向不存在的硬體。
        IoRuleInputCombo.Items.Clear();
        foreach (var port in ports.Where(p => p.Kind == IoPortKind.Di))
        {
            IoRuleInputCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribePort(port),
                Tag = port.Id,
            });
        }

        IoRuleOutputCombo.Items.Clear();
        foreach (var port in ports.Where(p => p.Kind == IoPortKind.Do))
        {
            IoRuleOutputCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribePort(port),
                Tag = port.Id,
            });
        }

        // 連動攝影機：觸發時把該台帶到監看牆上（或展開單畫面）。
        IoRuleLinkCombo.Items.Clear();
        IoRuleLinkCombo.Items.Add(new ComboBoxItem
        {
            Content = Localizer.T("Rules.IoLinkNone"),
            Tag = null,
        });
        foreach (var channel in _channels.List())
        {
            IoRuleLinkCombo.Items.Add(new ComboBoxItem
            {
                Content = $"#{channel.Id} ({channel.Name})",
                Tag = channel.Id,
            });
        }
        IoRuleLinkCombo.SelectedIndex = 0;
    }

    private static string DescribePort(IoPortRecord port) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}#{1} {2}",
            port.Kind == IoPortKind.Di ? "DI" : "DO", port.Number, port.Name);

    private void RefreshIoRules()
    {
        var portById = _ioPorts.List().ToDictionary(p => p.Id);
        var rows = _ioRules.List().Select(r =>
        {
            var input = portById.TryGetValue(r.InputPortId, out var p) ? DescribePort(p) : $"#{r.InputPortId}";
            var action = r.ActionKind == IoActionKind.Alarm
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Rules.IoActionAlarm"), EventTypeText.Label(r.EventType ?? string.Empty))
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Rules.IoActionDo"),
                    portById.TryGetValue(r.OutputPortId ?? 0, out var o) ? DescribePort(o) : $"#{r.OutputPortId}");
            return new IoRuleRowViewModel
            {
                Rule = r,
                InputText = input,
                ActionText = action,
            };
        }).ToList();
        IoRuleList.ItemsSource = rows;
    }

    private void OnIoRuleAddToggleClicked(object sender, RoutedEventArgs e)
    {
        IoRulesEditPanel.Visibility = IoRulesEditPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnIoRuleRefreshClicked(object sender, RoutedEventArgs e)
    {
        ReloadIoRuleChoices();
        RefreshIoRules();
    }

    private void OnIoRuleActionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IoRuleOutputCombo is null || IoRuleEventTypeBox is null)
        {
            return;
        }

        var isDo = ReferenceEquals(IoRuleActionCombo.SelectedItem, IoRuleActionDoItem);
        IoRuleOutputCombo.Visibility = isDo ? Visibility.Visible : Visibility.Collapsed;
        IoRuleEventTypeBox.Visibility = isDo ? Visibility.Collapsed : Visibility.Visible;
    }

    private static long? SelectedPortId(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as long?;

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int ParseSeconds(string text) =>
        int.TryParse(text.Trim(), out var value) && value > 0 ? value : 0;

    /// <summary>挑選對講／警報音檔（觸發時主機播放）。</summary>
    private void OnIoRuleSoundBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Talk.PickTitle"),
            Filter = Localizer.T("Talk.FileFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            IoRuleSoundBox.Text = dialog.FileName;
        }
    }

    private void OnIoRuleConfirmed(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = SelectedPortId(IoRuleInputCombo);
            if (input is null)
            {
                RulesStatusText.Text = Localizer.T("Rules.IoNeedInput");
                return;
            }

            var isDo = ReferenceEquals(IoRuleActionCombo.SelectedItem, IoRuleActionDoItem);
            var output = isDo ? SelectedPortId(IoRuleOutputCombo) : null;
            if (isDo && output is null)
            {
                RulesStatusText.Text = Localizer.T("Rules.IoNeedTarget");
                return;
            }

            var eventType = isDo ? null : (IoRuleEventTypeBox.Text ?? string.Empty).Trim();
            if (!isDo && string.IsNullOrWhiteSpace(eventType))
            {
                RulesStatusText.Text = Localizer.T("Rules.IoNeedEventType");
                return;
            }

            if (!int.TryParse(IoRuleRetriggerBox.Text.Trim(), out var retrigger) || retrigger < 0)
            {
                RulesStatusText.Text = Localizer.T("Rules.IoNeedTarget");
                return;
            }

            var enabled = IoRuleEnabledBox.IsChecked == true;
            var linkChannel = (IoRuleLinkCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
            var id = _ioRules.Add(
                input.Value,
                isDo ? IoActionKind.ToggleDo : IoActionKind.Alarm,
                output,
                eventType,
                retrigger,
                enabled,
                linkChannel,
                IoRuleFullscreenBox.IsChecked == true,
                IoRuleSnapshotBox.IsChecked == true,
                NullIfEmpty(IoRuleSoundBox.Text),
                IoRuleMapBox.IsChecked == true,
                ParseSeconds(IoRuleResetBox.Text),
                ParseSeconds(IoRuleOutputSecBox.Text));
            IoRulesEditPanel.Visibility = Visibility.Collapsed;
            RefreshIoRules();
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Rules.IoAdded"), id);
            _ioRulesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Rules.IoFailed"), ex.Message);
        }
    }

    private void OnIoRuleToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: IoRuleRowViewModel row })
        {
            return;
        }

        _ioRules.SetEnabled(row.Rule.Id, !row.Rule.Enabled);
        RefreshIoRules();
        _ioRulesChanged?.Invoke();
    }

    private void OnIoRuleDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: IoRuleRowViewModel row })
        {
            return;
        }

        _ioRules.Delete(row.Rule.Id);
        RefreshIoRules();
        _ioRulesChanged?.Invoke();
    }

    private static string Shorten(string json, int max)
    {
        var flat = json.Replace("\n", " ").Replace("\r", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}