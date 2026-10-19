using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 測試用的極簡 RTSP 伺服器：把一段 H.264 Annex-B 位元流以 RTP over TCP（interleaved）送給
/// 連上來的 ffmpeg/ffprobe 客戶端。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要自己寫：<see cref="RtspClient"/> 的驗收要一條真的 RTSP 來源，但環境裡沒有
/// mediamtx 之類的伺服器，而 ffmpeg 自己不能當 RTSP 伺服器供人拉流——<c>-rtsp_flags listen</c>
/// 只有 demuxer 有（讓 ffmpeg 當接收端），muxer 沒有（見 <c>ffmpeg -h muxer=rtsp</c>）。
/// 手寫的伺服器只服務這一條測試，卻能真的走完 OPTIONS→DESCRIBE→SETUP→PLAY 與 RTP 解包，
/// 驗到「參數對不對」以外的整條解碼鏈。
/// </para>
/// <para>
/// 刻意只做 TCP interleaved（不開任何 UDP 埠）：AGENTS.md 的 UDP 埠地雷在這裡直接不存在，
/// 不必猜埠、也不必擔心別的測試類別搶到同一個埠。監聽 socket 在 <see cref="Start"/> 前就綁好
/// 並一路持有（<see cref="TcpListener"/> 綁 0 埠後讀回真實埠號），不是先探測再放掉。
/// </para>
/// <para>
/// 每個連線各自起一個 media pump：ffprobe 與 ffmpeg 會先後各連一次（探測、解碼），
/// 兩者都必須被服務到。pump 會一直重播同一段 GOP 直到連線結束，因為真實攝影機不會送一秒就停；
/// 送完就停會讓 ffprobe 卡在等資料直到 rw_timeout，測試慢上一個數量級。
/// </para>
/// </remarks>
internal sealed class RtspLoopbackServer : IDisposable
{
    private const int Mtu = 1200;
    private const int PayloadType = 96;
    private static readonly uint Ssrc = 0x1234_5678;
    private const int ClockRate = 90_000;
    private const int Fps = 10;

    /// <summary>
    /// 送幀間隔。刻意遠快於即時（時間戳仍按 10fps 遞增）：ffprobe 的 analyzeduration 是 5 秒的
    /// <em>串流時間</em>，照即時送就要等 5 秒牆上時間才分析完，測試得平白多花好幾秒。
    /// </summary>
    private const int PacingMs = 5;

    private readonly TcpListener _listener;
    private readonly IReadOnlyList<IReadOnlyList<byte[]>> _frames;
    private readonly byte[] _sps;
    private readonly byte[] _pps;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly object _clientsGate = new();

    public RtspLoopbackServer(byte[] annexB)
    {
        var nals = SplitNals(annexB);
        _sps = nals.FirstOrDefault(n => (n[0] & 0x1F) == 7) ?? [];
        _pps = nals.FirstOrDefault(n => (n[0] & 0x1F) == 8) ?? [];
        if (_sps.Length == 0 || _pps.Length == 0)
        {
            throw new ArgumentException("位元流沒有 SPS/PPS，無法提供可解碼的 SDP。", nameof(annexB));
        }

        _frames = GroupFrames(nals);

        // 綁 0 埠並持有這個 socket：先開一個探測 socket 找埠再放掉會讓別的測試搶走它。
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string Url => $"rtsp://127.0.0.1:{Port}/live";

    /// <summary>收到的請求方法（診斷用：測試失敗時可以看出卡在哪個階段）。</summary>
    public List<string> Requests { get; } = [];

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 已經停了就沒事了。
        }

        lock (_clientsGate)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                    // 同上。
                }
            }

            _clients.Clear();
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            lock (_clientsGate)
            {
                _clients.Add(client);
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        var stream = connection.GetStream();
        var writeGate = new SemaphoreSlim(1, 1);
        var buffer = new List<byte>(8192);
        var sessionId = "00000001";
        var cseq = 0;
        var mediaStarted = false;
        var pump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunk = new byte[8192];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk, _cts.Token);
                if (read == 0)
                {
                    return;
                }

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());

                // 客戶端可能先送 interleaved 的 RTCP（$ + channel + 長度），先跳過。
                while (buffer.Count > 0 && buffer[0] == 0x24)
                {
                    if (buffer.Count < 4)
                    {
                        break;
                    }

                    var length = (buffer[2] << 8) | buffer[3];
                    if (buffer.Count < 4 + length)
                    {
                        break;
                    }

                    buffer.RemoveRange(0, 4 + length);
                }

                while (TryTakeRequest(buffer, out var request))
                {
                    var lines = request.Split("\r\n");
                    var method = lines[0].Split(' ')[0];
                    foreach (var line in lines.Skip(1))
                    {
                        if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = int.TryParse(line[5..].Trim(), out cseq);
                        }
                    }

                    Requests.Add(method);

                    var response = method switch
                    {
                        "DESCRIBE" => WithBody(
                            cseq,
                            "application/sdp",
                            Sdp()),
                        "SETUP" => $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: {sessionId}\r\n"
                            + "Transport: RTP/AVP/TCP;unicast;interleaved=0-1;ssrc=0x12345678\r\n\r\n",
                        "PLAY" => $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: {sessionId}\r\nRange: npt=0.000-\r\n"
                            + $"RTP-Info: url={Url}/trackID=0;seq=1;rtptime=0\r\n\r\n",
                        "OPTIONS" => $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n"
                            + "Public: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN\r\n\r\n",
                        _ => $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: {sessionId}\r\n\r\n",
                    };

                    await writeGate.WaitAsync(_cts.Token);
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
                        if (method == "PLAY" && !mediaStarted)
                        {
                            mediaStarted = true;
                            _ = Task.Run(() => PumpAsync(stream, writeGate, pump));
                        }
                    }
                    finally
                    {
                        writeGate.Release();
                    }
                }
            }
        }
        catch (Exception)
        {
            // 客戶端斷線（TEARDOWN 或程序被殺）是正常結束路徑，不是測試失敗。
        }
        finally
        {
            pump.TrySetResult();
        }
    }

    /// <summary>重播 GOP 直到連線結束：真實攝影機不會送一秒就停，停下來只會讓探測端等逾時。</summary>
    private async Task PumpAsync(
        NetworkStream stream,
        SemaphoreSlim writeGate,
        TaskCompletionSource done)
    {
        var sequence = new RtpSequence();
        uint timestamp = 0;
        try
        {
            await Task.Delay(150, _cts.Token);
            while (!_cts.IsCancellationRequested && !done.Task.IsCompleted)
            {
                foreach (var frame in _frames)
                {
                    for (var i = 0; i < frame.Count; i++)
                    {
                        foreach (var packet in RtpPackets(frame[i], marker: i == frame.Count - 1, timestamp, sequence))
                        {
                            await writeGate.WaitAsync(_cts.Token);
                            try
                            {
                                await stream.WriteAsync(packet, _cts.Token);
                            }
                            finally
                            {
                                writeGate.Release();
                            }
                        }
                    }

                    timestamp = (uint)((timestamp + (ClockRate / Fps)) & 0xFFFF_FFFF);
                    await Task.Delay(PacingMs, _cts.Token);
                }
            }
        }
        catch (Exception)
        {
            // 連線結束或取消。
        }
    }

    private byte[] Sdp()
    {
        var sdp = new StringBuilder()
            .Append("v=0\r\n")
            .Append("o=- 0 0 IN IP4 127.0.0.1\r\n")
            .Append("s=HeliVMS loopback\r\n")
            .Append("c=IN IP4 127.0.0.1\r\n")
            .Append("t=0 0\r\n")
            .Append("m=video 0 RTP/AVP 96\r\n")
            .Append("a=rtpmap:96 H264/90000\r\n")
            .Append("a=fmtp:96 packetization-mode=1;profile-level-id=42e01f;sprop-parameter-sets=")
            .Append(Convert.ToBase64String(_sps))
            .Append(',')
            .Append(Convert.ToBase64String(_pps))
            .Append("\r\n")
            .Append("a=control:trackID=0\r\n")
            .Append("a=range:npt=0-\r\n")
            .ToString();
        return Encoding.ASCII.GetBytes(sdp);
    }

    private static string WithBody(int cseq, string contentType, byte[] body) =>
        $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Type: {contentType}\r\n"
        + $"Content-Length: {body.Length}\r\n\r\n"
        + Encoding.ASCII.GetString(body);

    private static bool TryTakeRequest(List<byte> buffer, out string request)
    {
        request = string.Empty;

        // 標頭區塊以空行結束；RTSP 請求（OPTIONS/DESCRIBE/SETUP/PLAY）沒有內文。
        for (var i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == 0x0D && buffer[i + 1] == 0x0A && buffer[i + 2] == 0x0D && buffer[i + 3] == 0x0A)
            {
                request = Encoding.ASCII.GetString(buffer.GetRange(0, i + 4).ToArray());
                buffer.RemoveRange(0, i + 4);
                return true;
            }
        }

        return false;
    }

    /// <summary>依 Annex-B 起點碼切出 NAL 單元（去掉每個單元前的 3/4 byte 起點碼與緊接的 0）。</summary>
    private static List<byte[]> SplitNals(byte[] data)
    {
        var nals = new List<byte[]>();
        var index = 0;
        while (true)
        {
            var start = IndexOfStartCode(data, index);
            if (start < 0)
            {
                break;
            }

            var nalStart = start + 3;
            var next = IndexOfStartCode(data, nalStart);
            var nalEnd = next < 0 ? data.Length : next;
            while (nalEnd > nalStart && data[nalEnd - 1] == 0)
            {
                nalEnd--;
            }

            if (nalEnd > nalStart)
            {
                nals.Add(data[nalStart..nalEnd]);
            }

            index = nalStart;
        }

        return nals;
    }

    private static int IndexOfStartCode(byte[] data, int from)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>NAL 分組為影格：出現 VCL NAL（type 1／5）且目前這組已有 VCL NAL 就是下一格。</summary>
    private static List<IReadOnlyList<byte[]>> GroupFrames(List<byte[]> nals)
    {
        var frames = new List<IReadOnlyList<byte[]>>();
        var current = new List<byte[]>();
        foreach (var nal in nals)
        {
            var type = nal[0] & 0x1F;
            if ((type == 1 || type == 5) && current.Any(n => (n[0] & 0x1F) is 1 or 5))
            {
                frames.Add(current);
                current = [];
            }

            current.Add(nal);
        }

        if (current.Count > 0)
        {
            frames.Add(current);
        }

        return frames;
    }

    /// <summary>
    /// 把一個 NAL 包成 interleaved RTP：小於 MTU 用 single NAL unit，超過則用 FU-A 分片。
    /// </summary>
    private static List<byte[]> RtpPackets(byte[] nal, bool marker, uint timestamp, RtpSequence sequence)
    {
        var packets = new List<byte[]>();
        if (nal.Length <= Mtu)
        {
            packets.Add(Interleave(RtpHeader(sequence.Next(), timestamp, marker).Concat(nal).ToArray()));
            return packets;
        }

        var offset = 0;
        while (offset < nal.Length)
        {
            var length = Math.Min(Mtu, nal.Length - offset);
            var first = offset == 0;
            var last = offset + length >= nal.Length;

            // FU-A：原本的 NAL 標頭由 FU indicator＋FU header 取代，所以第一片不能重複原本的標頭位元組
            // （重複會讓解碼器把 NAL 主體的第一個位元組當成標頭，症狀是「deblocking filter parameters out of range」）。
            var payload = new List<byte>
            {
                (byte)((nal[0] & 0xE0) | 28),
                (byte)((nal[0] & 0x1F) | (first ? 0x80 : 0) | (last ? 0x40 : 0)),
            };
            payload.AddRange(nal.AsSpan(first ? offset + 1 : offset, first ? length - 1 : length).ToArray());

            packets.Add(Interleave(RtpHeader(sequence.Next(), timestamp, marker && last).Concat(payload).ToArray()));
            offset += length;
        }

        return packets;
    }

    private static byte[] RtpHeader(int seq, uint timestamp, bool marker) =>
    [
        0x80,
        (byte)(PayloadType | (marker ? 0x80 : 0)),
        (byte)(seq >> 8),
        (byte)(seq & 0xFF),
        (byte)(timestamp >> 24),
        (byte)(timestamp >> 16),
        (byte)(timestamp >> 8),
        (byte)timestamp,
        (byte)(Ssrc >> 24),
        (byte)(Ssrc >> 16),
        (byte)(Ssrc >> 8),
        (byte)Ssrc,
    ];

    private static byte[] Interleave(byte[] rtp)
    {
        var framed = new byte[4 + rtp.Length];
        framed[0] = 0x24;
        framed[1] = 0x00; // RTP 走 channel 0、RTCP 走 1
        framed[2] = (byte)(rtp.Length >> 8);
        framed[3] = (byte)(rtp.Length & 0xFF);
        rtp.CopyTo(framed, 4);
        return framed;
    }

    /// <summary>RTP 序號（迭代器不能帶 ref 參數，序號改成可變狀態）。</summary>
    private sealed class RtpSequence
    {
        private int _value = 1;

        public int Next() => _value = (_value + 1) & 0xFFFF;
    }
}
