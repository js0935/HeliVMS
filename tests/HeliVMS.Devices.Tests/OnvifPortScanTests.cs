using System.Net;
using System.Net.Sockets;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 掃描的 TCP 預檢。真實 socket（loopback）測試：預檢要能分辨「有人在聽」與「沒人聽」，
/// 逾時要算「沒有」，而且不能把取消當成「沒有」（否則操作員按取消後掃描會繼續跑完）。
/// </summary>
public class OnvifPortScanTests
{
    [Fact]
    public async Task 有人在聽的埠才會回報()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        // 找一個沒有人在聽的埠：綁定後立刻放掉（測試內短時間使用，且只比對它不在結果裡）。
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var open = await OnvifPortScan.OpenPortsAsync(
            "127.0.0.1",
            [closedPort, openPort],
            connectTimeout: TimeSpan.FromMilliseconds(500));

        Assert.Equal([openPort], open);
    }

    [Fact]
    public async Task 結果順序與輸入相同()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
        var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;

        var open = await OnvifPortScan.OpenPortsAsync(
            "127.0.0.1",
            [secondPort, firstPort],
            connectTimeout: TimeSpan.FromMilliseconds(500));

        // 清單順序即優先序（慣例埠先於其他）；順序被改掉會讓「先用 80」的意圖失效。
        Assert.Equal([secondPort, firstPort], open);
    }

    [Fact]
    public async Task 逾時算沒有而不會卡住()
    {
        // 10.255.255.1 是私有網段裡不會有回應的位址：連線逾時必須被當成「沒有人在聽」。
        var started = DateTime.UtcNow;
        var open = await OnvifPortScan.OpenPortsAsync(
            "10.255.255.1",
            [80],
            connectTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Empty(open);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "逾時必須真的結束");
    }

    [Fact]
    public async Task 取消會往上拋而不是當成沒有埠()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OnvifPortScan.OpenPortsAsync("127.0.0.1", [80], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task 連線途中取消也要往上拋而不是當成沒有埠()
    {
        // 連線卡住時操作員按取消：若把「取消」與「逾時」一起吞掉，掃描會繼續跑完剩下的位址，
        // 按了取消卻沒停——所以要分辨這兩者（逾時吞掉、取消往上拋）。
        // 用注入的探測（而不是真實逾時）才能確定測到的是那個判斷，不依賴網路時序。
        using var cts = new CancellationTokenSource();
        var probe = (string _, int __, TimeSpan ___, CancellationToken token) =>
        {
            // 同步取消：旗標立刻設定，不必等 CancelAsync 的回呼排程（否則測到的是排程時序）。
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OnvifPortScan.OpenPortsAsync(
                "127.0.0.1",
                [80],
                probe: probe,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task 沒有候選埠時不會連線()
    {
        var called = false;
        var open = await OnvifPortScan.OpenPortsAsync(
            "127.0.0.1",
            [],
            probe: (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult(true);
            });

        Assert.Empty(open);
        Assert.False(called);
    }
}
