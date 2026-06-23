// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeliVMS.Controls;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// 解碼器外部程序通訊管理 — 透過 Named Pipe 與 HeliVMS.Decoder.exe 通訊。<br/>
/// 訊息協定：8 位元組標頭 (MsgType:int + PayloadLen:int) + JSON 或二進位承載。<br/>
/// 幀資料透過 PooledBuffer（ArrayPool）直接傳送，避免逐幀 GC 分配。<br/>
/// 使用 SemaphoreSlim(8,8) 限制同時啟用的解碼器數量，防止 CPU/記憶體暴增。
/// </summary>
internal sealed class DecoderSession(string cameraId, string ffmpegPath) : IPlaybackDecoder {
    private readonly string _cameraId = cameraId;
    private readonly string _ffmpegPath = ffmpegPath;
    private readonly string _pipeName = $"HeliVMS_Decoder_{Guid.NewGuid():N}";

    private Process? _process;
    private NamedPipeServerStream? _pipeServer;
    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private readonly Lock _pipeLock = new();
    private volatile bool _processExited;
    private bool _disposed;

    private long _duration;
    private volatile bool _isLive;

    /// <summary>同時啟動解碼器上限（避免大量 ffmpeg RTSP 連線導致記憶體暴增）</summary>
    private static readonly SemaphoreSlim _startThrottle = new(8, 8);
    /// <summary>啟動順序計數（用於錯開啟動延遲）</summary>
    private static int _launchOrder;

    /// <summary>來自 EvtFrameInfo 的待處理幀元資料（下一個 EvtFrameData 使用）</summary>
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;
    private long _pendingFramePts;

    public event Action<PooledBuffer>? FrameReady;
    public event Action<long, long>? PositionChanged;
    public event Action<bool>? PlaybackStatusChanged;
    public event Action? EOFReached;
    public event Action<string>? ErrorOccurred;

    public string CameraId => _cameraId;
    public long DurationMicroseconds => _duration;
    public bool IsRunning {
        get {
            if (_processExited) { return false; }
            try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; }
        }
    }

    public void Open(string filePath, long seekUs = 0, double? targetFps = null, int targetDecodeHeight = 0) {
        _isLive = false;
        _ = InitializeAsync(filePath, seekUs, targetFps, targetDecodeHeight);
    }

    public void OpenLive(string rtspUrl, double? targetFps = 10) {
        _isLive = true;
        _ = InitializeAsync(rtspUrl, seekUs: 0, targetFps, targetDecodeHeight: 0);
    }

    private async Task InitializeAsync(string filePath, long seekUs, double? targetFps, int targetDecodeHeight) {
        Stop();
        lock (_pipeLock) {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        var token = _cts.Token;

        // Wait for throttle semaphore to prevent CPU/memory overload from ffmpeg RTSP
        if (!await _startThrottle.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false)) {
            Log.Warning("[DecoderSession:{CameraId}] Start throttle timeout (30s), proceeding anyway", _cameraId);
        }
        try {
            // Stagger start delay to spread out CPU/memory load across decoders
            await StaggerDelay(token).ConfigureAwait(false);
            await InitializeCoreAsync(filePath, seekUs, targetFps, token, targetDecodeHeight).ConfigureAwait(false);
        } finally {
            try { _startThrottle.Release(); } catch { }
        }
    }
    // Stagger start delays to avoid CPU/memory spikes
    private static async Task StaggerDelay(CancellationToken token) {
        var order = Interlocked.Increment(ref _launchOrder);
        if (order > 1) {
            var delayMs = Math.Min(order * 400, 5000);
            await Task.Delay(delayMs, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 初始化解碼器外部程序並建立 Named Pipe 連線：<br/>
    /// 1. 建立 NamedPipeServerStream（256KB 緩衝）<br/>
    /// 2. 啟動 HeliVMS.Decoder.exe（含 --pipe, --ffmpeg, --camera, --hwaccel 參數）<br/>
    /// 3. 等待 Pipe 連線（10 秒逾時）<br/>
    /// 4. 建立專屬 ReadLoop 執行緒處理解碼器事件<br/>
    /// 5. 傳送 CmdOpen 及 CmdSetTargetFps<br/>
    /// stderr 過濾器：過濾已知 FFmpeg 警告避免日誌 flooding
    /// </summary>
    private async Task InitializeCoreAsync(string filePath, long seekUs, double? targetFps, CancellationToken token, int targetDecodeHeight = 0) {
        _pipeServer = new NamedPipeServerStream(
        _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
        256 * 1024, 256 * 1024); // 256KB pipe 緩衝（容納幀 backlog）

        var decoderExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HeliVMS.Decoder.exe");
        if (!File.Exists(decoderExe)) {
            decoderExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..",
                "..", "Tools", "HeliVMS.Decoder", "bin", "Debug", "net10.0-windows", "HeliVMS.Decoder.exe");
            decoderExe = Path.GetFullPath(decoderExe);
        }

        var psi = new ProcessStartInfo(decoderExe) {
            Arguments = $"--pipe \"{_pipeName}\" --ffmpeg \"{_ffmpegPath}\" --camera \"{_cameraId}\" --hwaccel auto",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Log.Information("[DecoderSession:{CameraId}] Spawning decoder: {Exe} {Args}",
            _cameraId, decoderExe, psi.Arguments);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Log.Debug("[Decoder:{Id}] {Data}", _cameraId, e.Data); } };
        _process.ErrorDataReceived += (_, e) => {
            if (e.Data is not null) {
                if (e.Data.Contains("Missing reference picture") ||
                    e.Data.Contains("concealing") ||
                    e.Data.Contains("default is") ||
                    e.Data.Contains("error while decoding MB") ||
                    e.Data.Contains("corrupted macroblock") ||
                    e.Data.Contains("out of range") ||
                    e.Data.Contains("mb_type") && e.Data.Contains("too large") ||
                    e.Data.Contains("Invalid level prefix") ||
                    e.Data.Contains("negative number of zero coeffs") ||
                    e.Data.Contains("left block unavailable") ||
                    e.Data.Contains("pps_id") && e.Data.Contains("out of range") ||
                    e.Data.Contains("coeff_count") ||
                    e.Data.Contains("coded bitstream") ||
                    e.Data.Contains("RTP:") && e.Data.Contains("bad cseq")) {
                    return;
                }

                Log.Debug("[Decoder:{Id}] {Data}", _cameraId, e.Data);
            }
        };

        _process.Start();
        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        bool pipeConnected;
        try {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await _pipeServer.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
            pipeConnected = true;
        } catch (OperationCanceledException) {
            Log.Error("[DecoderSession:{CameraId}] Pipe connection timeout (10s)", _cameraId);
            ErrorOccurred?.Invoke("Decoder pipe connection timeout");
            KillProcess();
            return;
        } catch (Exception ex) {
            Log.Error(ex, "[DecoderSession:{CameraId}] Pipe connection failed", _cameraId);
            ErrorOccurred?.Invoke($"Decoder pipe connection failed: {ex.Message}");
            KillProcess();
            return;
        }

        if (!pipeConnected) { return; }

        await Task.Factory.StartNew(() => {
            if (_processExited) {
                var code = -1;
                try { code = _process?.ExitCode ?? -1; } catch { }
                Log.Warning("[DecoderSession:{CameraId}] Process exited before pipe connection completed, exit code={ExitCode}",
                    _cameraId, code);
                ErrorOccurred?.Invoke($"Decoder process exited with code {code} before CmdOpen");
                return;
            }

            var readThread = new Thread(() => ReadLoop(token)) {
                Name = $"DecodeRead-{_cameraId}",
                IsBackground = true
            };
            readThread.Start();
            _readThread = readThread;

            var cmdOpenOk = SendCommand(DecoderMessageType.CmdOpen, new OpenPayload {
                FilePath = filePath,
                SeekMicroseconds = _isLive ? 0 : seekUs,
                TargetDecodeHeight = targetDecodeHeight,
            });

            if (cmdOpenOk && targetFps.HasValue) {
                SendCommand(DecoderMessageType.CmdSetTargetFps, new FpsPayload { Fps = targetFps.Value });
            }

            if (cmdOpenOk) {
                Log.Information("[DecoderSession:{CameraId}] Pipe connected, CmdOpen sent (seek={SeekUs}us, fps={Fps}, targetH={TargetH})",
                    _cameraId, seekUs, targetFps, targetDecodeHeight);
            } else {
                Log.Warning("[DecoderSession:{CameraId}] Pipe connected but CmdOpen failed (process may have crashed)",
                    _cameraId);
                ErrorOccurred?.Invoke("Pipe connected but CmdOpen failed");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
    }

    public void Seek(long microseconds) {
        SendCommand(DecoderMessageType.CmdSeek, new SeekPayload { Microseconds = microseconds });
    }

    public void SeekSeconds(double seconds) {
        Seek((long)(seconds * 1_000_000));
    }

    public void SetPlaybackRate(double rate) {
        SendCommand(DecoderMessageType.CmdSetRate, new RatePayload { Rate = rate });
    }

    public void SetTargetFps(double fps) {
        SendCommand(DecoderMessageType.CmdSetTargetFps, new FpsPayload { Fps = fps });
    }

    public void Pause() {
        SendCommand(DecoderMessageType.CmdPause, null);
    }

    public void Resume() {
        SendCommand(DecoderMessageType.CmdResume, null);
    }

    public void Stop() {
        _isLive = false;
        try { SendCommand(DecoderMessageType.CmdStop, null); } catch { /* pipe may already be closed */ }

        KillProcess();
    }

    private bool SendCommand(DecoderMessageType type, object? payload) {
        lock (_pipeLock) {
            if (_pipeServer is null || !_pipeServer.IsConnected) {
                return false;
            }

            try {
                byte[] payloadBytes;
                if (payload is not null) {
                    payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), DecoderProtocolSerializer.Context);
                } else {
                    payloadBytes = [];
                }

                Span<byte> header = stackalloc byte[8];
                BitConverter.TryWriteBytes(header, (int)type);
                BitConverter.TryWriteBytes(header[4..], payloadBytes.Length);
                _pipeServer.Write(header);
                if (payloadBytes.Length > 0) {
                    _pipeServer.Write(payloadBytes, 0, payloadBytes.Length);
                }
                _pipeServer.Flush();
                return true;
            } catch (Exception ex) {
                Log.Warning(ex, "[DecoderSession:{CameraId}] SendCommand failed", _cameraId);
                return false;
            }
        }
    }

    private void ReadLoop(CancellationToken token) {
        var headerBuf = new byte[8];

        try {
            while (!token.IsCancellationRequested) {
                var pipe = _pipeServer;
                if (pipe is null || !pipe.IsConnected) { break; }

                var bytesRead = ReadFully(pipe, headerBuf, 0, 8, token);
                if (bytesRead < 8) { break; }

                var msgType = (DecoderMessageType)BitConverter.ToInt32(headerBuf, 0);
                var payloadLen = BitConverter.ToInt32(headerBuf, 4);

                byte[]? jsonPayload = null;
                PooledBuffer? pooledPayload = null;
                if (payloadLen > 0) {
                    // Frame data (EvtFrameData) uses native pooled buffer; everything else uses byte[]
                    if (msgType == DecoderMessageType.EvtFrameData) {
                        pooledPayload = new PooledBuffer(payloadLen);
                        if (ReadFully(pipe, pooledPayload.DataPtr, payloadLen, token) < payloadLen) {
                            pooledPayload.Dispose();
                            break;
                        }
                        pooledPayload.DataSize = payloadLen;
                    } else {
                        jsonPayload = new byte[payloadLen];
                        if (ReadFully(pipe, jsonPayload, 0, payloadLen, token) < payloadLen) {
                            break;
                        }
                    }
                }

                DispatchEvent(msgType, jsonPayload, pooledPayload);
            }
        } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (NullReferenceException) { } catch (IOException ex) {
            Log.Warning(ex, "[DecoderSession:{CameraId}] Pipe read error (process may have crashed)", _cameraId);
        }
    }

    private void DispatchEvent(DecoderMessageType type, byte[]? jsonPayload, PooledBuffer? pooledPayload) {
        try {
            switch (type) {
                case DecoderMessageType.EvtFrameInfo:
                    if (jsonPayload is not null && jsonPayload.Length >= Unsafe.SizeOf<FrameInfoHeader>()) {
                        var header = MemoryMarshal.Read<FrameInfoHeader>(jsonPayload);
                        _pendingFrameWidth = header.Width;
                        _pendingFrameHeight = header.Height;
                        _pendingFramePts = header.Pts;
                    } else {
                        _pendingFrameWidth = 0;
                    }
                    break;

                case DecoderMessageType.EvtFrameData:
                    if (_pendingFrameWidth > 0 && _pendingFrameHeight > 0 && pooledPayload is not null) {
                        pooledPayload.Width = _pendingFrameWidth;
                        pooledPayload.Height = _pendingFrameHeight;
                        pooledPayload.PtsMicroseconds = _pendingFramePts;
                        _pendingFrameWidth = 0;
                        FrameReady?.Invoke(pooledPayload);
                    } else {
                        pooledPayload?.Dispose();
                    }
                    break;

                case DecoderMessageType.EvtPosition:
                    if (jsonPayload is not null) {
                        var pos = JsonSerializer.Deserialize(jsonPayload.AsSpan(), DecoderProtocolContext.Default.PositionPayload);
                        if (pos is not null) {
                            _duration = pos.Duration;
                            Log.Debug("[DecoderSession:{CamId}] EvtPosition pts={Pts}us dur={Dur}us",
                                _cameraId, pos.Pts, pos.Duration);
                            PositionChanged?.Invoke(pos.Pts, pos.Duration);
                        }
                    }
                    break;

                case DecoderMessageType.EvtStatus:
                    if (jsonPayload is not null) {
                        var st = JsonSerializer.Deserialize(jsonPayload.AsSpan(), DecoderProtocolContext.Default.StatusPayload);
                        if (st is not null) {
                            PlaybackStatusChanged?.Invoke(st.Playing);
                        }
                    }
                    break;

                case DecoderMessageType.EvtEof:
                    EOFReached?.Invoke();
                    break;

                case DecoderMessageType.EvtError:
                    if (jsonPayload is not null) {
                        var err = JsonSerializer.Deserialize(jsonPayload.AsSpan(), DecoderProtocolContext.Default.ErrorPayload);
                        if (err is not null) {
                            ErrorOccurred?.Invoke(err.Message);
                        }
                    }
                    break;
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[DecoderSession:{CameraId}] Dispatch event failed", _cameraId);
        }
    }

    private static int ReadFully(NamedPipeServerStream pipe, byte[] buffer, int offset, int count, CancellationToken token) {
        var totalRead = 0;
        while (totalRead < count) {
            token.ThrowIfCancellationRequested();
            var chunk = pipe.Read(buffer, offset + totalRead, count - totalRead);
            if (chunk <= 0) { break; }
            totalRead += chunk;
        }
        return totalRead;
    }

    private static unsafe int ReadFully(NamedPipeServerStream pipe, IntPtr buffer, int count, CancellationToken token) {
        var totalRead = 0;
        var span = new Span<byte>((byte*)buffer, count);
        while (totalRead < count) {
            token.ThrowIfCancellationRequested();
            var chunk = pipe.Read(span.Slice(totalRead));
            if (chunk <= 0) { break; }
            totalRead += chunk;
        }
        return totalRead;
    }

    private void OnProcessExited(object? sender, EventArgs e) {
        _processExited = true;
        if (_process is null) {
            Log.Warning("[DecoderSession:{CameraId}] Process exited (process object null)", _cameraId);
            return;
        }

        int exitCode;
        try { exitCode = _process.ExitCode; } catch (InvalidOperationException) { return; }

        if (exitCode == 0) {
            Log.Information("[DecoderSession:{CameraId}] Process exited code={ExitCode}",
                _cameraId, exitCode);
        } else {
            Log.Warning("[DecoderSession:{CameraId}] Process exited code={ExitCode}",
                _cameraId, exitCode);
            ErrorOccurred?.Invoke($"Decoder process exited with code {exitCode} (FFmpeg crash)");
        }
    }

    private void KillProcess() {
        // Sync fallback for Dispose
        try {
            _cts?.Cancel();
            if (_process is { HasExited: false }) {
                SendCommand(DecoderMessageType.CmdExit, null);
                if (!_process.WaitForExit(2000)) {
                    _process.Kill();
                    _process.WaitForExit(1000);
                }
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[DecoderSession:{CameraId}] KillProcess", _cameraId);
        } finally {
            Thread? readThread;
            lock (_pipeLock) {
                _pipeServer?.Dispose();
                _pipeServer = null;
                readThread = _readThread;
                _readThread = null;
                _process?.Dispose();
                _process = null;
            }
            if (readThread is { IsAlive: true }) {
                readThread.Join(1000);
            }
        }
    }

    private async Task KillProcessAsync() {
        try {
            _cts?.Cancel();
            if (_process is { HasExited: false }) {
                SendCommand(DecoderMessageType.CmdExit, null);
                if (!await WaitForExitAsync(_process, 2000).ConfigureAwait(false)) {
                    _process.Kill();
                    await WaitForExitAsync(_process, 1000).ConfigureAwait(false);
                }
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[DecoderSession:{CameraId}] KillProcessAsync", _cameraId);
        } finally {
            Thread? readThread;
            lock (_pipeLock) {
                _pipeServer?.Dispose();
                _pipeServer = null;
                readThread = _readThread;
                _readThread = null;
                _process?.Dispose();
                _process = null;
            }
            if (readThread is { IsAlive: true }) {
                readThread.Join(1000);
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs) {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
