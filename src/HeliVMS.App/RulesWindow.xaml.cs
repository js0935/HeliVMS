using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
using HeliVMS.App.Services;
using HeliVMS.Shared;
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
    private readonly ScheduledTaskRepository _taskRepo;

    /// <summary>是否處於簡易（表單）模式；false＝進階（直接寫 JSON）。</summary>
    private bool _simpleMode = true;

    /// <summary>IO 連動規則變更後的回呼（讓執行中的監視器重建引擎，不必重啟軟體）。</summary>
    private readonly Action? _ioRulesChanged;

    /// <summary>複合事件規則變更後的回呼（讓執行期求值器重建，不必重啟軟體）。</summary>
    private readonly Action? _eventRulesChanged;

    /// <summary>手動執行一顆排程任務（回傳結果字串供狀態列顯示）；宿主由主視窗提供。</summary>
    private readonly Func<long, Task<string?>>? _runTaskNow;

    /// <summary>
    /// 任務清單自動刷新（30 秒）：宿主在背景輪詢執行任務後，結果寫在資料庫的
    /// last_result——視窗開著不重讀的話，操作員看到的永遠是開窗那一刻的舊結果，
    /// 以為任務沒跑。只刷新清單，不動編輯面板裡正在輸入的欄位。
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _taskRefreshTimer;

    /// <summary>正在編輯的複合事件規則 Id；null＝新增模式。</summary>
    private int? _editingRuleId;

    /// <summary>正在編輯的 IO 連動規則 Id；null＝新增模式。</summary>
    private long? _editingIoRuleId;

    /// <summary>正在編輯的排程任務 Id；null＝新增模式。</summary>
    private long? _editingTaskId;

    private sealed class RuleRowViewModel
    {
        public required AiRuleRecord Rule { get; init; }

        public string Name => Rule.Name;

        public string EnabledText => Rule.Enabled ? Localizer.T("Rules.Enabled") : Localizer.T("Rules.Disabled");

        public string EnabledColor => Rule.Enabled ? "#8AE08A" : "#E0B0B0";

        public string ExpressionSummary => Shorten(Rule.ExpressionJson, 60);

        public string ActionsSummary => Shorten(Rule.ActionsJson, 40);

        public string ToggleText => Rule.Enabled ? Localizer.T("Rules.Disabled") : Localizer.T("Rules.Enabled");

        public string EditText => Localizer.T("Rules.Edit");

        public string DeleteText => Localizer.T("Rules.Delete");
    }

    public RulesWindow(SqliteStore store, Action? ioRulesChanged = null, Action? eventRulesChanged = null, Func<long, Task<string?>>? runTaskNow = null)
    {
        _store = store;
        _rules = new RuleRepository(store);
        _ioPorts = new IoPortRepository(store);
        _ioRules = new IoRuleRepository(store);
        _channels = new ChannelRepository(store);
        _taskRepo = new ScheduledTaskRepository(store);
        _ioRulesChanged = ioRulesChanged;
        _eventRulesChanged = eventRulesChanged;
        _runTaskNow = runTaskNow;
        InitializeComponent();

        _taskRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(30), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RefreshTasks(), Dispatcher);
        _taskRefreshTimer.Start();
        ApplyI18n();
        ReloadIoRuleChoices();
        RefreshIoRules();
        ReloadTaskChannels();
        RefreshTasks();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _taskRefreshTimer.Stop();
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

        // Scheduled maintenance tasks (reboot / snapshot / reconnect).
        TasksHeaderText.Text = Localizer.T("Tasks.Header");
        TasksAddToggleButton.Content = Localizer.T("Tasks.AddToggle");
        TasksAddToggleButton.ToolTip = Localizer.T("Tasks.AddToggleTip");
        TasksRefreshButton.Content = Localizer.T("Tasks.Refresh");
        TasksRefreshButton.ToolTip = Localizer.T("Tasks.RefreshTip");
        TaskNameLabel.Text = Localizer.T("Tasks.NameLabel");
        TaskKindLabel.Text = Localizer.T("Tasks.KindLabel");
        TaskKindRebootItem.Content = Localizer.T("Tasks.KindReboot");
        TaskKindSnapshotItem.Content = Localizer.T("Tasks.KindSnapshot");
        TaskKindReconnectItem.Content = Localizer.T("Tasks.KindReconnect");
        TaskEnabledBox.Content = Localizer.T("Tasks.Enable");
        TaskEnabledBox.ToolTip = Localizer.T("Tasks.EnableTip");
        TaskSchedLabel.Text = Localizer.T("Tasks.SchedLabel");
        TaskSchedDailyItem.Content = Localizer.T("Tasks.SchedDaily");
        TaskSchedWeeklyItem.Content = Localizer.T("Tasks.SchedWeekly");
        TaskSchedIntervalItem.Content = Localizer.T("Tasks.SchedInterval");
        TaskTimeLabel.Text = Localizer.T("Tasks.TimeLabel");
        TaskTimeBox.ToolTip = Localizer.T("Tasks.TimeTip");
        TaskIntervalLabel.Text = Localizer.T("Tasks.IntervalLabel");
        TaskIntervalUnit.Text = Localizer.T("Tasks.IntervalUnit");
        TaskChannelLabel.Text = Localizer.T("Tasks.ChannelLabel");
        TaskWeekdayLabel.Text = Localizer.T("Tasks.WeekdayLabel");
        TaskDow0.Content = Localizer.T("Tasks.Dow0");
        TaskDow1.Content = Localizer.T("Tasks.Dow1");
        TaskDow2.Content = Localizer.T("Tasks.Dow2");
        TaskDow3.Content = Localizer.T("Tasks.Dow3");
        TaskDow4.Content = Localizer.T("Tasks.Dow4");
        TaskDow5.Content = Localizer.T("Tasks.Dow5");
        TaskDow6.Content = Localizer.T("Tasks.Dow6");
        TaskConfirmButton.Content = Localizer.T("Tasks.Confirm");
        TaskConfirmButton.ToolTip = Localizer.T("Tasks.ConfirmTip");
        TaskCancelButton.Content = Localizer.T("Tasks.Cancel");
        TaskColName.Header = Localizer.T("Tasks.ColName");
        TaskColKind.Header = Localizer.T("Tasks.ColKind");
        TaskColSchedule.Header = Localizer.T("Tasks.ColSchedule");
        TaskColLastRun.Header = Localizer.T("Tasks.ColLastRun");
        TaskColState.Header = Localizer.T("Tasks.ColState");
        TaskColOps.Header = Localizer.T("Tasks.ColOps");

        TaskKindCombo.SelectedIndex = 0;
        TaskSchedCombo.SelectedIndex = 0;
        TaskDow0.IsChecked = true;
        TaskDow1.IsChecked = true;
        TaskDow2.IsChecked = true;
        TaskDow3.IsChecked = true;
        TaskDow4.IsChecked = true;
        TaskDow5.IsChecked = true;
        TaskDow6.IsChecked = true;
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

    /// <summary>依 Tag 選下拉項目（物件類型／頻道／接點）；找不到退回第一個。</summary>
    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, object? tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && Equals(item.Tag, tag))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// 點「編輯」：把這條規則載回表單。
    /// 條件 JSON 解得回簡易形狀就回表單；解不回（any／not／巢狀等進阶形狀）
    /// 就進階 JSON 原樣顯示——硬解會把規則改壞，寧可讓操作員看到原文。
    /// </summary>
    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleRowViewModel row })
        {
            return;
        }

        var rule = row.Rule;
        _editingRuleId = rule.Id;
        RulesNameBox.Text = rule.Name;
        RulesEnabledBox.IsChecked = rule.Enabled;
        RulesSimpleCountCheck.IsChecked = false;
        RulesSimpleTimeCheck.IsChecked = false;

        var form = RuleExpressionBuilder.TryParseExpression(rule.ExpressionJson);
        if (form is { } values)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            _simpleMode = true;
            RulesModeCombo.SelectedItem = RulesModeSimple;
            SelectComboByTag(RulesSimpleEventCombo, values.EventType);
            SelectComboByTag(RulesSimpleChannelCombo, values.ChannelId);
            if (values.CountAtLeast is int atLeast)
            {
                RulesSimpleCountCheck.IsChecked = true;
                RulesSimpleCountBox.Text = atLeast.ToString(culture);
                RulesSimpleSecondsBox.Text = (values.CountSeconds ?? 60).ToString(culture);
            }

            if (values.TimeStartHhmm is int start && values.TimeEndHhmm is int end)
            {
                RulesSimpleTimeCheck.IsChecked = true;
                RulesSimpleStartBox.Text = start.ToString(culture);
                RulesSimpleEndBox.Text = end.ToString(culture);
            }

            var (severity, tag, notify) = RuleExpressionBuilder.TryParseActions(rule.ActionsJson);
            RulesSimpleSeverityCombo.SelectedItem = severity ?? RuleSeverity.Warning;
            RulesSimpleTagBox.Text = tag ?? string.Empty;
            RulesSimpleNotifyCheck.IsChecked = notify;
        }
        else
        {
            _simpleMode = false;
            RulesModeCombo.SelectedItem = RulesModeAdvanced;
            RulesExprBox.Text = rule.ExpressionJson;
            RulesActionsBox.Text = rule.ActionsJson;
        }

        RulesEditPanel.Visibility = Visibility.Visible;
    }

    private void OnAddToggleClicked(object sender, RoutedEventArgs e)
    {
        if (RulesEditPanel.Visibility != Visibility.Visible)
        {
            _editingRuleId = null;   // 開「新增」面板＝離開編輯模式，別把上一條的 Id 帶進來。
        }

        RulesEditPanel.Visibility = RulesEditPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

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
            var wasEdit = _editingRuleId is not null;
            int ruleId;
            if (_editingRuleId is int editingRuleId)
            {
                // 編輯走 Update：刪掉重加會換 Id，稽核軌跡與清單就斷線了。
                _rules.Update(editingRuleId, name, expr!, actions!, enabled);
                ruleId = editingRuleId;
                _editingRuleId = null;
            }
            else
            {
                ruleId = _rules.Add(name, expr!, actions!, enabled);
            }

            RulesNameBox.Clear();
            RulesExprBox.Clear();
            RulesActionsBox.Clear();
            RulesEnabledBox.IsChecked = true;
            RulesEditPanel.Visibility = Visibility.Collapsed;
            Refresh();
            RulesStatusText.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Localizer.T(wasEdit ? "Rules.Updated" : "Rules.Added"),
                ruleId);
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

        public string EditText => Localizer.T("Rules.Edit");

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
        if (IoRulesEditPanel.Visibility != Visibility.Visible)
        {
            _editingIoRuleId = null;
        }

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

    /// <summary>點「編輯」：把這條 IO 連動的值載回表單並切到編輯模式。</summary>
    private void OnIoRuleEditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: IoRuleRowViewModel row })
        {
            return;
        }

        var rule = row.Rule;
        _editingIoRuleId = rule.Id;

        SelectComboByTag(IoRuleInputCombo, rule.InputPortId);
        var isDo = rule.ActionKind == IoActionKind.ToggleDo;
        IoRuleActionCombo.SelectedItem = isDo ? IoRuleActionDoItem : IoRuleActionAlarmItem;
        if (isDo)
        {
            SelectComboByTag(IoRuleOutputCombo, rule.OutputPortId);
        }
        else
        {
            IoRuleEventTypeBox.Text = rule.EventType ?? string.Empty;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        IoRuleRetriggerBox.Text = rule.RetriggerSec.ToString(culture);
        IoRuleEnabledBox.IsChecked = rule.Enabled;
        SelectComboByTag(IoRuleLinkCombo, rule.LinkChannelId);
        IoRuleFullscreenBox.IsChecked = rule.LinkFullscreen;
        IoRuleSnapshotBox.IsChecked = rule.LinkSnapshot;
        IoRuleMapBox.IsChecked = rule.LinkMap;
        IoRuleSoundBox.Text = rule.LinkSoundPath ?? string.Empty;
        IoRuleOutputSecBox.Text = rule.OutputSeconds.ToString(culture);
        IoRuleResetBox.Text = rule.ResetSeconds.ToString(culture);

        IoRulesEditPanel.Visibility = Visibility.Visible;
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
            var wasEdit = _editingIoRuleId is not null;
            long ruleId;
            if (_editingIoRuleId is long editingIoRuleId)
            {
                _ioRules.Update(
                    editingIoRuleId,
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
                ruleId = editingIoRuleId;
                _editingIoRuleId = null;
            }
            else
            {
                ruleId = _ioRules.Add(
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
            }
            IoRulesEditPanel.Visibility = Visibility.Collapsed;
            RefreshIoRules();
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T(wasEdit ? "Rules.IoUpdated" : "Rules.IoAdded"), ruleId);
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

    // ── 排程任務（scheduled_tasks）：重開機／定時截圖／重連攝影機。
    // 到點判定在 Shared.MaintenanceTaskPlanner（純函式）；執行在 App.Services.MaintenanceTaskHost。
    // 這裡只負責編輯與存取。建立當下就寫入 last_run（宿主以「上次執行」為基準），
    // 所以新建任務的第一次執行一定落在下一個到點時刻，
    // 不會有「15:00 建一顆每天 03:00 重開機，存好就立刻重開」這種事故。

    private sealed class TaskRowViewModel
    {
        public required ScheduledTaskRecord Task { get; init; }

        public string NameText => Task.Name;

        public string KindText => Task.Kind switch
        {
            MaintenanceTaskKind.Reboot => Localizer.T("Tasks.KindReboot"),
            MaintenanceTaskKind.Snapshot => Localizer.T("Tasks.KindSnapshot"),
            _ => Localizer.T("Tasks.KindReconnect"),
        };

        public string ScheduleText => DescribeSchedule(Task);

        public string LastRunText => Task.LastRunUtc is { } run
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Tasks.LastRunText"), run.ToLocalTime(), Task.LastResult ?? string.Empty)
            : Localizer.T("Tasks.LastNever");

        public string EnabledText => Task.Enabled ? Localizer.T("Tasks.Enabled") : Localizer.T("Tasks.Disabled");

        public string EnabledColor => Task.Enabled ? "#8AE08A" : "#E0B0B0";

        public string ToggleText => Task.Enabled ? Localizer.T("Tasks.Disable") : Localizer.T("Tasks.Enable");

        public string RunText => Localizer.T("Tasks.Run");

        public string EditText => Localizer.T("Tasks.Edit");

        public string DeleteText => Localizer.T("Tasks.Delete");
    }

    private static string DescribeSchedule(ScheduledTaskRecord task)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return task.ScheduleKind switch
        {
            MaintenanceScheduleKind.Daily => string.Format(culture, Localizer.T("Tasks.SchedDailyText"), task.TimeOfDay / 100, task.TimeOfDay % 100),
            MaintenanceScheduleKind.Weekly => string.Format(culture, Localizer.T("Tasks.SchedWeeklyText"), DescribeWeekdays(task.WeekdaysMask), task.TimeOfDay / 100, task.TimeOfDay % 100),
            _ => string.Format(culture, Localizer.T("Tasks.SchedIntervalText"), task.IntervalMinutes),
        };
    }

    private static string DescribeWeekdays(int mask)
    {
        var names = new[] { "Dow0", "Dow1", "Dow2", "Dow3", "Dow4", "Dow5", "Dow6" };
        var selected = new System.Collections.Generic.List<string>();
        for (var day = 0; day < 7; day++)
        {
            if ((mask & (1 << day)) != 0)
            {
                selected.Add(Localizer.T("Tasks." + names[day]));
            }
        }

        return string.Join("/", selected);
    }

    private void ReloadTaskChannels()
    {
        TaskChannelCombo.Items.Clear();
        TaskChannelCombo.Items.Add(new ComboBoxItem
        {
            Content = Localizer.T("Tasks.AllChannels"),
            Tag = null,
        });
        foreach (var channel in _channels.List())
        {
            TaskChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"#{channel.Id} ({channel.Name})",
                Tag = channel.Id,
            });
        }

        TaskChannelCombo.SelectedIndex = 0;
    }

    private void RefreshTasks()
    {
        TaskList.ItemsSource = _taskRepo.List().Select(t => new TaskRowViewModel { Task = t }).ToList();
    }

    private void OnTaskAddToggleClicked(object sender, RoutedEventArgs e)
    {
        if (TasksEditPanel.Visibility != Visibility.Visible)
        {
            _editingTaskId = null;
        }

        TasksEditPanel.Visibility = TasksEditPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnTaskRefreshClicked(object sender, RoutedEventArgs e)
    {
        ReloadTaskChannels();
        RefreshTasks();
    }

    private void OnTaskSchedChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TaskTimeBox is null || TaskIntervalBox is null || TaskWeekdaysPanel is null || TaskTimeLabel is null)
        {
            return;
        }

        var isInterval = ReferenceEquals(TaskSchedCombo.SelectedItem, TaskSchedIntervalItem);
        var isWeekly = ReferenceEquals(TaskSchedCombo.SelectedItem, TaskSchedWeeklyItem);
        TaskTimeBox.Visibility = isInterval ? Visibility.Collapsed : Visibility.Visible;
        TaskTimeLabel.Visibility = TaskTimeBox.Visibility;
        TaskTimeLabel.Margin = TaskTimeBox.Visibility == Visibility.Visible
            ? new Thickness(16, 0, 0, 0)
            : new Thickness(0);
        TaskIntervalBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        TaskIntervalLabel.Visibility = TaskIntervalBox.Visibility;
        TaskIntervalUnit.Visibility = TaskIntervalBox.Visibility;
        TaskWeekdaysPanel.Visibility = isWeekly ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int? ParseHhmm(string? text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length is < 3 or > 4 || !int.TryParse(text, out var parsed))
        {
            return null;
        }

        return parsed / 100 > 23 || parsed % 100 > 59 ? null : parsed;
    }

    /// <summary>點「編輯」：把這顆排程任務的值載回表單並切到編輯模式。</summary>
    private void OnTaskEditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: TaskRowViewModel row })
        {
            return;
        }

        var task = row.Task;
        _editingTaskId = task.Id;
        TaskNameBox.Text = task.Name;
        TaskKindCombo.SelectedItem = task.Kind switch
        {
            MaintenanceTaskKind.Reboot => TaskKindRebootItem,
            MaintenanceTaskKind.Snapshot => TaskKindSnapshotItem,
            _ => TaskKindReconnectItem,
        };
        TaskSchedCombo.SelectedItem = task.ScheduleKind switch
        {
            MaintenanceScheduleKind.Daily => TaskSchedDailyItem,
            MaintenanceScheduleKind.Weekly => TaskSchedWeeklyItem,
            _ => TaskSchedIntervalItem,
        };

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        TaskTimeBox.Text = task.TimeOfDay.ToString("0000", culture);
        TaskIntervalBox.Text = task.IntervalMinutes.ToString(culture);
        var boxes = new[] { TaskDow0, TaskDow1, TaskDow2, TaskDow3, TaskDow4, TaskDow5, TaskDow6 };
        for (var day = 0; day < boxes.Length; day++)
        {
            boxes[day].IsChecked = MaintenanceWeekdays.Has(task.WeekdaysMask, (DayOfWeek)day);
        }

        SelectComboByTag(TaskChannelCombo, task.ChannelId);
        TaskEnabledBox.IsChecked = task.Enabled;

        TasksEditPanel.Visibility = Visibility.Visible;
    }

    private void OnTaskConfirmed(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = (TaskNameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                RulesStatusText.Text = Localizer.T("Tasks.NeedName");
                return;
            }

            var kind = ReferenceEquals(TaskKindCombo.SelectedItem, TaskKindSnapshotItem) ? MaintenanceTaskKind.Snapshot
                : ReferenceEquals(TaskKindCombo.SelectedItem, TaskKindReconnectItem) ? MaintenanceTaskKind.ReconnectChannels
                : MaintenanceTaskKind.Reboot;

            var isInterval = ReferenceEquals(TaskSchedCombo.SelectedItem, TaskSchedIntervalItem);
            var isWeekly = ReferenceEquals(TaskSchedCombo.SelectedItem, TaskSchedWeeklyItem);

            if (kind == MaintenanceTaskKind.Reboot && isInterval)
            {
                RulesStatusText.Text = Localizer.T("Tasks.RebootIntervalDenied");
                return;
            }

            var timeOfDay = 0;
            if (!isInterval)
            {
                var parsed = ParseHhmm(TaskTimeBox.Text);
                if (parsed is null)
                {
                    RulesStatusText.Text = Localizer.T("Tasks.NeedTime");
                    return;
                }

                timeOfDay = parsed.Value;
            }

            var weekdays = MaintenanceWeekdays.All;
            if (isWeekly)
            {
                weekdays = 0;
                var boxes = new[] { TaskDow0, TaskDow1, TaskDow2, TaskDow3, TaskDow4, TaskDow5, TaskDow6 };
                for (var day = 0; day < boxes.Length; day++)
                {
                    if (boxes[day].IsChecked == true)
                    {
                        weekdays |= 1 << day;
                    }
                }

                if (weekdays == 0)
                {
                    RulesStatusText.Text = Localizer.T("Tasks.NeedWeekday");
                    return;
                }
            }

            var interval = 0;
            if (isInterval)
            {
                if (!int.TryParse((TaskIntervalBox.Text ?? string.Empty).Trim(), out interval) || interval is < 1 or > 10080)
                {
                    RulesStatusText.Text = Localizer.T("Tasks.NeedInterval");
                    return;
                }
            }

            var scheduleKind = isInterval ? MaintenanceScheduleKind.Interval
                : isWeekly ? MaintenanceScheduleKind.Weekly
                : MaintenanceScheduleKind.Daily;
            var channelId = (TaskChannelCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
            var snapshotChannel = kind == MaintenanceTaskKind.Snapshot ? channelId : null;
            var taskEnabled = TaskEnabledBox.IsChecked == true;
            // 編輯不重置執行狀態（倉匨的 Update 刻意不動 last_run）：
            // 否則編一下「每天 03:00 重開機」等於把今天已跑抹掀，下一輪又到點重開一次。
            var wasEdit = _editingTaskId is not null;
            long taskId;
            if (_editingTaskId is long editingTaskId)
            {
                _taskRepo.Update(editingTaskId, name, kind, scheduleKind, timeOfDay, weekdays, interval, snapshotChannel, taskEnabled);
                taskId = editingTaskId;
                _editingTaskId = null;
            }
            else
            {
                taskId = _taskRepo.Add(name, kind, scheduleKind, timeOfDay, weekdays, interval, snapshotChannel, taskEnabled);
            }

            TasksEditPanel.Visibility = Visibility.Collapsed;
            RefreshTasks();
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T(wasEdit ? "Tasks.Updated" : "Tasks.Added"), taskId);
        }
        catch (Exception ex)
        {
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Tasks.Failed"), ex.Message);
        }
    }

    /// <summary>
    /// 手動立刻執行一次。重開機是會讓所有人離席的動作，必須先確認；
    /// 其餘動作（截圖／重連）無害，直接跑。結果寫回狀態列。
    /// </summary>
    private async void OnTaskRunClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: TaskRowViewModel row })
        {
            return;
        }

        var run = _runTaskNow;
        if (run is null)
        {
            RulesStatusText.Text = Localizer.T("Tasks.RunUnavailable");
            return;
        }

        if (row.Task.Kind == MaintenanceTaskKind.Reboot)
        {
            var confirm = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Localizer.T("Tasks.RunRebootConfirm"),
                row.Task.Name);
            if (MessageBox.Show(this, confirm, Localizer.T("Tasks.RunTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            var result = await run(row.Task.Id);
            RulesStatusText.Text = result is null
                ? Localizer.T("Tasks.RunUnavailable")
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Tasks.RunDone"), result);
        }
        catch (Exception ex)
        {
            RulesStatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Localizer.T("Tasks.Failed"), ex.Message);
        }
    }

    private void OnTaskToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: TaskRowViewModel row })
        {
            return;
        }

        _taskRepo.SetEnabled(row.Task.Id, !row.Task.Enabled);
        RefreshTasks();
    }

    private void OnTaskDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: TaskRowViewModel row })
        {
            return;
        }

        _taskRepo.Delete(row.Task.Id);
        RefreshTasks();
    }

    private static string Shorten(string json, int max)
    {
        var flat = json.Replace("\n", " ").Replace("\r", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}