using System.Net;
using System.Text;
using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// ONVIF 的 HttpClient 共用（正式路徑）。
///
/// <para>
/// Web API 的每條 PTZ 命令、乾接點輪詢、巡邏步進都會 <c>new OnvifDeviceService</c>；
/// 每實例一個 HttpClient 是「每次請求新建客戶端」的教科書反模式——高頻操作下
/// 連線在 TIME_WAIT 裡堆積，症狀是操作一段時間後 ONVIF 整批連不上而日誌乾淨。
/// 正式路徑（未注入 handler）必須共用；注入 handler 的測試路徑維持實例專屬。
/// </para>
/// <para>
/// 計數斷言刻意用「每支測試專屬的逾時值」：逾時是共用客戶端的鍵，專屬逾時保證
/// 第一個實例必得新建、第二個實例必得沿用，不依賴測試執行順序與共用字典的殘留。
/// 「Dispose 不得殺死共用客戶端」用回環 HTTP 伺服器做真行為驗證：第一個實例
/// 收掉之後，第二個實例的請求必須照常成功（突變：Dispose 連共用客戶端一起
/// Dispose → 第二台整批 ObjectDisposedException，紅）。
/// </para>
/// </summary>
[Collection(OnvifPtzCacheCollection.Name)]
public sealed class OnvifHttpClientSharingTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _listenerPrefix;

    public OnvifHttpClientSharingTests()
    {
        _listener = new HttpListener();
        _listenerPrefix = $"http://127.0.0.1:{FreePort()}/onvif/";
        _listener.Prefixes.Add(_listenerPrefix);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    /// <summary>挑一個空閒埠：綁定成功即持有（見 AGENTS 的埠選擇 landmine）。</summary>
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var payload = body.Contains("GetSystemDateAndTime", StringComparison.Ordinal)
                    ? OnvifTestHttp.SystemDateAndTime(DateTimeOffset.UtcNow)
                    : OnvifTestHttp.DeviceInfoXml;
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 客戶端在突變下會丟 ObjectDisposedException：回應端照樣收尾。
            }
        }
    }

    private OnvifDeviceService Create(TimeSpan timeout)
        => new(
            _listenerPrefix + "device_service",
            null,
            null,
            HeliVMS.Devices.Onvif.OnvifClientOptions.Default with { Timeout = timeout });

    [Fact]
    public async Task 正式路徑的多個實例共用同一個HttpClient()
    {
        var timeout = TimeSpan.FromSeconds(7.31);
        var before = OnvifDeviceService.HttpClientCreatedCount;

        using (var a = Create(timeout))
        {
            Assert.Equal(before + 1, OnvifDeviceService.HttpClientCreatedCount);
            var info = await a.GetInfoAsync();
            Assert.Equal("HIKVISION", info.Manufacturer);
        }

        using (var b = Create(timeout))
        {
            // 第二個實例（同逾時）沿用同一個客戶端：不再新建。
            Assert.Equal(before + 1, OnvifDeviceService.HttpClientCreatedCount);
            var info = await b.GetInfoAsync();
            Assert.Equal("HIKVISION", info.Manufacturer);
        }
    }

    /// <summary>
    /// 第一個實例 Dispose 不得關掉共用客戶端：否則第二個實例的每條請求都會
    /// 撞 ObjectDisposedException（症狀：第一台 PTZ 操作完，第二台整批連不上）。
    /// </summary>
    [Fact]
    public async Task 共用客戶端在實例釋放後仍然可用()
    {
        var timeout = TimeSpan.FromSeconds(7.32);
        var first = Create(timeout);
        var info = await first.GetInfoAsync();
        Assert.Equal("HIKVISION", info.Manufacturer);

        first.Dispose();

        using var second = Create(timeout);
        var info2 = await second.GetInfoAsync();
        Assert.Equal("HIKVISION", info2.Manufacturer);
    }

    [Fact]
    public async Task 逾時不同的實例各自持有客戶端()
    {
        var before = OnvifDeviceService.HttpClientCreatedCount;

        using var fast = Create(TimeSpan.FromSeconds(3.17));
        await fast.GetInfoAsync();

        // 逾時是客戶端的屬性：不同的逾時需求不能共用同一個客戶端。
        Assert.Equal(before + 1, OnvifDeviceService.HttpClientCreatedCount);
    }
}
