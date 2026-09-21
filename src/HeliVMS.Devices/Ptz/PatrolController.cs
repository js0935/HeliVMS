namespace HeliVMS.Devices.Ptz;

/// <summary>巡航步驟：某預設點＋停留時間（M69）。</summary>
public sealed record PatrolStep(string PresetName, TimeSpan Dwell);

/// <summary>巡航排程（每日時段 WindowStart~WindowEnd；Start&gt;End 視為跨午夜）。</summary>
public sealed record PatrolPlan(
    string Name,
    bool Enabled,
    TimeSpan WindowStart,
    TimeSpan WindowEnd,
    IReadOnlyList<PatrolStep> Steps);

/// <summary>下一次動作。</summary>
public enum PatrolActionKind
{
    Idle,
    MoveTo,
    Wait,
}

/// <summary>巡航控制器每次 Tick 的產出。</summary>
public sealed record PatrolAction(
    PatrolActionKind Kind,
    string? PresetName = null,
    TimeSpan? Remaining = null);

/// <summary>
/// PTZ 巡航排程器（M69，L0 純演算法，不觸網）——依注入時鐘決定「現在該到哪個預設點／停留／停擺」。
/// 狀態機：停用或時段外→Idle；時段起始進首點；每點停留 Dwell 期滿依序進下一點（末後繞回首）；
/// 同點停留中重複 Tick 回 Wait(剩餘) 不重複 MoveTo；Hold 期間凍結進度，Release 後續走剩餘停留。
/// </summary>
public sealed class PatrolController
{
    private readonly PatrolPlan _plan;
    private readonly Func<DateTime> _clock;
    private int _index = -1;
    private DateTime _stepAnchorUtc;
    private DateTime? _holdUntilUtc;
    private DateTime? _holdStartUtc;

    /// <param name="plan">巡航排程（步驟＋時段）。</param>
    /// <param name="clock">注入時鐘（預設 UTC now）；測試可傳可變假時鐘。</param>
    public PatrolController(PatrolPlan plan, Func<DateTime>? clock = null)
    {
        _plan = plan;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public PatrolPlan Plan => _plan;

    public int CurrentIndex => _index;

    public PatrolAction Tick()
    {
        var now = _clock();

        if (!_plan.Enabled || _plan.Steps.Count == 0)
        {
            return Idle();
        }

        var tod = now.TimeOfDay;
        if (!InsideWindow(tod))
        {
            _index = -1;
            return Idle();
        }

        if (_holdUntilUtc is { } holdUntil)
        {
            if (now < holdUntil)
            {
                return Idle();
            }

            AccrueHold(holdUntil);
        }

        if (_index < 0)
        {
            return BeginAt(0, now);
        }

        var step = _plan.Steps[_index];
        var dwellUntil = _stepAnchorUtc + step.Dwell;
        if (now < dwellUntil)
        {
            return Wait(dwellUntil - now);
        }

        return BeginAt((_index + 1) % _plan.Steps.Count, now);
    }

    /// <summary>操作員/事件介入：暫停巡航至指定時刻（或持續 span）；hold 期間停留時間凍結。</summary>
    public void HoldFor(TimeSpan duration) => HoldUntil(_clock() + duration);

    public void HoldUntil(DateTime utc)
    {
        var until = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        _holdUntilUtc = until;
        _holdStartUtc ??= _clock();
    }

    public void ReleaseHold()
    {
        if (_holdUntilUtc is { })
        {
            AccrueHold(_clock()); // 提前釋放：凍結實際已過時間（至釋放此刻）
        }

        _holdUntilUtc = null;
    }

    /// <summary>把 hold 期間加到本點停留錨點——停留不被消耗（凍結）；hold 到 point 時刻才算。 </summary>
    private void AccrueHold(DateTime holdEnd)
    {
        if (_holdStartUtc is { } start && _index >= 0)
        {
            _stepAnchorUtc += holdEnd - start;
        }

        _holdStartUtc = null;
    }

    private PatrolAction BeginAt(int index, DateTime now)
    {
        _index = index;
        _stepAnchorUtc = now;
        return new PatrolAction(PatrolActionKind.MoveTo, _plan.Steps[index].PresetName, _plan.Steps[index].Dwell);
    }

    private static PatrolAction Idle() => new(PatrolActionKind.Idle);

    private static PatrolAction Wait(TimeSpan remaining) => new(PatrolActionKind.Wait, null, remaining);

    private bool InsideWindow(TimeSpan tod)
    {
        var start = _plan.WindowStart;
        var end = _plan.WindowEnd;
        if (start <= end)
        {
            return tod >= start && tod <= end;
        }

        // 跨午夜：Start→24:00 或 00:00→End
        return tod >= start || tod <= end;
    }
}