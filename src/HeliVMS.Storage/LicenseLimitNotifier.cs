using System.Collections.Concurrent;

namespace HeliVMS.Storage;

/// <summary>
/// 「已達授權上限」寫入事件中心的唯一入口（§19.4 超限行為），人工錄影與排程錄影共用。
/// </summary>
/// <remarks>
/// 排程器每 30 秒調和一次，若每次都寫，事件中心會被同一條超限訊息灌滿，真正的授權事件反而被埋掉；
/// 故同一「頻道＋原因」在持續超限期間只寫一次，待頻道恢復可錄影後再允許下一次通知。
/// 人工路徑原本不會重複（使用者不會連點），但共用此類別讓兩條路徑行為一致，並讓測試能直接驗證去重。
/// </remarks>
public sealed class LicenseLimitNotifier
{
    private readonly AlarmEventRepository _events;
    private readonly ConcurrentDictionary<int, string> _notified = new();

    public LicenseLimitNotifier(SqliteStore store)
        : this(new AlarmEventRepository(store))
    {
    }

    public LicenseLimitNotifier(AlarmEventRepository events)
        => _events = events;

    /// <summary>
    /// 記錄一次被授權閘門拒絕的錄影嘗試。僅 <see cref="LicenseDecision.Valid"/>（額度用完，
    /// 而非授權本身失效）且該頻道尚未以相同原因通知時，才寫入事件中心。
    /// </summary>
    /// <returns>是否真的寫入了一筆事件。</returns>
    public bool Report(RecordingGateResult gate, DateTime nowUtc)
    {
        if (gate.Allowed || gate.Decision != LicenseDecision.Valid)
        {
            return false;
        }

        var reason = gate.Reason ?? "已達授權上限。";
        if (_notified.TryGetValue(gate.ChannelId, out var previous) && previous == reason)
        {
            return false;
        }

        _notified[gate.ChannelId] = reason;
        _events.Insert(gate.ChannelId, "license_limit", nowUtc, null, reason);
        return true;
    }

    /// <summary>頻道恢復可錄影時呼叫，讓下一次超限能再次通知。</summary>
    public void Clear(int channelId) => _notified.TryRemove(channelId, out _);

    /// <summary>清除所有已通知狀態（例如授權換發後）。</summary>
    public void ClearAll() => _notified.Clear();
}
