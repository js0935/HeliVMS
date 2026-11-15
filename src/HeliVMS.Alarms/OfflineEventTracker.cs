using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 離線事件源（M29，§8.5 事件平面）：追蹤每頻道正在發生的離線窗，並在復連時補齊
/// online 事件。斷線開窗以「一筆 end_time IS NULL 的 offline」表示（防重複），
/// 復連時以 UpdateEnd 收斂並補一筆帶持續時間的 online。
/// </summary>
/// <remarks>
/// 兩個寫入點都回傳「剛剛真的寫進去的那一筆」（沒有寫入時 <c>null</c>），呼叫端才有辦法
/// 把同一筆事件推播出去。<b>只回報一次</b>是這個型別的核心語意：同一個離線期反覆的
/// Reconnecting 不該產生第二次開窗，否則事件中心會在幾分鐘內被同一次斷線洗版；
/// 復連時沒開窗（例如啟動後第一次連上）也一樣不回報，否則每一次開機都會多一筆假上線。
/// </remarks>
public sealed class OfflineEventTracker
{
    private readonly AlarmEventRepository _events;

    public OfflineEventTracker(AlarmEventRepository events)
    {
        _events = events;
    }

    /// <summary>
    /// 頻道進入離線：若尚未有開窗離線事件，寫入一筆並回傳它；同一個離線期重複呼叫回傳 null。
    /// </summary>
    public AlarmEventRecord? MarkOffline(int channelId)
    {
        if (_events.FindOpenOffline(channelId) is not null)
        {
            return null;
        }

        var now = Now();
        const string detail = "connection lost";
        var id = _events.Insert(channelId, EventTaxonomy.Offline, now, null, detail);
        return new AlarmEventRecord
        {
            Id = id,
            ChannelId = channelId,
            EventType = EventTaxonomy.Offline,
            StartUtc = now,
            Detail = detail,
        };
    }

    /// <summary>
    /// 頻道復連：收斂開窗離線事件並補一筆 online（detail 含持續時間），回傳那筆 online。
    /// 沒有開窗離線事件時不寫任何東西、回傳 null。
    /// </summary>
    public AlarmEventRecord? MarkOnline(int channelId)
    {
        if (_events.FindOpenOffline(channelId) is not OpenOfflineEvent open)
        {
            return null;
        }

        var now = Now();
        _events.UpdateEnd(open.Id, now, null);
        var detail = $"offline_duration={now - open.StartUtc:hh\\:mm\\:ss}";
        var id = _events.Insert(channelId, EventTaxonomy.Online, now, null, detail);
        return new AlarmEventRecord
        {
            Id = id,
            ChannelId = channelId,
            EventType = EventTaxonomy.Online,
            StartUtc = now,
            Detail = detail,
        };
    }

    /// <summary>收斂上次執行時期留下的所有開窗事件（啟動時呼叫；不補 fake online）。</summary>
    public void CloseOpenAtStartup()
    {
        _events.CloseOpenEvents(DateTime.UtcNow);
    }

    /// <summary>
    /// 現在時刻，截到毫秒。<c>segments</c>／<c>alarm_events</c> 的時間欄位存成
    /// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>（<see cref="SqliteStore.Iso"/>），回報出去的那筆事件
    /// 若不截，推播到事件中心／牆上面板的時刻會比資料列多出小數點後四位——同一筆事件在
    /// 兩個畫面上顯示不同時刻，追查時會被當成兩筆。
    /// </summary>
    private static DateTime Now()
    {
        var ticks = DateTime.UtcNow.Ticks;
        return new DateTime(ticks - ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
