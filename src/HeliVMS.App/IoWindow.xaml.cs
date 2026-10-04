using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 感測器 IO 測試視窗（M75，§14.1 #16）：列出 DI/DO 埠與動作鏈規則；「切換閉合＋餵入狀態」呼叫
/// <see cref="IoRuleEngine"/> 模擬接點變化，狀態列回報觸發事件。實體採樣（GPIO/模組）留真機層。
/// </summary>
public partial class IoWindow : Window
{
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly IoRuleEngine _engine;
    private readonly Dictionary<long, bool> _physical = new();
    private readonly ObservableCollection<PortRow> _portRows = new();
    private readonly ObservableCollection<RuleRow> _ruleRows = new();
    private int _triggerCount;

    public IoWindow(SqliteStore store)
    {
        _ports = new IoPortRepository(store);
        _rules = new IoRuleRepository(store);
        _engine = new IoRuleEngine(_ports.List(), _rules.List());
        InitializeComponent();
        ApplyI18n();

        IoPortList.ItemsSource = _portRows;
        IoRuleList.ItemsSource = _ruleRows;

        foreach (var di in _ports.List().Where(p => p.Kind == IoPortKind.Di))
        {
            IoDiCombo.Items.Add(string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Io.DiComboItem"), di.Number, di.Name));
            _physical[di.Id] = false;
        }

        if (IoDiCombo.Items.Count > 0)
        {
            IoDiCombo.SelectedIndex = 0;
        }

        RefreshViews();
    }

    /// <summary>依現況語言套用標題、標籤、按鈕與兩份清單的欄位文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Io.Title");
        IoHeadingText.Text = Localizer.T("Io.Heading");
        IoDiLabel.Text = Localizer.T("Io.DiPort");
        IoClosedCheck.Content = Localizer.T("Io.Closed");
        IoFireButton.Content = Localizer.T("Io.Fire");
        IoFireButton.ToolTip = Localizer.T("Io.FireTip");

        if (IoPortList.View is GridView portGrid && portGrid.Columns.Count >= 5)
        {
            portGrid.Columns[0].Header = Localizer.T("Io.ColKind");
            portGrid.Columns[2].Header = Localizer.T("Io.ColName");
            portGrid.Columns[3].Header = Localizer.T("Io.ColPolarity");
            portGrid.Columns[4].Header = Localizer.T("Io.ColState");
        }

        if (IoRuleList.View is GridView ruleGrid && ruleGrid.Columns.Count >= 5)
        {
            ruleGrid.Columns[0].Header = Localizer.T("Io.ColRule");
            ruleGrid.Columns[1].Header = Localizer.T("Io.ColInputPort");
            ruleGrid.Columns[2].Header = Localizer.T("Io.ColAction");
            ruleGrid.Columns[3].Header = Localizer.T("Io.ColRetrigger");
            ruleGrid.Columns[4].Header = Localizer.T("Io.ColEnabled");
        }
    }

    private sealed record PortRow(long Id, string KindText, int Number, string Name, string PolarityText, string StateText);

    private sealed record RuleRow(long Id, string InputText, string ActionText, int RetriggerSec, string EnabledText);

    private void RefreshViews()
    {
        _portRows.Clear();
        foreach (var p in _ports.List())
        {
            var value = p.Kind switch
            {
                IoPortKind.Di => _engine.GetInputState(p.Id),
                _ => _engine.GetOutputState(p.Id),
            };
            _portRows.Add(new PortRow(
                p.Id,
                p.Kind == IoPortKind.Di ? "DI" : "DO",
                p.Number,
                p.Name,
                p.Polarity == IoPolarity.NormallyClosed ? Localizer.T("Io.NormallyClosed") : Localizer.T("Io.NormallyOpen"),
                value is null ? Localizer.T("Io.Unknown") : (value.Value ? Localizer.T("Io.Open") : Localizer.T("Io.Close"))));
        }

        _ruleRows.Clear();
        foreach (var r in _rules.List())
        {
            var input = _ports.Get(r.InputPortId);
            var actionText = r.ActionKind switch
            {
                IoActionKind.Alarm => string.Format(CultureInfo.InvariantCulture, Localizer.T("Io.ActionAlarm"), r.EventType),
                _ => string.Format(CultureInfo.InvariantCulture, Localizer.T("Io.ActionSwitchDo"), _ports.Get(r.OutputPortId ?? 0)?.Number ?? 0),
            };
            _ruleRows.Add(new RuleRow(
                r.Id,
                input is null ? $"#{r.InputPortId}" : $"DI#{input.Number}",
                actionText,
                r.RetriggerSec,
                r.Enabled ? Localizer.T("Io.Enabled") : Localizer.T("Io.Disabled")));
        }
    }

    private void OnFireClicked(object sender, RoutedEventArgs e)
    {
        var di = SelectedDi();
        if (di is null)
        {
            IoStatusText.Text = Localizer.T("Io.SelectDi");
            return;
        }

        var closed = IoClosedCheck.IsChecked == true;
        _physical[di.Id] = closed;
        IoPhysicalLabel.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("Io.Physical"),
            _physical[di.Id] ? Localizer.T("Io.ClosedState") : Localizer.T("Io.OpenState"));
        var actions = _engine.OnInput(di.Id, closed, DateTime.UtcNow);
        if (actions.Count == 0)
        {
            IoStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Io.NoTrigger"), di.Number,
                _engine.GetInputState(di.Id) == true ? Localizer.T("Io.Open") : Localizer.T("Io.Close"));
        }
        else
        {
            _triggerCount++;
            var summary = string.Join(Localizer.T("Io.Separator"), actions.Select(a =>
                a.Kind == IoActionKind.Alarm
                    ? string.Format(CultureInfo.InvariantCulture, Localizer.T("Io.SummaryAlarm"), a.EventType)
                    : string.Format(CultureInfo.InvariantCulture, Localizer.T("Io.SummaryDo"),
                        _engine.GetOutputState(a.OutputPortId ?? 0) == true ? Localizer.T("Io.Open") : Localizer.T("Io.Close"))));
            IoStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Io.Triggered"), _triggerCount, summary);
        }

        RefreshViews();
    }

    private IoPortRecord? SelectedDi()
    {
        var idx = IoDiCombo.SelectedIndex;
        if (idx < 0)
        {
            return null;
        }

        var diList = _ports.List().Where(p => p.Kind == IoPortKind.Di).ToList();
        return idx < diList.Count ? diList[idx] : null;
    }
}