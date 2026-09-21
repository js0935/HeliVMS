namespace HeliVMS.Storage;

/// <summary>引擎產出之一個 IO 動作（M74）：由規則觸發，App/採樣層負責落地（alarm_events / DO 實際輸出）。</summary>
public sealed record IoAction(
    IoActionKind Kind,
    long RuleId,
    long ChannelId,
    long InputPortId,
    long? OutputPortId,
    string? EventType);

/// <summary>
/// 感測器動作鏈引擎（M74，§14.1 #16，純 BCL）：以「邏輯值」運算——
/// 實體值經極性轉邏輯，僅在邏輯上昇沿（false→true）依該埠規則觸發。
/// 同一規則在冷卻窗（retrigger_sec）內不重複觸發；時間由注入時鐘提供（可測）。
/// DO 輸出狀態由引擎持有（ToggleDo 時反相），以 GetOutputState 讀取。
/// </summary>
public sealed class IoRuleEngine
{
    private readonly IReadOnlyDictionary<long, IReadOnlyList<IoRuleRecord>> _rulesByInput;
    private readonly IReadOnlyDictionary<long, IoPortRecord> _ports;

    private readonly Dictionary<long, bool> _inputState = new();
    private readonly Dictionary<long, bool> _outputState = new();
    private readonly Dictionary<long, DateTime> _lastTrigger = new();

    public IoRuleEngine(IReadOnlyCollection<IoPortRecord> ports, IReadOnlyCollection<IoRuleRecord> rules)
    {
        _ports = ports.ToDictionary(p => p.Id);
        _rulesByInput = rules
            .GroupBy(r => r.InputPortId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IoRuleRecord>)g.OrderBy(r => r.Id).ToList());
    }

    /// <summary>目前邏輯輸入狀態；尚未收過該埠任何訊號→null。</summary>
    public bool? GetInputState(long portId) => _inputState.TryGetValue(portId, out var v) ? v : null;

    /// <summary>目前邏輯輸出狀態；尚未被任何規則切換過→null。</summary>
    public bool? GetOutputState(long portId) => _outputState.TryGetValue(portId, out var v) ? v : null;

    /// <summary>清空所有狀態（等同新引擎）。</summary>
    public void Reset()
    {
        _inputState.Clear();
        _outputState.Clear();
        _lastTrigger.Clear();
    }

    /// <summary>
    /// 餵入一個實體輸入變化。僅在邏輯上昇沿觸發；回傳本次所產出之動作（按規則 Id 排序）。
    /// <paramref name="physicalOn"/>＝實體接點閉合與否；極性於引擎內轉換邏輯值。
    /// </summary>
    public IReadOnlyList<IoAction> OnInput(long portId, bool physicalOn, DateTime utcNow)
    {
        if (!_ports.TryGetValue(portId, out var port))
        {
            throw new ArgumentOutOfRangeException(nameof(portId), "未知埠。");
        }

        var logical = ToLogical(physicalOn, port.Polarity);
        var previous = _inputState.TryGetValue(portId, out var p) ? p : (bool?)null;
        _inputState[portId] = logical;

        if (previous == true || logical != true)
        {
            return Array.Empty<IoAction>();
        }

        if (_rulesByInput.TryGetValue(portId, out var rules))
        {
            var actions = new List<IoAction>();
            foreach (var rule in rules)
            {
                if (!rule.Enabled)
                {
                    continue;
                }

                var last = _lastTrigger.TryGetValue(rule.Id, out var t) ? t : (DateTime?)null;
                if (last is not null && (utcNow - last.Value).TotalSeconds < rule.RetriggerSec)
                {
                    continue;
                }

                if (rule.ActionKind == IoActionKind.Alarm)
                {
                    actions.Add(new IoAction(
                        IoActionKind.Alarm, rule.Id, port.ChannelId, portId, null, rule.EventType));
                }
                else if (rule.OutputPortId is { } outputId)
                {
                    var next = !(_outputState.TryGetValue(outputId, out var o) && o);
                    _outputState[outputId] = next;
                    actions.Add(new IoAction(
                        IoActionKind.ToggleDo, rule.Id, port.ChannelId, portId, outputId, null));
                }

                _lastTrigger[rule.Id] = utcNow;
            }

            return actions;
        }

        return Array.Empty<IoAction>();
    }

    private static bool ToLogical(bool physicalOn, IoPolarity polarity)
        => polarity == IoPolarity.NormallyClosed ? !physicalOn : physicalOn;
}