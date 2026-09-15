using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 離線事件源（M29，§8.5 事件平面）：追蹤每頻道正在發生的離線窗，並在復連時補齊
/// online 事件。斷線開窗以「一筆 end_time IS NULL 的 offline」表示（防重複），
/// 復連時以 UpdateEnd 收斂並補一筆帶持續時間的 online。
/// </summary>
public sealed class OfflineEventTracker
{
    private readonly AlarmEventRepository _events;

    public OfflineEventTracker(AlarmEventRepository events)
    {
        _events = events;
    }

    /// <summary>頻道進入離線：若尚未有開窗離線事件，寫入一筆（防同一離線期重複開窗）。</summary>
    public void MarkOffline(int channelId)
    {
        if (_events.FindOpenOffline(channelId) is null)
        {
            _events.Insert(channelId, "offline", DateTime.UtcNow, null, "connection lost");
        }
    }

    /// <summary>頻道復連：收斂開窗離線事件並補一筆 online（detail 含持續時間）。</summary>
    public bool MarkOnline(int channelId)
    {
        if (_events.FindOpenOffline(channelId) is not OpenOfflineEvent open)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        _events.UpdateEnd(open.Id, now, null);
        var duration = now - open.StartUtc;
        _events.Insert(channelId, "online", now, null, $"offline_duration={duration:hh\\:mm\\:ss}");
        return true;
    }

    /// <summary>收斂上次執行時期留下的所有開窗事件（啟動時呼叫；不補 fake online）。</summary>
    public void CloseOpenAtStartup()
    {
        _events.CloseOpenEvents(DateTime.UtcNow);
    }
}