using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 「搜尋位址」輸入的展開。現場痛點是「一台一台輸入 IP」，這個展開就是替代品：
/// 打一個網段就該變成一份可掃描的清單，而且打得錯要看得出來，不能變成一次註定失敗的探測。
/// </summary>
public class DiscoveryAddressRangeTests
{
    [Fact]
    public void 單一位址原樣回傳()
    {
        var result = DiscoveryAddressRange.Expand("192.168.1.64");

        Assert.Equal(["192.168.1.64"], result.Addresses);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void 主機名與完整網址也當成一個位址()
    {
        var result = DiscoveryAddressRange.Expand("cam01.local, http://192.168.1.9/onvif/device_service");

        Assert.Equal(["cam01.local", "http://192.168.1.9/onvif/device_service",], result.Addresses);
    }

    [Fact]
    public void CIDR網段展開成可用主機且不含網路與廣播位址()
    {
        var result = DiscoveryAddressRange.Expand("192.168.1.0/30");

        // /30 的四個位址裡，.0 是網路位址、.3 是廣播位址，可用主機只有 .1 與 .2。
        Assert.Equal(["192.168.1.1", "192.168.1.2"], result.Addresses);
    }

    [Fact]
    public void 末碼範圍展開成連續位址()
    {
        var result = DiscoveryAddressRange.Expand("192.168.5.10-13");

        Assert.Equal(["192.168.5.10", "192.168.5.11", "192.168.5.12", "192.168.5.13"], result.Addresses);
    }

    [Fact]
    public void 完整範圍展開且頭尾顛倒視為輸入錯誤()
    {
        Assert.Equal(
            ["10.0.0.1", "10.0.0.2"],
            DiscoveryAddressRange.Expand("10.0.0.1-10.0.0.2").Addresses);

        // 倒過來寫不該掃描整個反向空間，而且那個 token 會被整批忽略（清單為空）。
        Assert.Empty(DiscoveryAddressRange.Expand("10.0.0.9-10.0.0.2").Addresses);
    }

    [Fact]
    public void 一個斜線寫壞的網段不會被當成主機名去掃()
    {
        // 192.168.1.0/99 是打錯的 CIDR。若退回「字面主機名」，掃描只會失敗，
        // 操作員看到的是「沒有設備」而不是「輸入有問題」。
        Assert.Empty(DiscoveryAddressRange.Expand("192.168.1.0/99").Addresses);

        // 但合法的網段仍要能與其他位址混用。
        var mixed = DiscoveryAddressRange.Expand("192.168.1.0/30 192.168.9.9");
        Assert.Equal(["192.168.1.1", "192.168.1.2", "192.168.9.9"], mixed.Addresses);
    }

    [Fact]
    public void 重複位址只留一份並保持輸入順序()
    {
        var result = DiscoveryAddressRange.Expand("192.168.1.1, cam1.local, 192.168.1.1");

        Assert.Equal(["192.168.1.1", "cam1.local"], result.Addresses);
    }

    [Fact]
    public void 超過上限會截斷並明確回報()
    {
        var result = DiscoveryAddressRange.Expand("10.0.0.0/16");

        Assert.Equal(DiscoveryAddressRange.MaxAddresses, result.Addresses.Count);
        Assert.True(result.Truncated, "超過上限必須回報截斷，否則操作員以為整段都掃過了");
        // 截斷是從頭取，不是隨機挑：掃描範圍要可預期。
        Assert.Equal("10.0.0.1", result.Addresses[0]);
    }

    [Fact]
    public void 空輸入沒有位址也不是截斷()
    {
        var result = DiscoveryAddressRange.Expand("   ");

        Assert.Empty(result.Addresses);
        Assert.False(result.Truncated);
    }
}
