using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen.Abstractions;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// 自訂非同步 RTSP 客戶端 — 實作 RTSP TCP 交握 + RTP/AVP/TCP 交錯傳輸。<br/>
/// 由於 FFmpeg 的 RTSP demuxer 不支援外部 AVIOContext（自行管理 TCP socket），
/// 此類別（1）手動執行 RTSP DESCRIBE/SETUP/PLAY 交握，（2）從 RTP 封包解出 H264/H265 NAL 單元，
/// （3）透過自訂 AVIOContext 餵給原始 H264/H265 demuxer，消除 FFmpeg 內建 RTSP handler 的阻塞執行緒。<br/><br/>
/// 資料流：<br/>
/// TCP Socket → ReadLoopAsync (ConcurrentQueue) → ReadCallback (AVIOContext) → ReassembleInterleaved → DepacketizeH264/5 → AppendNal → av_read_frame
/// </summary>
public sealed partial class AsyncRtspIO(string rtspUrl, string cameraId) : IDisposable {
    /// <summary>靜態實例對應表（ReadCallback 透過 instanceId 查找）</summary>
    private static readonly ConcurrentDictionary<int, AsyncRtspIO> _instances = new();
    private static int _nextId;

    private readonly int _instanceId = Interlocked.Increment(ref _nextId);
    private readonly string _rtspUrl = rtspUrl;
    private readonly string _cameraId = cameraId;
    private readonly CancellationTokenSource _stopCts = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _netStream;
    /// <summary>RTSP Session ID（SETUP 回應中取得）</summary>
    private string? _sessionId;
    /// <summary>CSeq 序號（防止 RTSP 請求衝突）</summary>
    private int _cseq;

    /// <summary>非同步讀取資料佇列：ReadLoopAsync 生產，ReadCallback 消費</summary>
    private readonly ConcurrentQueue<ArraySegment<byte>> _dataQueue = new();
    /// <summary>資料到達事件：ReadCallback 阻塞等待此事件</summary>
    private readonly ManualResetEventSlim _dataEvent = new(false);

    /// <summary>交錯緩衝區：TCP 串流緩衝，用於拼接 RTP 封包</summary>
    private readonly byte[] _interleaveBuf = new byte[65536];
    private int _interleaveOffset;
    /// <summary>交錯同步 flag：尋找下一個 $ 標記 (0x24)</summary>
    private bool _interleaveSync = true;

    /// <summary>NAL 緩衝區（ArrayPool 租用），預設 2MB</summary>
    private byte[] _nalBuffer = [];
    private int _nalLength;
    /// <summary>H264/H265 Annex-B 起始碼</summary>
    private static readonly byte[] AnnexB = [0x00, 0x00, 0x00, 0x01];

    /// <summary>網路讀取緩衝區（ArrayPool 租用）</summary>
    private byte[]? _readBuffer;

    private long _totalBytesRead;
    /// <summary>SDP 中偵測到的編碼：h264 / h265</summary>
    private string _sdpCodec = "h264";
    private bool _disposed;

    public string CameraId => _cameraId;
    public long TotalBytesRead => _totalBytesRead;

    /// <summary>
    /// RTSP 交握流程：<br/>
    /// 1. TCP 連線（10 秒逾時） → 2. DESCRIBE（取得 SDP）→<br/>
    /// 3. 解析 SDP 中的視訊軌道 → 4. SETUP（每軌，取得 Session ID）→<br/>
    /// 5. PLAY（開始串流）→ 6. 啟動 ReadLoopAsync（背景 RTP 讀取）
    /// </summary>
    public async Task<bool> OpenAsync(CancellationToken token = default) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopCts.Token);
        var ct = linked.Token;

        try {
            var uri = new Uri(_rtspUrl);
            var port = uri.Port > 0 ? uri.Port : 554;

            _tcpClient = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024, SendBufferSize = 65536 };
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);
            await _tcpClient.ConnectAsync(uri.Host, port, connectCts.Token).ConfigureAwait(false);
            _netStream = _tcpClient.GetStream();

            var creds = GetCredentials(uri);
            await SendDescribe(uri, creds, ct).ConfigureAwait(false);
            var descResp = await ReceiveResponseAsync(ct).ConfigureAwait(false);
            if (descResp == null || !descResp.Contains("200 OK")) return false;
            _sdpCodec = DetectCodec(descResp);

            var tracks = ParseSdpTracks(descResp);
            if (tracks.Count == 0) return false;

            for (var i = 0; i < tracks.Count; i++) {
                await SendSetup(uri, tracks[i].Control, i * 2, ct).ConfigureAwait(false);
                var setupResp = await ReceiveResponseAsync(ct).ConfigureAwait(false);
                if (setupResp == null) return false;
                ExtractSessionId(setupResp);
            }

            await SendPlay(uri, ct).ConfigureAwait(false);
            var playResp = await ReceiveResponseAsync(ct).ConfigureAwait(false);
            if (playResp == null || !playResp.Contains("200 OK")) {
                Log.Warning("[AsyncRtsp:{CameraId}] PLAY not OK", _cameraId);
                return false;
            }

            _ = Task.Run(() => ReadLoopAsync(ct), ct);
            return true;
        } catch (Exception ex) {
            Log.Error(ex, "[AsyncRtsp:{CameraId}] Open failed for {Url}", _cameraId, _rtspUrl);
            return false;
        }
    }

    private static unsafe readonly avio_alloc_context_read_packet _readPacketDeleg = ReadCallback;

    public unsafe AVFormatContext* CreateFormatContext() {
        _instances[_instanceId] = this;

        var buffer = (byte*)ffmpeg.av_malloc(256 * 1024);
        _avIoContext = ffmpeg.avio_alloc_context(
            buffer, 256 * 1024, 0, (void*)_instanceId,
            _readPacketDeleg, null, null);

        var fmtName = _sdpCodec is "h265" or "hevc" ? "hevc" : "h264";
        var fmt = ffmpeg.av_find_input_format(fmtName);
        if (fmt == null) {
            Log.Warning("[AsyncRtsp:{CameraId}] Raw demuxer {Codec} not found, fallback h264", _cameraId, fmtName);
            fmt = ffmpeg.av_find_input_format("h264");
        }

        _formatContext = ffmpeg.avformat_alloc_context();
        _formatContext->pb = _avIoContext;
        _formatContext->iformat = fmt;
        _formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

        Log.Information("[AsyncRtsp:{CameraId}] FormatContext ready, demuxer={Demuxer}", _cameraId, fmtName);
        return _formatContext;
    }

    private unsafe AVIOContext* _avIoContext;
    private unsafe AVFormatContext* _formatContext;

    public unsafe void CleanupFormatContext() {
        if (_formatContext != null) {
            var pCtx = _formatContext;
            ffmpeg.avformat_close_input(&pCtx);
            _formatContext = null;
        }
        if (_avIoContext != null) {
            var pb = _avIoContext;
            ffmpeg.avio_context_free(&pb);
            _avIoContext = null;
        }
    }

    /// <summary>
    /// FFmpeg AVIOContext 讀取回呼（在 FFmpeg 原生執行緒上執行）。<br/>
    /// 從 _dataQueue 取得 RTP 資料，組裝交錯緩衝區，解出 NAL 單元回傳給 FFmpeg raw demuxer。
    /// </summary>
    private static unsafe int ReadCallback(void* opaque, byte* buf, int bufSize) {
        var id = (int)(nint)opaque;
        if (_instances.TryGetValue(id, out var self) && !self._disposed)
            return self.ReadFromChannel(buf, bufSize);
        return -1; // AVERROR_EOF
    }

    /// <summary>
    /// 從交錯佇列讀取並重組 RTP 資料：<br/>
    /// 1. 嘗試從 _interleaveBuf 組裝完整 RTP 封包<br/>
    /// 2. 若不足則從 _dataQueue 取更多資料<br/>
    /// 3. 組裝完成後呼叫 ReassembleInterleaved 解出 NAL 給 FFmpeg<br/>
    /// 阻塞策略：ManualResetEventSlim.Wait(5000) — 不消耗執行緒集區
    /// </summary>
    private unsafe int ReadFromChannel(byte* buf, int bufSize) {
        try {
            while (true) {
                if (_interleaveOffset > 0) {
                    var c = ReassembleInterleaved(buf, bufSize);
                    if (c > 0) return c;
                }

                if (!_dataQueue.TryDequeue(out var chunkSeg)) {
                    _dataEvent.Reset();
                    if (!_dataQueue.TryDequeue(out chunkSeg)) {
                        if (!_dataEvent.Wait(5000)) return -5; // AVERROR(EIO)
                        if (!_dataQueue.TryDequeue(out chunkSeg)) continue;
                    }
                }

                var copyLen = Math.Min(chunkSeg.Count, _interleaveBuf.Length - _interleaveOffset);
                Buffer.BlockCopy(chunkSeg.Array!, chunkSeg.Offset, _interleaveBuf, _interleaveOffset, copyLen);
                _interleaveOffset += copyLen;
                ArrayPool<byte>.Shared.Return(chunkSeg.Array!);

                var consumed = ReassembleInterleaved(buf, bufSize);
                if (consumed > 0) return consumed;

                if (_interleaveOffset > _interleaveBuf.Length * 3 / 4)
                    _interleaveOffset = 0;
            }
        } catch {
            return -5; // AVERROR(EIO)
        }
    }

    private unsafe int ReassembleInterleaved(byte* buf, int bufSize) {
        if (_interleaveOffset < 4) return 0;

        var offset = 0;
        if (_interleaveSync) {
            while (offset < _interleaveOffset && _interleaveBuf[offset] != 0x24)
                offset++;
            if (offset > 0) {
                _interleaveOffset -= offset;
                Array.Copy(_interleaveBuf, offset, _interleaveBuf, 0, _interleaveOffset);
            }
            if (_interleaveOffset == 0 || _interleaveBuf[0] != 0x24) return 0;
            _interleaveSync = false;
        }

        if (_interleaveOffset < 4) return 0;
        var packetLen = (_interleaveBuf[2] << 8) | _interleaveBuf[3];
        if (packetLen <= 0 || packetLen > 65535) {
            _interleaveSync = true;
            _interleaveBuf[0] = 0;
            _interleaveOffset--;
            Array.Copy(_interleaveBuf, 1, _interleaveBuf, 0, _interleaveOffset);
            return 0;
        }

        var totalNeeded = 4 + packetLen;
        if (_interleaveOffset < totalNeeded) return 0;

        // Prepare NAL buffer
        if (_nalBuffer.Length == 0)
            _nalBuffer = ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
        _nalLength = 0;

        var hasData = TryDepacketizeRtp(
            new ReadOnlySpan<byte>(_interleaveBuf, 4, packetLen));

        _interleaveOffset -= totalNeeded;
        if (_interleaveOffset > 0)
            Array.Copy(_interleaveBuf, totalNeeded, _interleaveBuf, 0, _interleaveOffset);
        _interleaveSync = true;

        if (!hasData || _nalLength == 0) return 0;

        var writeLen = Math.Min(_nalLength, bufSize);
        fixed (byte* src = _nalBuffer) {
            Buffer.MemoryCopy(src, buf, writeLen, writeLen);
        }
        return writeLen;
    }

    private bool TryDepacketizeRtp(ReadOnlySpan<byte> rtpPacket) {
        if (rtpPacket.Length < 12) return false;
        var payload = rtpPacket[12..];
        if (payload.Length < 1) return false;

        return _sdpCodec is "h265" or "hevc"
            ? DepacketizeH265(payload)
            : DepacketizeH264(payload);
    }

    private void AppendNal(ReadOnlySpan<byte> data) {
        var needed = _nalLength + 4 + data.Length;
        if (needed > _nalBuffer.Length) {
            var newBuf = ArrayPool<byte>.Shared.Rent(needed);
            Buffer.BlockCopy(_nalBuffer, 0, newBuf, 0, _nalLength);
            ArrayPool<byte>.Shared.Return(_nalBuffer);
            _nalBuffer = newBuf;
        }
        AnnexB.CopyTo(_nalBuffer.AsSpan(_nalLength));
        _nalLength += 4;
        data.CopyTo(_nalBuffer.AsSpan(_nalLength));
        _nalLength += data.Length;
    }

    private void AppendNalByte(byte b) {
        var needed = _nalLength + 5;
        if (needed > _nalBuffer.Length) {
            var newBuf = ArrayPool<byte>.Shared.Rent(needed);
            Buffer.BlockCopy(_nalBuffer, 0, newBuf, 0, _nalLength);
            ArrayPool<byte>.Shared.Return(_nalBuffer);
            _nalBuffer = newBuf;
        }
        AnnexB.CopyTo(_nalBuffer.AsSpan(_nalLength));
        _nalLength += 4;
        _nalBuffer[_nalLength++] = b;
    }

    private void AppendRaw(ReadOnlySpan<byte> data) {
        var needed = _nalLength + data.Length;
        if (needed > _nalBuffer.Length) {
            var newBuf = ArrayPool<byte>.Shared.Rent(needed);
            Buffer.BlockCopy(_nalBuffer, 0, newBuf, 0, _nalLength);
            ArrayPool<byte>.Shared.Return(_nalBuffer);
            _nalBuffer = newBuf;
        }
        data.CopyTo(_nalBuffer.AsSpan(_nalLength));
        _nalLength += data.Length;
    }

    /// <summary>
    /// H264 RTP 去封包化 (RFC 3984)：<br/>
    /// • NAL 單元類型 1-23 → 單一 NAL 單元封包，直接附加 Annex-B 起始碼<br/>
    /// • NAL 單元類型 28 → FU-A 分割單元（start bit 判斷是否為 NAL 開頭）<br/>
    /// • NAL 單元類型 24 → STAP-A 聚合封包（包含多個 NAL 單元）
    /// </summary>
    private bool DepacketizeH264(ReadOnlySpan<byte> payload) {
        if (payload.Length < 1) return false;
        var nalType = (byte)(payload[0] & 0x1F);

        if (nalType >= 1 && nalType <= 23) {
            AppendNal(payload);
            return true;
        }

        if (nalType == 28) {
            if (payload.Length < 2) return false;
            var start = (payload[1] & 0x80) != 0;
            if (start) {
                AppendNalByte((byte)((payload[0] & 0xE0) | (payload[1] & 0x1F)));
                if (payload.Length > 2) AppendRaw(payload[2..]);
            } else {
                AppendRaw(payload[2..]);
            }
            return true;
        }

        if (nalType == 24) {
            var offset = 1;
            while (offset + 2 <= payload.Length) {
                var size = (payload[offset] << 8) | payload[offset + 1];
                offset += 2;
                if (offset + size > payload.Length) break;
                AppendNal(payload.Slice(offset, size));
                offset += size;
            }
            return _nalLength > 0;
        }
        return false;
    }

    /// <summary>
    /// H265 RTP 去封包化 (RFC 7798)：<br/>
    /// • NAL 類型 0-47 或 49 → 單一 NAL 單元封包<br/>
    /// • NAL 類型 48 → AP 聚合封包<br/>
    /// • NAL 類型 49 → FU 分割單元
    /// </summary>
    private bool DepacketizeH265(ReadOnlySpan<byte> payload) {
        if (payload.Length < 2) return false;
        var nalType = (payload[0] >> 1) & 0x3F;

        if (nalType <= 47) {
            if (nalType == 48) {
                var offset = 2;
                while (offset + 2 <= payload.Length) {
                    var size = (payload[offset] << 8) | payload[offset + 1];
                    offset += 2;
                    if (offset + size > payload.Length) break;
                    AppendNal(payload.Slice(offset, size));
                    offset += size;
                }
                return _nalLength > 0;
            }
            AppendNal(payload);
            return true;
        }

        if (nalType == 49) {
            if (payload.Length < 3) return false;
            var start = (payload[2] & 0x80) != 0;
            if (start) {
                var needed = _nalLength + 6;
                if (needed > _nalBuffer.Length) {
                    var newBuf = ArrayPool<byte>.Shared.Rent(needed);
                    Buffer.BlockCopy(_nalBuffer, 0, newBuf, 0, _nalLength);
                    ArrayPool<byte>.Shared.Return(_nalBuffer);
                    _nalBuffer = newBuf;
                }
                AnnexB.CopyTo(_nalBuffer.AsSpan(_nalLength));
                _nalLength += 4;
                _nalBuffer[_nalLength++] = (byte)((payload[0] & 0x81) | ((payload[2] & 0x3F) << 1));
                _nalBuffer[_nalLength++] = payload[1];
                if (payload.Length > 3) AppendRaw(payload[3..]);
            } else {
                AppendRaw(payload[3..]);
            }
            return true;
        }
        return false;
    }

    #region RTSP Protocol

    /// <summary>
    /// TCP 讀取迴圈（背景執行緒）：持續從 NetworkStream 讀取原始位元組，<br/>
    /// 放入 ConcurrentQueue 供 FFmpeg ReadCallback 消費。<br/>
    /// 使用 ArrayPool 避免每次讀取配置新緩衝區，但佇列項目為 new byte[]（需傳遞給原生執行緒）。
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct) {
        _readBuffer = ArrayPool<byte>.Shared.Rent(65536);
        try {
            while (!ct.IsCancellationRequested && _netStream != null && _netStream.CanRead) {
                var bytesRead = await _netStream.ReadAsync(
                    _readBuffer.AsMemory(0, _readBuffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0) break;
                _totalBytesRead += bytesRead;

                var chunk = ArrayPool<byte>.Shared.Rent(bytesRead);
                Buffer.BlockCopy(_readBuffer, 0, chunk, 0, bytesRead);
                _dataQueue.Enqueue(new ArraySegment<byte>(chunk, 0, bytesRead));
                _dataEvent.Set();
            }
        } catch (OperationCanceledException) { } catch (IOException) when (ct.IsCancellationRequested) { } catch (Exception ex) {
            if (!ct.IsCancellationRequested)
                Log.Warning(ex, "[AsyncRtsp:{CameraId}] Read loop error", _cameraId);
        } finally {
            _dataEvent.Set();
            if (_readBuffer != null) {
                ArrayPool<byte>.Shared.Return(_readBuffer);
                _readBuffer = null;
            }
        }
    }

    private static (string? User, string? Pass) GetCredentials(Uri uri) {
        if (!string.IsNullOrEmpty(uri.UserInfo)) {
            var parts = uri.UserInfo.Split(':');
            return (parts.Length > 0 ? Uri.UnescapeDataString(parts[0]) : null,
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);
        }
        return (null, null);
    }

    // ── Message builders (shared by sync & async paths) ──

    private string BuildDescribe((string? User, string? Pass) creds) {
        var cseq = Interlocked.Increment(ref _cseq);
        var sb = new StringBuilder();
        sb.Append($"DESCRIBE {_rtspUrl} RTSP/1.0\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        sb.Append("Accept: application/sdp\r\n");
        if (creds.User != null && creds.Pass != null) {
            var raw = Encoding.UTF8.GetBytes($"{creds.User}:{creds.Pass}");
            sb.Append($"Authorization: Basic {Convert.ToBase64String(raw)}\r\n");
        }
        sb.Append("User-Agent: HeliVMS/1.0\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private string BuildSetup(Uri uri, string control, int interleaveStart) {
        var cseq = Interlocked.Increment(ref _cseq);
        var trackUrl = control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            ? control
            : control.StartsWith('/')
                ? $"{uri.Scheme}://{uri.Host}:{uri.Port}{control}"
                : $"{uri.Scheme}://{uri.Host}:{uri.Port}/{control}";
        var sb = new StringBuilder();
        sb.Append($"SETUP {trackUrl} RTSP/1.0\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        sb.Append($"Transport: RTP/AVP/TCP;interleaved={interleaveStart}-{interleaveStart + 1}\r\n");
        sb.Append("User-Agent: HeliVMS/1.0\r\n");
        if (_sessionId != null) sb.Append($"Session: {_sessionId}\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private string BuildPlay() {
        var cseq = Interlocked.Increment(ref _cseq);
        var sb = new StringBuilder();
        sb.Append($"PLAY {_rtspUrl} RTSP/1.0\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        if (_sessionId != null) sb.Append($"Session: {_sessionId}\r\n");
        sb.Append("User-Agent: HeliVMS/1.0\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    // ── Async send path (unchanged) ──

    private async Task SendDescribe(Uri _, (string? User, string? Pass) creds, CancellationToken ct) {
        await SendAsync(BuildDescribe(creds), ct).ConfigureAwait(false);
    }

    private async Task SendSetup(Uri uri, string control, int interleaveStart, CancellationToken ct) {
        await SendAsync(BuildSetup(uri, control, interleaveStart), ct).ConfigureAwait(false);
    }

    private async Task SendPlay(Uri _, CancellationToken ct) {
        await SendAsync(BuildPlay(), ct).ConfigureAwait(false);
    }

    private async Task SendAsync(string msg, CancellationToken ct) {
        if (_netStream == null) return;
        var bytes = Encoding.UTF8.GetBytes(msg);
        await _netStream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
        await _netStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Synchronous variant of OpenAsync — for dedicated decode threads that must not block the thread pool.</summary>
    public bool Open(CancellationToken token = default) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopCts.Token);
        var ct = linked.Token;

        try {
            var uri = new Uri(_rtspUrl);
            var port = uri.Port > 0 ? uri.Port : 554;

            _tcpClient = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024, SendBufferSize = 65536 };

            ct.ThrowIfCancellationRequested();
            var ar = _tcpClient.BeginConnect(uri.Host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10))) {
                _tcpClient.Close();
                return false;
            }
            _tcpClient.EndConnect(ar);
            _netStream = _tcpClient.GetStream();
            _netStream.ReadTimeout = 15_000;
            _netStream.WriteTimeout = 15_000;

            var creds = GetCredentials(uri);
            SendSync(BuildDescribe(creds), ct);
            var descResp = ReceiveResponseSync(ct);
            if (descResp == null || !descResp.Contains("200 OK")) return false;
            _sdpCodec = DetectCodec(descResp);

            var tracks = ParseSdpTracks(descResp);
            if (tracks.Count == 0) return false;

            for (var i = 0; i < tracks.Count; i++) {
                SendSync(BuildSetup(uri, tracks[i].Control, i * 2), ct);
                var setupResp = ReceiveResponseSync(ct);
                if (setupResp == null) return false;
                ExtractSessionId(setupResp);
            }

            SendSync(BuildPlay(), ct);
            var playResp = ReceiveResponseSync(ct);
            if (playResp == null || !playResp.Contains("200 OK")) {
                Log.Warning("[AsyncRtsp:{CameraId}] PLAY not OK", _cameraId);
                return false;
            }

            _ = Task.Run(() => ReadLoopAsync(ct), ct);
            return true;
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            Log.Error(ex, "[AsyncRtsp:{CameraId}] Open failed for {Url}", _cameraId, _rtspUrl);
            return false;
        }
    }

    private void SendSync(string msg, CancellationToken ct) {
        if (_netStream == null) return;
        ct.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(msg);
        _netStream.Write(bytes, 0, bytes.Length);
        _netStream.Flush();
    }

    private string? ReceiveResponseSync(CancellationToken ct) {
        if (_netStream == null) return null;
        using var ms = new MemoryStream();
        var buf = new byte[1];
        var consecutiveNewlines = 0;
        var contentLength = -1;
        var bodyBytesRead = 0;
        var text = "";

        try {
            while (!ct.IsCancellationRequested) {
                var n = _netStream.Read(buf, 0, 1);
                if (n <= 0) break;
                ms.WriteByte(buf[0]);

                if (buf[0] == '\n') consecutiveNewlines++;
                else if (buf[0] != '\r') consecutiveNewlines = 0;

                if (consecutiveNewlines >= 2 && contentLength < 0) {
                    text = Encoding.UTF8.GetString(ms.ToArray());
                    var match = MyRegex().Match(text);
                    if (match.Success) contentLength = int.Parse(match.Groups[1].Value);
                    else break;
                }

                if (contentLength >= 0) {
                    if (text.Length == 0) text = Encoding.UTF8.GetString(ms.ToArray());
                    var headerEndIdx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEndIdx >= 0) {
                        bodyBytesRead = (int)ms.Length - (headerEndIdx + 4);
                        if (bodyBytesRead >= contentLength) break;
                    }
                }
            }
        } catch (OperationCanceledException) {
            Log.Warning("[AsyncRtsp:{CameraId}] Receive response timeout", _cameraId);
        } catch (IOException) when (ct.IsCancellationRequested) { }

        return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }

    private async Task<string?> ReceiveResponseAsync(CancellationToken ct) {
        if (_netStream == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var ms = new MemoryStream();
        var buf = new byte[1];
        var consecutiveNewlines = 0;
        var contentLength = -1;
        var bodyBytesRead = 0;

        try {
            while (!timeout.Token.IsCancellationRequested) {
                var n = await _netStream.ReadAsync(buf.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
                if (n <= 0) break;
                ms.WriteByte(buf[0]);

                if (buf[0] == '\n') consecutiveNewlines++;
                else if (buf[0] != '\r') consecutiveNewlines = 0;

                var text = Encoding.UTF8.GetString(ms.ToArray());

                if (consecutiveNewlines >= 2 && contentLength < 0) {
                    var match = MyRegex().Match(text);
                    if (match.Success) contentLength = int.Parse(match.Groups[1].Value);
                    else break;
                }

                if (contentLength >= 0) {
                    var headerEndIdx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEndIdx >= 0) {
                        bodyBytesRead = (int)ms.Length - (headerEndIdx + 4);
                        if (bodyBytesRead >= contentLength) break;
                    }
                }
            }
        } catch (OperationCanceledException) {
            Log.Warning("[AsyncRtsp:{CameraId}] Receive response timeout", _cameraId);
        }

        return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }

    private void ExtractSessionId(string response) {
        var match = SessionRegex().Match(response);
        if (match.Success)
            _sessionId = match.Groups[1].Value.Split(';')[0];
    }

    private static string DetectCodec(string sdp) {
        if (sdp.Contains("H265", StringComparison.OrdinalIgnoreCase) ||
            sdp.Contains("HEVC", StringComparison.OrdinalIgnoreCase))
            return "h265";
        return "h264";
    }

    private sealed class SdpTrack { public string Control { get; set; } = ""; }

    private static List<SdpTrack> ParseSdpTracks(string sdp) {
        var tracks = new List<SdpTrack>();
        SdpTrack? current = null;
        var inVideo = false;

        foreach (var line in sdp.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var trimmed = line.Trim().Trim('\r');
            if (trimmed.StartsWith("m=", StringComparison.Ordinal)) {
                if (current != null && inVideo && !string.IsNullOrEmpty(current.Control))
                    tracks.Add(current);
                current = new SdpTrack();
                inVideo = trimmed.StartsWith("m=video", StringComparison.OrdinalIgnoreCase);
            } else if (inVideo && current != null &&
                       trimmed.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase)) {
                current.Control = trimmed["a=control:".Length..].Trim();
            }
        }
        if (current != null && inVideo && !string.IsNullOrEmpty(current.Control))
            tracks.Add(current);

        return tracks;
    }

    #endregion

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        _instances.TryRemove(_instanceId, out _);
        _stopCts.Cancel();
        _stopCts.Dispose();
        CleanupFormatContext();
        _netStream?.Dispose();
        _tcpClient?.Dispose();
        _dataEvent.Set();
        _dataEvent.Dispose();

        if (_nalBuffer.Length > 0) {
            ArrayPool<byte>.Shared.Return(_nalBuffer);
            _nalBuffer = [];
        }
        if (_readBuffer != null) {
            ArrayPool<byte>.Shared.Return(_readBuffer);
            _readBuffer = null;
        }
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"Session:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionRegex();
}
