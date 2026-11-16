using HeliVMS.Shared;

namespace HeliVMS.Storage;

/// <summary>新增一顆網路 IO 模組的結果。</summary>
/// <param name="DeviceId">模組的資料庫識別碼。</param>
/// <param name="DeviceCreated"><c>true</c>＝這次真的新增了模組；<c>false</c>＝同一個位址已存在，只補通道。</param>
/// <param name="ChannelsCreated">這次補上的通道數（DI＋DO）。</param>
/// <param name="DiCount">補完之後這台模組的 DI 總數。</param>
/// <param name="DoCount">補完之後這台模組的 DO 總數。</param>
public readonly record struct IoModuleEnrollment(
    int DeviceId,
    bool DeviceCreated,
    int ChannelsCreated,
    int DiCount,
    int DoCount);

/// <summary>
/// 把「新增一顆 ADAM-6250」變成一次呼叫：建立模組 ＋ 它的 DI／DO 通道。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要有這一層：模組（<c>io_devices</c>）與通道（<c>io_channels</c>）是兩張表，而操作員
/// 的心智模型是「一台模組有 8 進 8 出」。只建立模組、忘了建立通道的症狀是「模組加好了，但
/// IO 頁面上一條接點都沒有」——沒有任何錯誤，因為兩張表都寫入成功。
/// </para>
/// <para>
/// <b>同一個位址重複新增是補齊，不是複製。</b>操作員換了機型（8DI→16DI）或上次只填了 4 個點時，
/// 再按一次新增就該把缺的通道補上。既有通道的名稱、極性、去抖與啟用狀態一律不動——那些是
/// 操作員調過的值，重建等於把他的設定清掉。
/// </para>
/// </remarks>
public static class IoModuleEnroller
{
    /// <summary>
    /// 建立（或補齊）一台模組及其 DI／DO 通道。
    /// </summary>
    /// <param name="io">IO 資料庫存取。</param>
    /// <param name="profile">機型（決定預設點數；表單可用 <paramref name="diCount"/>／<paramref name="doCount"/> 覆寫）。</param>
    /// <param name="name">模組名稱（空白時用機型顯示名）。</param>
    /// <param name="host">模組位址。</param>
    /// <param name="port">Modbus/TCP 埠。</param>
    /// <param name="unitId">Modbus 單元編號（ADAM 出廠為 1）。</param>
    /// <param name="pollMs">輪詢間隔（毫秒）。</param>
    /// <param name="diCount">DI 點數覆寫；<c>null</c>／0 時用機型預設。</param>
    /// <param name="doCount">DO 點數覆寫；<c>null</c>／0 時用機型預設。</param>
    /// <param name="actor">稽核記錄的操作者。</param>
    public static IoModuleEnrollment Enroll(
        IoRepository io,
        IoModuleProfile profile,
        string host,
        int? port = null,
        int? unitId = null,
        int? pollMs = null,
        string? name = null,
        int? diCount = null,
        int? doCount = null,
        string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var resolvedHost = host.Trim();
        var resolvedName = string.IsNullOrWhiteSpace(name) ? profile.DisplayName : name.Trim();
        var resolvedPort = port is > 0 and <= 65535 ? port.Value : profile.DefaultPort;
        var resolvedUnit = unitId is >= 1 and <= 247 ? unitId.Value : profile.DefaultUnitId;
        var resolvedPoll = pollMs is >= 50 and <= 60000 ? pollMs.Value : 500;
        var wantedDi = profile.ResolveDiCount(diCount);
        var wantedDo = profile.ResolveDoCount(doCount);

        // 同一個位址＝同一台模組。用位址而不是名稱比對：操作員常把名稱改成人看得懂的字
        // （「一樓電箱」），照名字比對會在改名後又新增出第二台，而兩台都連同一個 IP。
        var existing = io.ListDevices()
            .FirstOrDefault(d => string.Equals(d.Host, resolvedHost, StringComparison.OrdinalIgnoreCase)
                                 && d.Port == resolvedPort);
        var deviceCreated = existing is null;
        var deviceId = existing?.Id
                       ?? io.AddDevice(resolvedName, resolvedHost, resolvedPort, resolvedUnit, resolvedPoll, actor);

        var channelsCreated = 0;
        channelsCreated += EnsureChannels(io, deviceId, "DI", wantedDi, actor);
        channelsCreated += EnsureChannels(io, deviceId, "DO", wantedDo, actor);

        var channels = io.ListChannels(deviceId: deviceId);
        return new IoModuleEnrollment(
            deviceId,
            deviceCreated,
            channelsCreated,
            channels.Count(c => c.Direction == "DI"),
            channels.Count(c => c.Direction == "DO"));
    }

    /// <summary>補上缺少的 DI 或 DO 通道（既有的一律不動）。</summary>
    private static int EnsureChannels(IoRepository io, int deviceId, string direction, int wanted, string? actor)
    {
        // 已存在的索引編號不連續是常態（操作員刪掉第 3 點）；補齊時以「總數」為準而不是
        // 「下一個編號」——否則刪兩個補兩個會補出編號 9、10 的通道，而模組只有 8 個點。
        var existing = io.ListChannels(deviceId: deviceId, direction: direction);
        var created = 0;
        for (var index = 0; index < wanted; index++)
        {
            if (existing.Any(c => c.IoIndex == index))
            {
                continue;
            }

            io.AddChannel(
                deviceId,
                direction,
                index,
                IoModuleProfile.ChannelName(direction, index),
                actor: actor);
            created++;
        }

        return created;
    }
}
