namespace HeliVMS.Storage;

/// <summary>
/// 把網路模組的通道（<c>io_channels</c>）映到統一的接點表（<c>io_ports</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要映一次的動作：規則是掛在<b>接點</b>（<c>io_ports</c>）上的——攝影機乾接點與 Modbus
/// 模組的 DI/DO 走同一套規則引擎，這是刻意的（否則「哪一種來源可不可以掛規則」會變成兩份規則）。
/// 但操作員新增的是一台模組與它的通道，所以通道要自動出現在接點表裡，否則症狀是「模組加好了，
/// 規則清單裡卻找不到它的接點」。
/// </para>
/// <para>
/// 這個方法原本寫在桌面端的 <c>IoMonitorHost</c> 裡。搬到這裡是因為它是<b>資料層的恆等式</b>
/// （每個啟用的通道都有一個接點），而不是桌面程式的細節：純 Web／Agent 部署同樣需要它，
/// 而且放在 App 裡時任何測試都碰不到（App 專案沒有執行期測試）。
/// </para>
/// </remarks>
public static class IoPortMirror
{
    /// <summary>
    /// 依目前的通道清單補齊／移除接點，回傳這次新增或更新的接點數。
    /// </summary>
    /// <param name="io">通道來源。</param>
    /// <param name="ports">接點表。</param>
    public static int Sync(IoRepository io, IoPortRepository ports)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(ports);

        var channels = io.ListChannels();
        var seen = new HashSet<int>();
        var touched = 0;

        foreach (var ch in channels)
        {
            seen.Add(ch.Id);
            var kind = ch.Direction == "DO" ? IoPortKind.Do : IoPortKind.Di;
            var polarity = ch.Polarity ? IoPolarity.NormallyClosed : IoPolarity.NormallyOpen;
            ports.EnsureForIoChannel(ch.CameraId ?? 0, ch.Id, kind, ch.IoIndex + 1, ch.Name, polarity, ch.DebounceMs);
            touched++;
        }

        // 通道被刪掉時接點也要消失：留著一個永遠不會觸發的接點，操作員會照它掛規則，
        // 然後等一個不會發生的事件。
        foreach (var port in ports.List().Where(p => p.IoChannelId is not null))
        {
            if (port.IoChannelId is int id && !seen.Contains(id))
            {
                ports.DeleteByIoChannel(id);
            }
        }

        return touched;
    }
}
