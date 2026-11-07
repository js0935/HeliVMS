using System.Net;
using System.Net.Sockets;
using HeliVMS.Qctek.Alarm;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>警報回報客戶端：loopback 假 CMS 實測 HELLO／週期保活／事件／重連／停止。</summary>
public class QctekAlarmClientTests
{
    /// <summary>假 CMS：接受連線、以 NEMO 框解析訊息、可主動斷線（模擬 CMS 重啟）。</summary>
    private sealed class FakeCms : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly List<string> _received = [];
        private readonly object _gate = new();
        private TcpClient? _current;
        private int _connections;

        public FakeCms()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public int Port { get; }

        public int ConnectionCount
        {
            get { lock (_gate) { return _connections; } }
        }

        public IReadOnlyList<string> Received
        {
            get { lock (_gate) { return [.. _received]; } }
        }

        public void CloseCurrent()
        {
            lock (_gate)
            {
                _current?.Close();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                lock (_gate)
                {
                    _current = client;
                    _connections++;
                }

                _ = Task.Run(() => ReadLoopAsync(client, cancellationToken), CancellationToken.None);
            }
        }

        private async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var pending = new List<byte>();
                var buffer = new byte[512];
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count;
                    try
                    {
                        count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        return;
                    }

                    if (count == 0)
                    {
                        return;
                    }

                    pending.AddRange(buffer.AsSpan(0, count));
                    while (NemoMessage.TryDecodeText(pending.ToArray(), out var message, out var used))
                    {
                        lock (_gate)
                        {
                            _received.Add(message);
                        }

                        pending.RemoveRange(0, used);
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static QctekAlarmClient CreateClient(FakeCms cms, TimeSpan hello, TimeSpan reconnect)
        => new(new QctekAlarmOptions
        {
            CmsHost = "127.0.0.1",
            CmsPort = cms.Port,
            Info = "test-info",
            HelloInterval = hello,
            ReconnectDelay = reconnect,
        });

    [Fact]
    public async Task 連上CMS後第一則訊息是HELLO且以NEMO框傳送()
    {
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        client.Start();

        // 假 CMS 用 NemoMessage 拆框——收到完整 HELLO 即證明框格式與 payload 都對。
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));
        Assert.Equal("ALARM_HELLO 1 =test-info", cms.Received[0]);
    }

    [Fact]
    public async Task 保活是週期重送HELLO而不是KEEP_ALIVE()
    {
        // QCV8001 的發送端沒有 ALARM_KEEP_ALIVE 字串（它只出現在 CMS 的接收分派器），
        // 保活靠連線主控迴圈週期重送 HELLO——送錯命令會讓 CMS 把它當未知訊息。
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        client.Start();

        Assert.True(await WaitUntilAsync(
            () => cms.Received.Count(r => r.StartsWith("ALARM_HELLO", StringComparison.Ordinal)) >= 3,
            TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(cms.Received, r => r.Contains("KEEP_ALIVE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 事件以ALARM_MSG送出且欄位順序正確()
    {
        // 欄位順序由發送端（QC_Alarm 0x18150 的 sprintf_s 槽序）與 CMS 解析器
        // （QC_RM_Alarm 0x4a14 的讀回段）雙向校準：id 相機 年 月 日 時 分 秒
        // 之後才是 $Name/$Describe/類型/$Pri/$Live/=%s。
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        client.Start();
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));

        client.Report(new QctekAlarmEvent(
            CameraNumber: 7,
            LocalTime: new DateTime(2026, 11, 8, 14, 5, 9),
            Name: "大門",
            Describe: "motion 12%",
            Priority: 0,
            EventType: "motion"));

        Assert.True(await WaitUntilAsync(
            () => cms.Received.Any(r => r.StartsWith("ALARM_MSG", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)));
        var msg = cms.Received.First(r => r.StartsWith("ALARM_MSG", StringComparison.Ordinal));
        Assert.Equal(
            "ALARM_MSG 1 7 2026 11 8 14 5 9 $Name:大門 Describe:motion 12% motion $Pri:0 $Live:-1 =test-info",
            msg);
    }

    [Fact]
    public async Task 事件在斷線期間排隊且重連後補送()
    {
        // 現場情境：CMS 重啟的幾秒內發生的偵測不能無聲消失——事件進佇列，
        // 重連後補送（送出途中斷線的那一筆才會遺失）。
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        client.Start();
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));

        cms.CloseCurrent();
        await Task.Delay(150); // 讓用戶端偵測到斷線

        client.Report(new QctekAlarmEvent(3, new DateTime(2026, 1, 2, 3, 4, 5), "車道", "offline"));

        Assert.True(await WaitUntilAsync(
            () => cms.Received.Any(r => r.StartsWith("ALARM_MSG 2 3 ", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task 重連後HELLO使用新的連線識別()
    {
        // QCV8001 的 %d 是「每條連線的計數」——重連是新連線、要拿新識別，
        // 否則 CMS 會把它綁回已死掉的舊連線格。
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        client.Start();
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));
        cms.CloseCurrent();

        Assert.True(await WaitUntilAsync(
            () => cms.Received.Any(r => r.StartsWith("ALARM_HELLO 2 ", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task 停止後不再送出訊息()
    {
        await using var cms = new FakeCms();
        var client = CreateClient(cms, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        client.Start();
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));

        await client.StopAsync();
        await Task.Delay(300); // 讓停止前已在管線中的訊息落地
        var countAtStop = cms.Received.Count;
        await Task.Delay(700);
        Assert.Equal(countAtStop, cms.Received.Count);

        await client.DisposeAsync();
    }
}
