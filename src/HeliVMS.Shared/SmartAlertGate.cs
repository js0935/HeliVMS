namespace HeliVMS.Shared;

/// <summary>智慧警報的窗內判定結果。</summary>
/// <param name="RuleId">規則 ID。</param>
/// <param name="WindowCount">窗內（含本筆）命中次數。</param>
/// <param name="Reached">是否達到門檻。</param>
/// <param name="FirstCrossing">是否為這個窗的「首次跨過」。</param>
public readonly record struct SmartAlertDecision(
    long RuleId,
    int WindowCount,
    bool Reached,
    bool FirstCrossing);

/// <summary>
/// 智慧警報的「每窗只告警一次」閘門（M54，§14.7 #8）。
/// </summary>
/// <remarks>
/// <para>
/// 這是整個聚合功能最容易寫錯的地方，所以刻意抽成純邏輯：門檻跨過之後，
/// 同一個窗內的後續事件<b>不該</b>再告一次。舊的寫法是每次都比對窗內計數，
/// 門檻一旦跨過就永遠不再抑制，於是 8 次的門檻在跨過之後每一筆都照送——
/// 正是這條規則要擋掉的洪泛。</para>
/// <para>
/// 計數刻意由呼叫端提供（而非本類別自己記錄時間戳）：現場重啟 App 後記憶體歸零，
/// 若用它算次數，操作員重啟 NVR 就能洗掉已達門檻的告警。</para>
/// </remarks>
public sealed class SmartAlertGate
{
    private readonly Dictionary<long, DateTime> _claimedAtUtc = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// 登記一筆命中並回傳是否應該在此刻告警。
    /// </summary>
    /// <param name="ruleId">規則 ID。</param>
    /// <param name="windowCount">呼叫端算出的窗內次數（含本筆）。</param>
    /// <param name="minEvents">門檻。</param>
    /// <param name="nowUtc">窗的右端（UTC）。</param>
    /// <param name="windowMinutes">窗長（分）。</param>
    public SmartAlertDecision Observe(long ruleId, int windowCount, int minEvents, DateTime nowUtc, int windowMinutes)
    {
        var reached = windowCount >= minEvents;
        if (!reached)
        {
            return new SmartAlertDecision(ruleId, windowCount, false, false);
        }

        var fromUtc = nowUtc.AddMinutes(-windowMinutes);

lock (_gate)
        {
            if (_claimedAtUtc.TryGetValue(ruleId, out var at) && at >= fromUtc)
            {
                return new SmartAlertDecision(ruleId, windowCount, true, false);
            }

            _claimedAtUtc[ruleId] = nowUtc;
            return new SmartAlertDecision(ruleId, windowCount, true, true);
        }
    }

    /// <summary>清除某規則的「已告警」標記（規則被改動或刪除時呼叫）。</summary>
    public void Reset(long ruleId)
    {
        lock (_gate)
        {
            _claimedAtUtc.Remove(ruleId);
        }
    }

    /// <summary>清除全部標記。</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _claimedAtUtc.Clear();
        }
    }
}
