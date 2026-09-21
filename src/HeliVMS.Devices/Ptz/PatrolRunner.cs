namespace HeliVMS.Devices.Ptz;

/// <summary>把 MoveTo 動作付諸實行的執行者（M70）；真實實作＝ONVIF GotoPreset。</summary>
public interface IPtzExecutor
{
    Task GotoPresetAsync(string presetName, CancellationToken ct = default);
}

/// <summary>
/// 巡航執行器（M70，L1）——取 PatrolController 的 Tick 動作；MoveTo 才呼叫 executor；
/// Wait/Idle 直接回（不觸網）。executor 例外原樣傳出（App 面可記錄離線）。
/// </summary>
public sealed class PatrolRunner
{
    private readonly PatrolController _controller;
    private readonly IPtzExecutor _executor;

    public PatrolRunner(PatrolController controller, IPtzExecutor executor)
    {
        _controller = controller;
        _executor = executor;
    }

    public PatrolController Controller => _controller;

    public async Task<PatrolAction> TickOnceAsync(CancellationToken ct = default)
    {
        var action = _controller.Tick();
        if (action.Kind == PatrolActionKind.MoveTo)
        {
            await _executor.GotoPresetAsync(action.PresetName!, ct);
        }

        return action;
    }
}