using System.Net;
using System.Net.Sockets;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 多播探測的來源位址選擇。不逐網卡送多播時，多網卡的機器只找得到預設路由那一邊的攝影機，
/// 而畫面上沒有任何錯誤——所以這條選擇規則要釘死。
/// </summary>
public class MulticastSourceAddressesTests
{
    private static MulticastSourceAddresses.Adapter Nic(
        string name,
        bool up,
        bool multicast,
        params string[] addresses)
        => new(name, up, multicast, [.. addresses.Select(IPAddress.Parse)]);

    [Fact]
    public void 每個可多播的網卡各取一個來源()
    {
        var sources = MulticastSourceAddresses.Select(
        [
            Nic("監控網段", up: true, multicast: true, "192.168.1.10"),
            Nic("辦公網段", up: true, multicast: true, "10.0.0.5"),
        ]);

        Assert.Equal(["192.168.1.10", "10.0.0.5"], sources.Select(a => a.ToString()));
    }

    [Fact]
    public void 停用或不支援多播的網卡不列入()
    {
        var sources = MulticastSourceAddresses.Select(
        [
            Nic("停用", up: false, multicast: true, "192.168.1.10"),
            Nic("不支援多播", up: true, multicast: false, "192.168.1.11"),
            Nic("可用", up: true, multicast: true, "192.168.1.12"),
        ]);

        Assert.Equal(["192.168.1.12"], sources.Select(a => a.ToString()));
    }

    [Fact]
    public void 只取IPv4且不重複()
    {
        var sources = MulticastSourceAddresses.Select(
        [
            Nic("雙棧", up: true, multicast: true, "fe80::1", "192.168.1.10"),
            Nic("另一張也是同一位址", up: true, multicast: true, "192.168.1.10"),
        ]);

        Assert.Equal(["192.168.1.10"], sources.Select(a => a.ToString()));
    }

    [Fact]
    public void 只有環回時用環回()
    {
        // 單機環境與測試用得到；環回到不了攝影機，但比「完全沒有來源」好。
        var sources = MulticastSourceAddresses.Select(
        [
            Nic("loopback", up: true, multicast: true, "127.0.0.1"),
        ]);

        Assert.Equal(["127.0.0.1"], sources.Select(a => a.ToString()));
    }

    [Fact]
    public void 有實體位址時不會用環回()
    {
        var sources = MulticastSourceAddresses.Select(
        [
            Nic("loopback", up: true, multicast: true, "127.0.0.1"),
            Nic("實體", up: true, multicast: true, "192.168.1.10"),
        ]);

        Assert.Equal(["192.168.1.10"], sources.Select(a => a.ToString()));
    }

    [Fact]
    public void 什麼都沒有時退回不綁定()
    {
        // 退回 Any＝與舊行為相同（不綁來源位址），而不是讓搜尋整個不動。
        Assert.Equal([IPAddress.Any], MulticastSourceAddresses.Select([]));
    }

    [Fact]
    public void 本機列舉至少有一個來源且都是IPv4()
    {
        var sources = MulticastSourceAddresses.Enumerate();

        Assert.NotEmpty(sources);
        Assert.All(sources, address => Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily));
    }
}
