using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// M74 動作鏈的 DO 輸出寫入抽象。引擎只決定「邏輯狀態應為何」，實際打到哪種硬體
/// （攝影機 ONVIF 繼電器、Modbus 模組、GPIO…）由實作決定，讓 Storage 層維持純粹、可測。
/// </summary>
public interface IIoOutputWriter
{
    /// <summary>把輸出埠的邏輯狀態寫到實際硬體；<paramref name="logicalOn"/>＝true 表示閉合／通電。</summary>
    Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default);
}

/// <summary>
/// 感測器動作鏈收尾（M74 落地層）：把 <see cref="IoRuleEngine"/> 接上真實效果。
/// <para>
/// M74 只交付純 BCL 引擎、M75 只交付手動模擬視窗，兩者都沒有把引擎產出的動作落到任何地方——
/// 規則「觸發」後 alarm_events 沒有事件、DO 也沒有真的切換，畫面看起來有反應、系統卻什麼都沒記。
/// 這個宿主補上這一段：DI 變化餵入引擎後，Alarm 動作寫入 <c>alarm_events</c>（綁該埠的頻道）、
/// ToggleDo 動作交給注入的 <see cref="IIoOutputWriter"/>。
/// </para>
/// </summary>
public sealed class IoRuleHost
{
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly IIoOutputWriter _writer;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private IoRuleEngine _engine;

    public IoRuleHost(
        IoPortRepository ports,
        IoRuleRepository rules,
        AlarmEventRepository events,
        IIoOutputWriter? outputWriter = null,
        TimeProvider? clock = null)
    {
        _ports = ports;
        _rules = rules;
        _events = events;
        _writer = outputWriter ?? NullIoOutputWriter.Instance;
        _clock = clock ?? TimeProvider.System;
        _engine = new IoRuleEngine(_ports.List(), _rules.List());
    }

    /// <summary>設定變更後重建引擎（埠／規則重載；引擎狀態歸零，等同重新上電）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _engine = new IoRuleEngine(_ports.List(), _rules.List());
        }
    }

    /// <summary>目前邏輯輸入狀態；尚未收過該埠任何訊號→null。</summary>
    public bool? GetInputState(long portId)
    {
        lock (_gate)
        {
            return _engine.GetInputState(portId);
        }
    }

    /// <summary>目前邏輯輸出狀態；尚未被任何規則切換過→null。</summary>
    public bool? GetOutputState(long portId)
    {
        lock (_gate)
        {
            return _engine.GetOutputState(portId);
        }
    }

    /// <summary>清空所有引擎狀態（等同重新上電；不停用規則或埠）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _engine.Reset();
        }
    }

    /// <summary>
    /// 餵入一個實體 DI 變化並落地其動作，回傳本次觸發的動作（供介面顯示）。
    /// 警報動作在方法內同步落地；DO 動作以 <see cref="IIoOutputWriter"/> 非同步寫出。
    /// </summary>
    public async Task<IReadOnlyList<IoAction>> FeedAsync(
        long diPortId,
        bool physicalOn,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<IoAction> actions;
        lock (_gate)
        {
            actions = _engine.OnInput(diPortId, physicalOn, now);
        }

        foreach (var action in actions)
        {
            if (action.Kind == IoActionKind.Alarm)
            {
                var detail = $"rule={action.RuleId};port={action.InputPortId};event={action.EventType}";
                _events.Insert((int)action.ChannelId, action.EventType ?? "io_trigger", now, null, detail);
            }
            else if (action.Kind == IoActionKind.ToggleDo && action.OutputPortId is long outputId)
            {
                var state = GetOutputState(outputId) == true;
                await _writer.WriteAsync(outputId, state, cancellationToken).ConfigureAwait(false);
            }
        }

        return actions;
    }

    /// <summary>未提供輸出寫入器時使用：DO 動作仍計入引擎狀態，但不寫任何硬體。</summary>
    private sealed class NullIoOutputWriter : IIoOutputWriter
    {
        public static readonly NullIoOutputWriter Instance = new();

        public Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
