using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 一台極簡的 RTSP 攝影機（只支援 interleaved TCP），供整合測試取代真實攝影機。
///
/// <para>
/// 為什麼需要自己寫：<c>LiveEncodeOptions</c> 產生的是
/// <c>-rtsp_transport tcp -i rtsp://…</c>，要驗證這串參數就必須有一台<b>真的</b>會回應
/// RTSP 握手的來源。ffmpeg 做不到——它的 <c>rtsp</c> muxer 沒有 listen 模式
/// （<c>-rtsp_flags listen</c> 是 demuxer 的選項），所以「用第二個 ffmpeg 假裝攝影機」
/// 這條路走不通。
/// </para>
///
/// <para>
/// 串流內容是 <b>ffmpeg 自己產生的 RTP 封包</b>：先用 <c>-f rtp</c> 把 Annex-B H.264
/// 送進一個 UDP port 錄下來，再把這些封包原封不動地搬到 RTSP 的 interleaved channel。
/// 這樣 RTP 的封裝（FU-A 分片、SSRC、marker bit、序號）完全由 ffmpeg 產生，
/// 這份測試基礎設施就不需要自己重implement 一套 H.264 over RTP——而自己實作 RTP
/// 正是最容易悄悄出錯、也最難驗證的部分。
/// </para>
///
/// <para>
/// 刻意只實作 publisher 真正會用到的那幾個方法（OPTIONS／DESCRIBE／SETUP／PLAY／
/// GET_PARAMETER／TEARDOWN）與 interleaved TCP 的 RTP 推送；任何其他方法回 200 帶
/// <c>Public</c>，足以讓 ffmpeg 繼續。多做功能只會增加這份測試基礎設施的維護成本。
/// </para>
/// </summary>
internal sealed class FakeRtspCamera : IAsyncDisposable
{
    private const int PayloadType = 96;
    private const uint ClockRate = 90_000;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<byte[]> _rtpPackets;
    private readonly int _frameRate;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly string _spropParameterSets;
    private long _packetsSent;
    private ushort _sequence;
    private uint _lastTimestamp;

    /// <summary>
    /// 以預先錄好的 RTP 封包建立攝影機。
    /// </summary>
    /// <param name="rtpPackets">完整 RTP 封包（含 12 bytes 標頭），順序即送出順序。</param>
    /// <param name="spropParameterSets">DESCRIBE 的 SDP 用的 SPS／PPS（base64,base64）。</param>
    /// <param name="frameRate">來源影格率，決定 RTP 時間戳前進速度。</param>
    public FakeRtspCamera(List<byte[]> rtpPackets, string spropParameterSets, int frameRate)
    {
        _rtpPackets = rtpPackets;
        _frameRate = Math.Max(1, frameRate);
        _spropParameterSets = spropParameterSets;

        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>攝影機的 RTSP 埠。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>已送出的 RTP 封包數；用來確認「publisher 真的有從這台攝影機取流」。</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();

            try
            {
                if (!await ReadInitialRequestAsync(stream).ConfigureAwait(false)) return;

                while (!_stop.IsCancellationRequested)
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request is null) return;

                    if (request.Method.Equals("PLAY", StringComparison.Ordinal))
                    {
                        await SendResponseAsync(stream, request, 200, "Session", $"{_session};timeout=60",
                            extra: $"RTP-Info: url=rtsp://127.0.0.1:{Port}/live/streamid=0;seq={_sequence};rtptime=0\r\n")
                            .ConfigureAwait(false);
                        await StreamAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    switch (request.Method)
                    {
                        case "OPTIONS":
                            await SendResponseAsync(stream, request, 200, "Public",
                                "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER")
                                .ConfigureAwait(false);
                            break;

                        case "DESCRIBE":
                            await SendResponseAsync(stream, request, 200, null, null,
                                    contentType: "application/sdp", body: BuildSdp())
                                .ConfigureAwait(false);
                            break;

                        case "SETUP":
                            await SendResponseAsync(stream, request, 200, "Transport",
                                "RTP/AVP/TCP;unicast;interleaved=0-1",
                                extra: $"Session: {_session};timeout=60\r\n")
                                .ConfigureAwait(false);
                            break;

                        case "TEARDOWN":
                            await SendResponseAsync(stream, request, 200).ConfigureAwait(false);
                            return;

                        default:
                            // GET_PARAMETER（keep-alive）與其他未知方法：回 200 讓 ffmpeg 繼續。
                            await SendResponseAsync(stream, request, 200, "Public", "OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER")
                                .ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // 測試結束時直接斷線是預期行為。
            }
        }
    }

    /// <summary>把錄好的 RTP 封包依序以 interleaved TCP 送出，並在停止前無限循環。</summary>
    private async Task StreamAsync(NetworkStream stream)
    {
        var ticksPerFrame = ClockRate / (uint)_frameRate;

        // ffmpeg 會在 channel 1 回送 RTCP RR/DRR。若不讀走，socket 的接收緩衝區
        // 填滿後 ffmpeg 的 send buffer 也會填滿，接著它的 write() 會阻塞——
        // 結果就是 RTSP 握手完成、ffmpeg 認得串流，卻收不到任何影格。
        // 真實 RTSP server 也會消費 RTCP，所以這裡同樣把它讀掉。
        var drainRtcp = Task.Run(() => DrainInterleavedAsync(stream));
        var senderReport = Task.Run(() => SendSenderReportsAsync(stream));

        while (!_stop.IsCancellationRequested)
        {
            foreach (var packet in _rtpPackets)
            {
                // 序號與時間戳重新起算，避免無限循環時 ffmpeg 認為封包重複而丟棄。
                var rewritten = new byte[packet.Length];
                packet.CopyTo(rewritten, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(rewritten.AsSpan(2), _sequence++);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rewritten.AsSpan(4), _lastTimestamp);

                await WriteInterleavedAsync(stream, rewritten).ConfigureAwait(false);
                _lastTimestamp += ticksPerFrame;

                // 來源是「直播」：照影格率推送，避免 ffmpeg 還沒送出 SETUP 就被
                // 整段資料灌爆 TCP 視窗。
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1000 / _frameRate), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await Task.WhenAll(drainRtcp, senderReport).ConfigureAwait(false);
                    return;
                }
            }
        }

        await Task.WhenAll(drainRtcp, senderReport).ConfigureAwait(false);
    }

    private async Task WriteInterleavedAsync(NetworkStream stream, byte[] packet)
    {
        // interleaved framing：'$' + channel + 2 byte 長度 + RTP
        var frame = new byte[4 + packet.Length];
        frame[0] = 0x24;
        frame[1] = 0x00;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)packet.Length);
        packet.CopyTo(frame, 4);

        // RTP 與 RTCP 兩條 channel 共用同一個 socket。NetworkStream 不允許並行寫入，
        // 否則兩邊的位元組會交錯，ffmpeg 就再也組不出完整的 RTP 封包。
        await _writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Interlocked.Increment(ref _packetsSent);
    }

    /// <summary>
    /// 定期送 RTCP Sender Report（SR）。ffmpeg 的 RTSP layer 需要 SR 才能把 RTP
    /// timestamp 對到 wallclock；沒有 SR 它會一直停在 <c>Reinit context</c> 之後、
    /// 不輸出任何影格。真實 RTSP server（ffmpeg 自己的 muxer、VLC、Darwin）都會送。
    /// </summary>
    private async Task SendSenderReportsAsync(NetworkStream stream)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1000), _stop.Token).ConfigureAwait(false);

                // RTCP SR：header(4) + SSRC(4) + NTP(8) + RTP ts(4) + 封包數(8) + octets(4)
                // = 28 bytes。length 欄位填「(bytes / 4) - 1」，含 header 本身。
                var rtcp = new byte[28];
                rtcp[0] = 0x80;
                rtcp[1] = 200;                                   // PT=200 (SR)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(rtcp.AsSpan(2), (28 / 4) - 1);

                var ntp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(4), Ssrc);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(8), unchecked((uint)(ntp / 1000 + 2_208_988_800L)));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(12), unchecked((uint)(ntp % 1000) * (uint)0x10000000 / 1000u));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(16), _lastTimestamp);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(20), (uint)_packetsSent);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtcp.AsSpan(24), 0);

                var frame = new byte[4 + rtcp.Length];
                frame[0] = 0x24;                                  // '$'
                frame[1] = 0x01;                                  // RTCP channel
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)rtcp.Length);
                rtcp.CopyTo(frame, 4);

                await _writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 讀掉 client 送來的 interleaved 影格（RTCP）。內容對本測試不重要，重點是
    /// 必須把它從 socket 移走，否則 ffmpeg 會卡在 <c>write()</c> 上再也送不出影格。
    /// </summary>
    private async Task DrainInterleavedAsync(NetworkStream stream)
    {
        var header = new byte[4];

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (await stream.ReadAsync(header, _stop.Token).ConfigureAwait(false) == 0) return;
                if (header[0] != 0x24) continue;   // 非 '$' framing（例如 RTSP 回應尾巴），忽略

                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                if (await stream.ReadAsync(new byte[length], _stop.Token).ConfigureAwait(false) == 0) return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private string BuildSdp()
    {
        var sdp = new StringBuilder()
            .Append("v=0\r\n")
            .Append("o=- 0 0 IN IP4 127.0.0.1\r\n")
            .Append("s=HeliVMS Fake Camera\r\n")
            .Append("c=IN IP4 0.0.0.0\r\n")
            .Append("t=0 0\r\n")
            .Append("a=control:*\r\n")
            .Append("m=video 0 RTP/AVP 96\r\n")
            .Append($"a=rtpmap:96 H264/{ClockRate}\r\n")
            .Append($"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={_spropParameterSets}\r\n")
            .Append("a=control:streamid=0\r\n")
            .ToString();

        return sdp;
    }

    private sealed record RtspRequest(string Method, string Uri, string CSeq);

    /// <summary>讀一個 RTSP 請求（以空行結尾）；連線關閉時回 <c>null</c>。</summary>
    private static async Task<RtspRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        var single = new byte[1];

        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(single, CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return null;

            request.Append((char)single[0]);
            if (request.Length > 16 * 1024) return null;   // 防呆：別讓壞掉的 client 撐爆記憶體
        }

        var lines = request.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var cseq = lines
            .FirstOrDefault(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1].Trim() ?? "0";

        return new RtspRequest(first[0], first.Length > 1 ? first[1] : string.Empty, cseq);
    }

    private static async Task<bool> ReadInitialRequestAsync(NetworkStream stream)
    {
        // 第一個請求（ffmpeg 一律先送 OPTIONS）先讀掉再回應，讓 HandleAsync 的迴圈單純。
        var probe = await ReadRequestAsync(stream).ConfigureAwait(false);
        if (probe is null) return false;

        await SendResponseAsync(stream, probe, 200, "Public",
            "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER").ConfigureAwait(false);
        return true;
    }

    private static async Task SendResponseAsync(
        NetworkStream stream,
        RtspRequest request,
        int status,
        string? header = null,
        string? headerValue = null,
        string? contentType = null,
        string? body = null,
        string? extra = null)
    {
        var payload = body is null ? string.Empty : body;
        var head = new StringBuilder()
            .Append($"RTSP/1.0 {status} {ReasonPhrase(status)}\r\n")
            .Append($"CSeq: {request.CSeq}\r\n");

        if (header is not null) head.Append($"{header}: {headerValue}\r\n");
        if (extra is not null) head.Append(extra);
        if (contentType is not null) head.Append($"Content-Type: {contentType}\r\n");
        head.Append($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head + payload), CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        _ => "Error",
    };

    private const uint Ssrc = 0xDEADBEEF;

    /// <summary>
    /// 用 ffmpeg 把 Annex-B H.264 編成 RTP 封包並錄下來。
    /// </summary>
    /// <remarks>
    /// 讓 ffmpeg 自己產生 RTP 是刻意的：自己實作 H.264 over RTP（FU-A 分片、
    /// marker bit、SSRC 一致性）非常容易出錯，而且出錯時症狀是「ffmpeg 認得串流
    /// 卻永遠不吐影格」，極難診斷。改由 ffmpeg 產生後，這份攝影機就只負責 RTSP
    /// 握手與 interleaved framing——真正需要自己驗證的部分。
    /// </remarks>
    public static async Task<(List<byte[]> RtpPackets, string SpropParameterSets)> RecordRtpAsync(
        byte[] annexB,
        double seconds,
        Action<int>? onRecorderBound = null)
    {
        if (annexB is null || annexB.Length == 0) throw new ArgumentException("需要 Annex-B 串流。", nameof(annexB));

        var dir = Directory.CreateTempSubdirectory("helivms-camgen");
        var h264Path = Path.Combine(dir.FullName, "camera.h264");
        await File.WriteAllBytesAsync(h264Path, annexB);

        var (sps, pps) = ExtractParameterSets(annexB);
        var sprop = $"{Convert.ToBase64String(sps)},{Convert.ToBase64String(pps)}";

        /*
         * 先把接收 socket 綁在 loopback 的 port 0 讓 OS 挑一個 port，並在整個錄製
         * 期間持有它，再把實際 port 交給 ffmpeg。
         *
         * 舊順序是「綁一個 probe socket 讀出 port → 關掉 → 啟動 ffmpeg → 才綁接收
         * socket」。probe 一關，那個 port 就變成空閒：xunit 會平行執行測試類別，
         * 另一個測試的 RtpIngest 可能正好拿到同一個 port，於是 ffmpeg 的封包被別人的
         * ingest 收走。這會讓 LiveStreamServiceTests.第一個RTP封包到達前不會回應
         * 在自己送封包之前就完成，且只在 CI 上間歇發生——那正是難以重現的那種失敗。
         * 持有 socket 就沒有這個空窗。
         */
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        // 測試可以在此時驗證這個 port 已被獨占（見 PublisherIntegrationTests）。
        onRecorderBound?.Invoke(udpPort);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-re",
            "-stream_loop", "-1",
            "-i", h264Path,
            "-an", "-c:v", "copy",
            "-f", "rtp", "-payload_type", PayloadType.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"rtp://127.0.0.1:{udpPort}",
        };

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var sender = Process.Start(psi)!;
        var senderErr = sender.StandardError.ReadToEndAsync();

        var packets = new List<byte[]>();
        udp.Client.ReceiveTimeout = 500;

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var endpoint = new IPEndPoint(IPAddress.Any, 0);

        while (DateTime.UtcNow < deadline && !sender.HasExited)
        {
            try
            {
                var datagram = udp.Receive(ref endpoint);
                if (datagram.Length > 12) packets.Add(datagram);
            }
            catch (SocketException)
            {
                // 只是這 500ms 沒收到資料，繼續等到 deadline。
            }
        }

        try { if (!sender.HasExited) sender.Kill(entireProcessTree: true); } catch { /* 錄完就結束 */ }
        try { await senderErr; } catch { /* 忽略 */ }
        Directory.Delete(dir.FullName, recursive: true);

        return (packets, sprop);
    }

    /// <summary>從 Annex-B 取出 SPS（type 7）與 PPS（type 8）。</summary>
    private static (byte[] Sps, byte[] Pps) ExtractParameterSets(byte[] annexB)
    {
        byte[] sps = [], pps = [];

        for (var i = 0; i + 3 <= annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0 || annexB[i + 2] != 1) continue;

            var start = i + 3;
            var type = start < annexB.Length ? annexB[start] & 0x1F : 0;

            var end = start + 1;
            while (end + 3 <= annexB.Length)
            {
                if (annexB[end] == 0 && annexB[end + 1] == 0 && annexB[end + 2] == 1) break;
                end++;
            }

            if (type == 7 && sps.Length == 0) sps = annexB[start..end];
            if (type == 8 && pps.Length == 0) pps = annexB[start..end];

            i = start - 1;
        }

        return (sps, pps);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }
}