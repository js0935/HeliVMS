using System.Net;
using System.Net.Sockets;
using HeliVMS.Qctek.Alarm;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>警報回報客戶端：loopback 假 CMS 實測 HELLO／保活／重連／停止。</summary>
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

    private static QctekAlarmClient CreateClient(FakeCms cms, TimeSpan keepAlive, TimeSpan reconnect)
        => new(new QctekAlarmOptions
        {
            CmsHost = "127.0.0.1",
            CmsPort = cms.Port,
            Id = 7,
            Info = "test-info",
            KeepAliveInterval = keepAlive,
            ReconnectDelay = reconnect,
        });

    [Fact]
    public async Task 連上CMS後第一則訊息是HELLO且以NEMO框傳送()
    {
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        client.Start();

        // 假 CMS 用 NemoMessage 拆框——收到完整 HELLO 即證明框格式與 payload 都對。
        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));
        Assert.Equal("ALARM_HELLO 7 =test-info", cms.Received[0]);
    }

    [Fact]
    public async Task 保活依間隔持續送出()
    {
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        client.Start();

        Assert.True(await WaitUntilAsync(
            () => cms.Received.Count(r => r.StartsWith("ALARM_KEEP_ALIVE", StringComparison.Ordinal)) >= 2,
            TimeSpan.FromSeconds(10)));
        Assert.All(
            cms.Received.Where(r => r.StartsWith("ALARM_KEEP_ALIVE", StringComparison.Ordinal)),
            r => Assert.Equal("ALARM_KEEP_ALIVE 7 =test-info", r));
    }

    [Fact]
    public async Task 斷線後會重連並重送HELLO()
    {
        await using var cms = new FakeCms();
        await using var client = CreateClient(cms, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));
        client.Start();

        Assert.True(await WaitUntilAsync(() => cms.Received.Count >= 1, TimeSpan.FromSeconds(10)));
        cms.CloseCurrent();

        Assert.True(await WaitUntilAsync(() => cms.ConnectionCount >= 2, TimeSpan.FromSeconds(10)));
        Assert.True(await WaitUntilAsync(
            () => cms.Received.Count(r => r.StartsWith("ALARM_HELLO", StringComparison.Ordinal)) >= 2,
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
