using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 批次加入的解析：把探索到的設備變成一批可寫入的攝影機。
/// 現場要的是「一次把十幾台加進來」，所以這裡測的重點是「一台壞掉會不會拖垮整批」、
/// 「挑出來的主／子碼流對不對」與「取消按下去真的會停」。
/// </summary>
public class OnvifEnrollmentPlannerTests
{
    private const string MainUri = "rtsp://192.168.1.64:554/Streaming/Channels/101";
    private const string SubUri = "rtsp://192.168.1.64:554/Streaming/Channels/102";

    private static DiscoveredDevice Device(string host, string? name = null) => new()
    {
        EndpointAddress = $"urn:uuid:{host}",
        XAddrs = [$"http://{host}/onvif/device_service"],
        Scopes = name is null ? [] : [$"onvif://www.onvif.org/name/{name}"],
    };

    /// <summary>一台正常攝影機的 SOAP 回應（設備資訊、Media 服務、兩個 profile、兩條串流位址）。</summary>
    private static RecordingHandler Handler(string? mainOverride = null, string? subOverride = null)
        => new((_, body) => body switch
        {
            var b when b.Contains("GetDeviceInformation", StringComparison.Ordinal) => OnvifTestHttp.DeviceInfoXml,
            var b when b.Contains("GetCapabilities", StringComparison.Ordinal) => OnvifTestHttp.Capabilities(
                ("Media", "http://192.168.1.64/onvif/Media")),
            var b when b.Contains("GetServices", StringComparison.Ordinal) => OnvifTestHttp.Services(
                ("http://www.onvif.org/ver10/media/wsdl", "http://192.168.1.64/onvif/Media")),
            var b when b.Contains("GetProfiles", StringComparison.Ordinal) => OnvifTestHttp.Profiles(
                ("p_main", "MainProfile", 1920, 1080, "H264", "VideoSource1"),
                ("p_sub", "SubProfile", 640, 360, "H264", "VideoSource1")),
            var b when b.Contains("p_sub", StringComparison.Ordinal) => OnvifTestHttp.StreamUri(subOverride ?? SubUri),
            _ => OnvifTestHttp.StreamUri(mainOverride ?? MainUri),
        });

    private static OnvifEnrollmentPlanner Planner(RecordingHandler handler)
        => new((xAddr, user, pass) => new OnvifDeviceService(xAddr, user, pass, handler));

    [Fact]
    public async Task 批次解析會挑出主碼流與子碼流()
    {
        var results = await Planner(Handler()).RunAsync([Device("192.168.1.64")], "admin", "secret");

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        var candidate = result.Candidate!;
        Assert.Equal(MainUri, candidate.MainStreamUrl);
        Assert.Equal(SubUri, candidate.SubStreamUrl);
        // 呼叫端要據此建立 devices 記錄（RTSP 與 PTZ 共用同一份憑證）。
        Assert.Equal("192.168.1.64", candidate.DeviceIp);
        Assert.Equal(80, candidate.DevicePort);
        Assert.Equal("admin", candidate.Username);
        Assert.Equal("secret", candidate.Password);
        // 名稱優先用設備自報的（操作員才認得出是哪一台）。
        Assert.Equal("HIKVISION DS-2CD2T47G2", candidate.SuggestedName);
    }

    [Fact]
    public async Task 沒有可用profile的設備標成失敗而不是丟例外()
    {
        var handler = new RecordingHandler((_, body) => body switch
        {
            var b when b.Contains("GetDeviceInformation", StringComparison.Ordinal) => OnvifTestHttp.DeviceInfoXml,
            var b when b.Contains("GetCapabilities", StringComparison.Ordinal) => OnvifTestHttp.Capabilities(
                ("Media", "http://192.168.1.64/onvif/Media")),
            _ => OnvifTestHttp.Profiles(),
        });

        var results = await Planner(handler).RunAsync([Device("192.168.1.64")]);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Null(result.Candidate);
        // 沒有可用碼流不是例外，是「這台不加」；Error 留空讓畫面用在地語系說原因。
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task 一台失敗不會影響其餘設備()
    {
        var good = Handler();
        var planner = new OnvifEnrollmentPlanner((xAddr, user, pass) =>
            xAddr.Contains("192.168.1.99", StringComparison.Ordinal)
                ? new OnvifDeviceService(xAddr, user, pass, new ThrowingHandler())
                : new OnvifDeviceService(xAddr, user, pass, good));

        var results = await planner.RunAsync(
            [Device("192.168.1.64"), Device("192.168.1.99"), Device("192.168.1.65")]);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.NotNull(results[1].Error);
        Assert.True(results[2].Success, "一台連不上不該讓後面那台也失敗");
    }

    [Fact]
    public async Task 子碼流與主碼流相同時不當成子碼流()
    {
        // 有些設備的 profile 指向同一條串流；留著會讓多分割監看重複拉同一條 RTSP。
        var results = await Planner(Handler(subOverride: MainUri)).RunAsync([Device("192.168.1.64")]);

        Assert.Null(results[0].Candidate!.SubStreamUrl);
    }

    [Fact]
    public async Task 每台設備完成都會回報進度()
    {
        var progress = new SyncProgress();

        await Planner(Handler()).RunAsync(
            [Device("192.168.1.64"), Device("192.168.1.65")],
            progress: progress);

        Assert.Equal(2, progress.Items.Count);
        Assert.Equal([1, 2], progress.Items.Select(i => i.Done).Order());
        Assert.All(progress.Items, i => Assert.Equal(2, i.Total));
        Assert.All(progress.Items, i => Assert.NotEmpty(i.Address));
    }

    [Fact]
    public async Task 取消會停止批次()
    {
        using var cts = new CancellationTokenSource();
        var resolved = 0;
        var handler = Handler();
        var planner = new OnvifEnrollmentPlanner((xAddr, user, pass) =>
        {
            if (Interlocked.Increment(ref resolved) >= 2)
            {
                cts.Cancel();
            }

            return new OnvifDeviceService(xAddr, user, pass, handler);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await planner.RunAsync(
                [.. Enumerable.Range(1, 20).Select(i => Device($"10.0.0.{i}"))],
                cancellationToken: cts.Token));

        Assert.True(resolved < 20, $"取消後不該繼續解析，實際 {resolved} 台");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("連線被拒");
    }

    /// <summary>同步回報的進度收集器（Progress&lt;T&gt; 會看 SynchronizationContext，測試不需要那層）。</summary>
    private sealed class SyncProgress : IProgress<(int Done, int Total, string Address)>
    {
        private readonly List<(int Done, int Total, string Address)> _items = [];

        public IReadOnlyList<(int Done, int Total, string Address)> Items
        {
            get
            {
                lock (_items)
                {
                    return [.. _items];
                }
            }
        }

        public void Report((int Done, int Total, string Address) value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }
}
