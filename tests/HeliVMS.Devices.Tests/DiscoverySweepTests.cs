using HeliVMS.Devices.Onvif;

namespace HeliVMS.Devices.Tests;

/// <summary>
/// 網段掃描的執行。這裡測的不是 ONVIF 本身（那由 <see cref="DiscoveryClientTests"/> 與
/// 假 HTTP 處理器負責），而是掃描作為「一整段」的行為：同時幾個、失敗怎麼算、
/// 有沒有重複、取消會不會停。
/// </summary>
public class DiscoverySweepTests
{
    private static DiscoveredDevice Device(string host, string? name = null) => new()
    {
        EndpointAddress = $"urn:uuid:{host}",
        XAddrs = [$"http://{host}/onvif/device_service"],
        Scopes = name is null ? [] : [$"onvif://www.onvif.org/name/{name}"],
    };

    [Fact]
    public async Task 掃描會依位址順序回報找到的設備()
    {
        var addresses = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" };
        var devices = new Dictionary<string, DiscoveredDevice>(StringComparer.Ordinal)
        {
            ["10.0.0.3"] = Device("10.0.0.3"),
            ["10.0.0.1"] = Device("10.0.0.1"),
        };

        var found = await DiscoverySweep.RunAsync(
            addresses,
            (address, _) => Task.FromResult(devices.TryGetValue(address, out var d) ? d : null),
            concurrency: 4);

        Assert.Equal(["10.0.0.1", "10.0.0.3"], found.Select(d => d.HttpXAddr!.Replace("http://", string.Empty).Split('/')[0]));
    }

    [Fact]
    public async Task 單一位址失敗不會讓整段掃描失敗()
    {
        var found = await DiscoverySweep.RunAsync(
            ["10.0.0.1", "10.0.0.2", "10.0.0.3"],
            (address, _) => address switch
            {
                "10.0.0.2" => throw new HttpRequestException("連線被拒"),
                _ => Task.FromResult<DiscoveredDevice?>(Device(address)),
            },
            concurrency: 2);

        // 現場一定會有打錯的位址、關機的機器、非 ONVIF 的設備；一個壞位址不該讓
        // 「我剛裝好的另外十台」也一起消失。
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task 同一台設備被兩個位址找到時只留一筆()
    {
        // 主機名與 IP 指向同一台（EndpointAddress 相同）：清單上重複出現會讓人以為有兩台。
        var device = Device("cam1.local");
        var found = await DiscoverySweep.RunAsync(
            ["cam1.local", "192.168.1.64"],
            (_, _) => Task.FromResult<DiscoveredDevice?>(device),
            concurrency: 2);

        Assert.Single(found);
    }

    [Fact]
    public async Task 同時探測數不會超過上限()
    {
        var current = 0;
        var peak = 0;

        await DiscoverySweep.RunAsync(
            [.. Enumerable.Range(1, 40).Select(i => $"10.0.0.{i}")],
            async (_, token) =>
            {
                var running = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, running);
                await Task.Delay(15, token);
                Interlocked.Decrement(ref current);
                return null;
            },
            concurrency: 4);

        Assert.True(peak <= 4, $"同時應該最多 4 個，實際 {peak}");
        Assert.True(peak > 1, "應該要並行，不是一個一個來");
    }

    [Fact]
    public async Task 同時探測數有硬上限()
    {
        var current = 0;
        var peak = 0;

        await DiscoverySweep.RunAsync(
            [.. Enumerable.Range(1, 80).Select(i => $"10.0.0.{i}")],
            async (_, token) =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref current));
                await Task.Delay(5, token);
                Interlocked.Decrement(ref current);
                return null;
            },
            concurrency: 1000);

        // 一次湧入上百條連線會把攝影機的連線數吃光，連帶影響監看與錄影。
        Assert.True(peak <= DiscoverySweep.MaxConcurrency, $"實際 {peak}");
    }

    [Fact]
    public async Task 每個位址完成都會回報進度()
    {
        // 用同步收集器而不是 Progress<T>：後者把回呼丟到 SynchronizationContext（測試環境下
        // 送到哪裡、什麼時候跑都不是這個測試要驗的事），斷言會變成在測排程而不是測進度。
        var progress = new SyncProgress();

        await DiscoverySweep.RunAsync(
            ["10.0.0.1", "10.0.0.2", "10.0.0.3"],
            (_, _) => Task.FromResult<DiscoveredDevice?>(null),
            concurrency: 1,
            progress: progress);

        Assert.Equal(3, progress.Items.Count);
        Assert.Equal([1, 2, 3], progress.Items.Select(r => r.Done).Order());
        Assert.All(progress.Items, r => Assert.Equal(3, r.Total));
    }

    /// <summary>同步回報的進度收集器。</summary>
    private sealed class SyncProgress : IProgress<(int Done, int Total)>
    {
        private readonly List<(int Done, int Total)> _items = [];

        public IReadOnlyList<(int Done, int Total)> Items
        {
            get
            {
                lock (_items)
                {
                    return [.. _items];
                }
            }
        }

        public void Report((int Done, int Total) value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }

    [Fact]
    public async Task 取消會讓掃描停止()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        var completed = 0;
        var firstProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sweep = DiscoverySweep.RunAsync(
            [.. Enumerable.Range(1, 200).Select(i => $"10.0.0.{i}")],
            async (_, token) =>
            {
                if (Interlocked.Increment(ref started) == 1)
                {
                    firstProbeStarted.TrySetResult();
                }

                // 探測本身掛到取消為止：沒有取消就不會結束，所以「掃描結束」只可能來自取消。
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                Interlocked.Increment(ref completed);
                return null;
            },
            concurrency: 2,
            cancellationToken: cts.Token);

        await firstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        // 沒停止就是逾時（TimeoutException），不會讓測試永遠掛著。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sweep.WaitAsync(TimeSpan.FromSeconds(10)));

        // 判準用「完成」而不是「開始」：取消之後仍可能有探測進來就立刻拋（SemaphoreSlim 快路徑），
        // 那些沒有做任何事。真正該保證的是「取消後不再有人做完一輪探測」——這裡的探測在取消前
        // 永遠不會自己完成，所以任何一個「完成」都代表取消訊號被吞掉了。
        Assert.True(completed == 0, $"取消後不該再有探測完成，實際完成 {completed} 個（開始 {started} 個）");
        Assert.True(started < 200, $"取消後不該繼續掃完，實際開始 {started} 個");
    }

    [Fact]
    public async Task 空清單不會呼叫探測()
    {
        var called = false;
        var found = await DiscoverySweep.RunAsync([], (_, _) =>
        {
            called = true;
            return Task.FromResult<DiscoveredDevice?>(null);
        });

        Assert.Empty(found);
        Assert.False(called);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
