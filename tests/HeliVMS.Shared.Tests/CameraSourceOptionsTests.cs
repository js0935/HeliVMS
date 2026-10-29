using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 「來源攝影機」下拉（<see cref="CameraSourceOptions"/>）的行為契約。
/// </summary>
/// <remarks>
/// 操作員靠 IP 認機器（同型號攝影機的名稱往往一模一樣），而一台攝影機只能對應一條頻道
/// （憑證、PTZ、錄影路徑都掛在這條關聯上）。這兩件事看畫面都不一定看得出來：
/// 名稱顯示錯只會被當成「那台機器就叫這個名字」，已綁定的攝影機重複出現則要等到
/// 兩條頻道搶同一組 RTSP 串流、畫面互踢時才會爆發，而且日誌只會說連線失敗。
/// </remarks>
public sealed class CameraSourceOptionsTests
{
    [Fact]
    public void 顯示攝影機IP而不是名稱()
    {
        var options = CameraSourceOptions.Build([Device(1, "IPC", "192.168.1.21")], []);

        var option = Assert.Single(options);
        Assert.Equal(1, option.DeviceId);
        Assert.Equal("192.168.1.21", option.Label);
    }

    [Fact]
    public void 沒有IP的舊資料退回顯示名稱()
    {
        var options = CameraSourceOptions.Build([Device(1, "大門", "  ")], []);

        Assert.Equal("大門", Assert.Single(options).Label);
    }

    [Fact]
    public void 已加入頻道的攝影機不會再出現在清單()
    {
        var devices = new[]
        {
            Device(1, "IPC-A", "192.168.1.21"),
            Device(2, "IPC-B", "192.168.1.22"),
            Device(3, "IPC-C", "192.168.1.23"),
        };

        // 頻道 10 綁了設備 2；頻道 11 是手動輸入（DeviceId 為 null），不該排掉任何人。
        var options = CameraSourceOptions.Build(devices, [2, null]);

        Assert.Equal([1, 3], options.Select(o => o.DeviceId).ToArray());
    }

    [Fact]
    public void 候選清單依設備標識排序()
    {
        var devices = new[]
        {
            Device(7, "IPC-G", "192.168.1.27"),
            Device(3, "IPC-C", "192.168.1.23"),
        };

        Assert.Equal([3, 7], CameraSourceOptions.Build(devices, []).Select(o => o.DeviceId).ToArray());
    }

    private static DeviceRecord Device(int id, string name, string ip) =>
        new() { Id = id, Name = name, Ip = ip };
}
