using System.Collections.ObjectModel;
using System.Windows;
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

        IoPortList.ItemsSource = _portRows;
        IoRuleList.ItemsSource = _ruleRows;

        foreach (var di in _ports.List().Where(p => p.Kind == IoPortKind.Di))
        {
            IoDiCombo.Items.Add($"DI#{di.Number}（{di.Name}）");
            _physical[di.Id] = false;
        }

        if (IoDiCombo.Items.Count > 0)
        {
            IoDiCombo.SelectedIndex = 0;
        }

        RefreshViews();
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
                p.Polarity == IoPolarity.NormallyClosed ? "常閉" : "常開",
                value is null ? "－" : (value.Value ? "開" : "關")));
        }

        _ruleRows.Clear();
        foreach (var r in _rules.List())
        {
            var input = _ports.Get(r.InputPortId);
            var actionText = r.ActionKind switch
            {
                IoActionKind.Alarm => $"警報→{r.EventType}",
                _ => $"切換 DO#{_ports.Get(r.OutputPortId ?? 0)?.Number ?? 0}",
            };
            _ruleRows.Add(new RuleRow(
                r.Id,
                input is null ? $"#{r.InputPortId}" : $"DI#{input.Number}",
                actionText,
                r.RetriggerSec,
                r.Enabled ? "啟用" : "停用"));
        }
    }

    private void OnFireClicked(object sender, RoutedEventArgs e)
    {
        var di = SelectedDi();
        if (di is null)
        {
            IoStatusText.Text = "請先選擇 DI 埠。";
            return;
        }

        var closed = IoClosedCheck.IsChecked == true;
        _physical[di.Id] = closed;
        IoPhysicalLabel.Text = $"實體:{(_physical[di.Id] ? "閉合" : "斷開")}";
        var actions = _engine.OnInput(di.Id, closed, DateTime.UtcNow);
        if (actions.Count == 0)
        {
            IoStatusText.Text = $"未有觸發（DI#{di.Number} 邏輯:{(_engine.GetInputState(di.Id) == true ? "開" : "關")}）。";
        }
        else
        {
            _triggerCount++;
            var summary = string.Join("；", actions.Select(a =>
                a.Kind == IoActionKind.Alarm
                    ? $"警報[{a.EventType}]"
                    : $"DO→{(_engine.GetOutputState(a.OutputPortId ?? 0) == true ? "開" : "關")}"));
            IoStatusText.Text = $"觸發 #{_triggerCount}：{summary}";
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