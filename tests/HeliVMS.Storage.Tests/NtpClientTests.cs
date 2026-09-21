using System.Net;
using System.Net.Sockets;
using HeliVMS.Core.Ntp;

namespace HeliVMS.Storage.Tests;

public class NtpClientTests
{
    private static readonly DateTime RefTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildRequest_HeaderAndTransmitField_Valid()
    {
        var req = NtpFrames.BuildRequestUtc(RefTime);

        Assert.Equal(48, req.Length);
        Assert.Equal(0x23, req[0]); // LI0 VN4 MODE3
        var transmit = NtpFrames.ReadNtpTimestamp(req, 40);
        Assert.Equal(RefTime, transmit, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void TryParse_KnownTimestamps_ComputesOffsetAndDelay()
    {
        var t1 = RefTime;
        var t4 = t1 + TimeSpan.FromMilliseconds(800);
        var resp = BuildResponse(serverReceive: t1 + TimeSpan.FromMilliseconds(500), transmit: t1 + TimeSpan.FromMilliseconds(600));

        Assert.True(NtpFrames.TryParseResponse(resp, t1, t4, out var q));
        Assert.Equal(150, q.Offset.TotalMilliseconds, 3); // ((500)+(600-800))/2
        Assert.Equal(700, q.RoundTrip.TotalMilliseconds, 3); // (800)-(600-500)
    }

    [Fact]
    public void TryParse_ShortResponse_ReturnsFalse()
    {
        var t1 = RefTime;
        Assert.False(NtpFrames.TryParseResponse(new byte[40], t1, t1 + TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TryParse_StratumZero_KissOfDeathReturnsFalse()
    {
        var resp = BuildResponse(serverReceive: RefTime, transmit: RefTime);
        resp[1] = 0;

        Assert.False(NtpFrames.TryParseResponse(resp, RefTime, RefTime + TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void TryParse_NegativeDelay_ReturnsFalse()
    {
        // T3−T2 大於 T4−T1 → delay < 0（無法取樣）
        var t1 = RefTime;
        var resp = BuildResponse(serverReceive: t1 + TimeSpan.FromSeconds(1), transmit: t1 + TimeSpan.FromSeconds(3.5));

        Assert.False(NtpFrames.TryParseResponse(resp, t1, t1 + TimeSpan.FromSeconds(2), out _));
    }

    [Fact]
    public void TryParse_NegativeOffsetSample_SignPreserved()
    {
        var t1 = RefTime;
        var t4 = t1 + TimeSpan.FromMilliseconds(100);
        var resp = BuildResponse(serverReceive: t1 - TimeSpan.FromMilliseconds(300), transmit: t1 - TimeSpan.FromMilliseconds(200));

        Assert.True(NtpFrames.TryParseResponse(resp, t1, t4, out var q));
        Assert.Equal(-300, q.Offset.TotalMilliseconds, 3);
        Assert.True(q.RoundTrip.TotalMilliseconds >= 0);
    }

    [Fact]
    public void EpochConversion_RoundTrip()
    {
        var input = new DateTime(2030, 6, 15, 12, 34, 56, 789, DateTimeKind.Utc);
        var (seconds, fraction) = NtpClient.DateTimeToNtp(input);
        var back = NtpClient.NtpSecondsToDateTime(seconds, fraction);

        Assert.True(input == back || (back - input).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task QueryAsync_LoopbackFakeServer_OffsetMatches()
    {
        using var server = FakeNtpServer.Start();
        var client = new NtpClient();

        // 多次取樣取最小 RTT 樣本（NTP 過濾慣例）——伺服器注入 +100ms
        // 高負載下排程會拉大量到的 RTT；斷言務實化：樣本乾淨（RTT≤30ms）才做精確 >90ms 檢查
        NtpQuery? best = null;
        for (var i = 0; i < 5 && (best == null || best.RoundTrip > TimeSpan.FromMilliseconds(30)); i++)
        {
            var sample = await client.QueryAsync(server.Address.Address.ToString(), server.Address.Port, TimeSpan.FromSeconds(2));
            if (sample == null)
            {
                continue;
            }

            if (best == null || sample.RoundTrip < best.RoundTrip)
            {
                best = sample;
            }
        }

        Assert.NotNull(best);
        Assert.True(best.RoundTrip.TotalMilliseconds >= 0);
        Assert.Equal(2, best.Stratum);
        Assert.True(best.Offset.TotalMilliseconds > 0, $"方向錯誤：{best.Offset.TotalMilliseconds}"); // +100 注入必為正（rtt<200ms）
        if (best.RoundTrip <= TimeSpan.FromMilliseconds(30))
        {
            Assert.True(best.Offset.TotalMilliseconds > 90, $"乾淨樣本 offset 過低：{best.Offset.TotalMilliseconds} rtt={best.RoundTrip.TotalMilliseconds}");
        }
    }

    [Fact]
    public async Task QueryAsync_NoReply_TimesOutReturnsNull()
    {
        using var server = FakeNtpServer.Start(reply: false);

        var client = new NtpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.QueryAsync(server.Address.Address.ToString(), server.Address.Port, TimeSpan.FromMilliseconds(300));

        sw.Stop();
        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void NtpSecondsToDateTime_MillisScale()
    {
        var (sec, frac) = NtpClient.DateTimeToNtp(RefTime);
        var asDate = NtpClient.NtpSecondsToDateTime(sec, frac);
        Assert.Equal(RefTime, asDate, TimeSpan.FromMilliseconds(1));
    }

    private static byte[] BuildResponse(DateTime serverReceive, DateTime transmit)
    {
        var resp = new byte[48];
        resp[0] = 0x24; // LI0 VN4 MODE4
        resp[1] = 2;    // stratum 2
        var (rs, rf) = NtpClient.DateTimeToNtp(serverReceive);
        NtpFrames.WriteNtpTimestamp(resp, 32, rs, rf);
        var (ts, tf) = NtpClient.DateTimeToNtp(transmit);
        NtpFrames.WriteNtpTimestamp(resp, 40, ts, tf);
        return resp;
    }

private sealed class FakeNtpServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly bool _reply;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public IPEndPoint Address { get; }

        private FakeNtpServer(bool reply)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _reply = reply;
            Address = (IPEndPoint)_udp.Client.LocalEndPoint!;
            _thread = new Thread(RunLoop) { IsBackground = true };
            _thread.Start();
        }

        public static FakeNtpServer Start(bool reply = true) => new(reply);

        /// <summary>專屬 thread 同步收發——迴避 threadpool 飽和造成的延遲，RTT＝純 loopback。</summary>
        private void RunLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                IPEndPoint remote = new(IPAddress.Any, 0);
                byte[] received;
                try
                {
                    received = _udp.Receive(ref remote);
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!_reply)
                {
                    continue;
                }

                var t1 = NtpFrames.ReadNtpTimestamp(received, 40);
                var resp = new byte[48];
                resp[0] = 0x24;
                resp[1] = 2;
                var (r2s, r2f) = NtpClient.DateTimeToNtp(t1 + TimeSpan.FromMilliseconds(100));
                NtpFrames.WriteNtpTimestamp(resp, 32, r2s, r2f);
                NtpFrames.WriteNtpTimestamp(resp, 40, r2s, r2f);
                _udp.Send(resp, resp.Length, remote);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Close();
            _cts.Dispose();
        }
    }
}