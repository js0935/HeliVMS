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
    /// Manages decoder subprocess communication via Named Pipe.
    /// Used by FilePlaybackService and PlaybackCoordinator.
    /// </summary>
internal sealed class DecoderSession : IDisposable
{
    private readonly string _cameraId;
    private readonly string _ffmpegPath;
    private readonly string _pipeName;

    private Process? _process;
    private NamedPipeServerStream? _pipeServer;
    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private readonly object _pipeLock = new();
    private volatile bool _processExited;
    private bool _disposed;

    private long _duration;
    private volatile bool _isLive;

    // Limit concurrent decoder launches to prevent CPU/memory overload from ffmpeg RTSP
        private static readonly SemaphoreSlim _startThrottle = new(8, 8);
    private static int _launchOrder;

    // Pending frame dimensions from FrameInfoPayload before EvtFrameData
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;

    // Events consumed by FilePlaybackService
    public event Action<PooledBuffer>? FrameReady;
    public event Action<long, long>? PositionChanged;
    public event Action<bool>? PlaybackStatusChanged;
    public event Action? EOFReached;
    public event Action<string>? ErrorOccurred;

    public string CameraId => _cameraId;
    public long DurationMicroseconds => _duration;
    public bool IsRunning
    {
        get
        {
            if (_processExited) { return false; }
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public DecoderSession(string cameraId, string ffmpegPath)
    {
        _cameraId = cameraId;
        _ffmpegPath = ffmpegPath;
        _pipeName = $"HeliVMS_Decoder_{Guid.NewGuid():N}";
    }

    public void Open(string filePath, long seekUs = 0, double? targetFps = null)
    {
        _isLive = false;
        _ = InitializeAsync(filePath, seekUs, targetFps);
    }

    public void OpenLive(string rtspUrl, double? targetFps = 10)
    {
        _isLive = true;
        _ = InitializeAsync(rtspUrl, seekUs: 0, targetFps);
    }

    private async Task InitializeAsync(string filePath, long seekUs, double? targetFps)
    {
        Stop();
        lock (_pipeLock)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        var token = _cts.Token;

        // Wait for throttle semaphore to prevent CPU/memory overload from ffmpeg RTSP
        if (!await _startThrottle.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false))
        {
            Log.Warning("[DecoderSession:{CameraId}] Start throttle timeout (30s), proceeding anyway", _cameraId);
        }
        try
        {
            // Stagger start delay to spread out CPU/memory load across decoders
            await StaggerDelay(token).ConfigureAwait(false);
            await InitializeCoreAsync(filePath, seekUs, targetFps, token).ConfigureAwait(false);
        }
        finally
        {
            try { _startThrottle.Release(); } catch { }
        }
    }
    // Stagger start delays to avoid CPU/memory spikes
    private async Task StaggerDelay(CancellationToken token)
    {
        int order = Interlocked.Increment(ref _launchOrder);
        if (order > 1)
        {
            int delayMs = Math.Min(order * 400, 5000);
            await Task.Delay(delayMs, token).ConfigureAwait(false);
        }
    }

    private async Task InitializeCoreAsync(string filePath, long seekUs, double? targetFps, CancellationToken token)
    {
        // Create NamedPipeServer before spawning process to avoid race condition
            _pipeServer = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            256 * 1024, 256 * 1024); // 256KB pipe buffer for frame backlog

        // Locate decoder executable
        var decoderExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HeliVMS.Decoder.exe");
        if (!File.Exists(decoderExe))
        {
            // Fallback to project source directory during development
            decoderExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..",
                "..", "Tools", "HeliVMS.Decoder", "bin", "Debug", "net10.0-windows", "HeliVMS.Decoder.exe");
            decoderExe = Path.GetFullPath(decoderExe);
        }

        var psi = new ProcessStartInfo(decoderExe)
        {
            Arguments = $"--pipe \"{_pipeName}\" --ffmpeg \"{_ffmpegPath}\" --camera \"{_cameraId}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Log.Information("[DecoderSession:{CameraId}] Spawning decoder: {Exe} {Args}",
            _cameraId, decoderExe, psi.Arguments);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        // Redirect stdout/stderr to log, not pipe
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Log.Debug("[Decoder:{Id}] {Data}", _cameraId, e.Data); } }; // REVIEW: lambda captures 'this' ??consider weak event pattern
        _process.ErrorDataReceived += (_, e) => // REVIEW: lambda captures 'this' ??consider weak event pattern
        {
            if (e.Data is not null)
            {
                // Filter known ffmpeg stderr noise to avoid log flooding
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
                    e.Data.Contains("RTP:") && e.Data.Contains("bad cseq"))
                {
                    return;
                }

                // Route remaining stderr to Debug to avoid flood
                Log.Debug("[Decoder:{Id}] {Data}", _cameraId, e.Data);
            }
        };

        _process.Start();
        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not critical */ }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Wait for decoder pipe connection on ThreadPool
        bool pipeConnected;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await _pipeServer.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
            pipeConnected = true;
        }
        catch (OperationCanceledException)
        {
            Log.Error("[DecoderSession:{CameraId}] Pipe connection timeout (10s)", _cameraId);
            ErrorOccurred?.Invoke("Decoder pipe connection timeout");
            KillProcess();
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DecoderSession:{CameraId}] Pipe connection failed", _cameraId);
            ErrorOccurred?.Invoke($"Decoder pipe connection failed: {ex.Message}");
            KillProcess();
            return;
        }

        if (!pipeConnected) { return; }

        // Start pipe reader on dedicated thread to avoid blocking ThreadPool
        await Task.Factory.StartNew(() =>
        {
            // Check process exit flag before starting to avoid HasExited race
            if (_processExited)
            {
                int code = -1;
                try { code = _process?.ExitCode ?? -1; } catch { }
                Log.Warning("[DecoderSession:{CameraId}] Process exited before pipe connection completed, exit code={ExitCode}",
                    _cameraId, code);
                ErrorOccurred?.Invoke($"Decoder process exited with code {code} before CmdOpen");
                return;
            }

            // Start dedicated read thread for decoder pipe messages
            var readThread = new Thread(() => ReadLoop(token))
            {
                Name = $"DecodeRead-{_cameraId}",
                IsBackground = true
            };
            readThread.Start();
            _readThread = readThread;

            // Pipe connected, send CmdOpen
            bool cmdOpenOk = SendCommand(DecoderMessageType.CmdOpen, new OpenPayload
            {
                FilePath = filePath,
                SeekMicroseconds = _isLive ? 0 : seekUs,
            });

            // Limit FPS to reduce CPU load
            if (cmdOpenOk && targetFps.HasValue)
            {
                SendCommand(DecoderMessageType.CmdSetTargetFps, new FpsPayload { Fps = targetFps.Value });
            }

            if (cmdOpenOk)
            {
                Log.Information("[DecoderSession:{CameraId}] Pipe connected, CmdOpen sent (seek={SeekUs}us, fps={Fps})",
                    _cameraId, seekUs, targetFps);
            }
            else
            {
                Log.Warning("[DecoderSession:{CameraId}] Pipe connected but CmdOpen failed (process may have crashed)",
                    _cameraId);
                ErrorOccurred?.Invoke("Pipe connected but CmdOpen failed");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
    }

    public void Seek(long microseconds)
    {
        SendCommand(DecoderMessageType.CmdSeek, new SeekPayload { Microseconds = microseconds });
    }

    public void SeekSeconds(double seconds)
    {
        Seek((long)(seconds * 1_000_000));
    }

    public void SetPlaybackRate(double rate)
    {
        SendCommand(DecoderMessageType.CmdSetRate, new RatePayload { Rate = rate });
    }

    public void SetTargetFps(double fps)
    {
        SendCommand(DecoderMessageType.CmdSetTargetFps, new FpsPayload { Fps = fps });
    }

    public void Pause()
    {
        SendCommand(DecoderMessageType.CmdPause, null);
    }

    public void Resume()
    {
        SendCommand(DecoderMessageType.CmdResume, null);
    }

    public void Stop()
    {
        _isLive = false;
        try { SendCommand(DecoderMessageType.CmdStop, null); }
        catch { /* pipe may already be closed */ }

        KillProcess();
    }

    private bool SendCommand(DecoderMessageType type, object? payload)
    {
        lock (_pipeLock)
        {
            if (_pipeServer is null || !_pipeServer.IsConnected)
            {
                return false;
            }

            try
            {
                byte[] payloadBytes;
                if (payload is not null)
                {
                    payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
                }
                else
                {
                    payloadBytes = [];
                }

                Span<byte> header = stackalloc byte[8];
                BitConverter.TryWriteBytes(header, (int)type);
                BitConverter.TryWriteBytes(header[4..], payloadBytes.Length);
                _pipeServer.Write(header);
                if (payloadBytes.Length > 0)
                {
                    _pipeServer.Write(payloadBytes, 0, payloadBytes.Length);
                }
                _pipeServer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DecoderSession:{CameraId}] SendCommand failed", _cameraId);
                return false;
            }
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        var headerBuf = new byte[8];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = _pipeServer;
                if (pipe is null || !pipe.IsConnected) { break; }

                int bytesRead = ReadFully(pipe, headerBuf, 0, 8, token);
                if (bytesRead < 8) { break; }

                var msgType = (DecoderMessageType)BitConverter.ToInt32(headerBuf, 0);
                var payloadLen = BitConverter.ToInt32(headerBuf, 4);

                byte[]? jsonPayload = null;
                PooledBuffer? pooledPayload = null;
                if (payloadLen > 0)
                {
                    // Frame data (EvtFrameData) uses pooled buffer; everything else uses byte[]
                    if (msgType == DecoderMessageType.EvtFrameData)
                    {
                        pooledPayload = new PooledBuffer(payloadLen);
                        if (ReadFully(pipe, pooledPayload.Data, 0, payloadLen, token) < payloadLen)
                        {
                            pooledPayload.Dispose();
                            break;
                        }
                        pooledPayload.DataSize = payloadLen;
                    }
                    else
                    {
                        jsonPayload = new byte[payloadLen];
                        if (ReadFully(pipe, jsonPayload, 0, payloadLen, token) < payloadLen)
                        {
                            break;
                        }
                    }
                }

                DispatchEvent(msgType, jsonPayload, pooledPayload);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException ex)
        {
            Log.Warning(ex, "[DecoderSession:{CameraId}] Pipe read error (process may have crashed)", _cameraId);
        }
    }

    private void DispatchEvent(DecoderMessageType type, byte[]? jsonPayload, PooledBuffer? pooledPayload)
    {
        try
        {
            switch (type)
            {
                case DecoderMessageType.EvtFrameInfo:
                    if (jsonPayload is not null && jsonPayload.Length >= Unsafe.SizeOf<FrameInfoHeader>())
                    {
                        var header = MemoryMarshal.Read<FrameInfoHeader>(jsonPayload);
                        _pendingFrameWidth = header.Width;
                        _pendingFrameHeight = header.Height;
                    }
                    else
                    {
                        _pendingFrameWidth = 0;
                    }
                    break;

                case DecoderMessageType.EvtFrameData:
                    if (_pendingFrameWidth > 0 && _pendingFrameHeight > 0 && pooledPayload is not null)
                    {
                        pooledPayload.Width = _pendingFrameWidth;
                        pooledPayload.Height = _pendingFrameHeight;
                        _pendingFrameWidth = 0;
                        FrameReady?.Invoke(pooledPayload);
                    }
                    else
                    {
                        pooledPayload?.Dispose();
                    }
                    break;

                case DecoderMessageType.EvtPosition:
                    if (jsonPayload is not null)
                    {
                        var pos = JsonSerializer.Deserialize<PositionPayload>(jsonPayload.AsSpan());
                        if (pos is not null)
                        {
                            _duration = pos.Duration;
                            Log.Debug("[DecoderSession:{CamId}] EvtPosition pts={Pts}us dur={Dur}us",
                                _cameraId, pos.Pts, pos.Duration);
                            PositionChanged?.Invoke(pos.Pts, pos.Duration);
                        }
                    }
                    break;

                case DecoderMessageType.EvtStatus:
                    if (jsonPayload is not null)
                    {
                        var st = JsonSerializer.Deserialize<StatusPayload>(jsonPayload.AsSpan());
                        if (st is not null)
                        {
                            PlaybackStatusChanged?.Invoke(st.Playing);
                        }
                    }
                    break;

                case DecoderMessageType.EvtEof:
                    EOFReached?.Invoke();
                    break;

                case DecoderMessageType.EvtError:
                    if (jsonPayload is not null)
                    {
                        var err = JsonSerializer.Deserialize<ErrorPayload>(jsonPayload.AsSpan());
                        if (err is not null)
                        {
                            ErrorOccurred?.Invoke(err.Message);
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DecoderSession:{CameraId}] Dispatch event failed", _cameraId);
        }
    }

    private static int ReadFully(NamedPipeServerStream pipe, byte[] buffer, int offset, int count, CancellationToken token)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            token.ThrowIfCancellationRequested();
            var chunk = pipe.Read(buffer, offset + totalRead, count - totalRead);
            if (chunk <= 0) { break; }
            totalRead += chunk;
        }
        return totalRead;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _processExited = true;
        if (_process is null)
        {
            Log.Warning("[DecoderSession:{CameraId}] Process exited (process object null)", _cameraId);
            return;
        }

        int exitCode = -1;
        try { exitCode = _process.ExitCode; }
        catch (InvalidOperationException) { return; }

        if (exitCode == 0)
        {
            Log.Information("[DecoderSession:{CameraId}] Process exited code={ExitCode}",
                _cameraId, exitCode);
        }
        else
        {
            Log.Warning("[DecoderSession:{CameraId}] Process exited code={ExitCode}",
                _cameraId, exitCode);
            ErrorOccurred?.Invoke($"Decoder process exited with code {exitCode} (FFmpeg crash)");
        }
    }

    private void KillProcess()
    {
        try
        {
            _cts?.Cancel();
            if (_process is { HasExited: false })
            {
                SendCommand(DecoderMessageType.CmdExit, null);
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill();
                    _process.WaitForExit(1000);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DecoderSession:{CameraId}] KillProcess", _cameraId);
        }
        finally
        {
            lock (_pipeLock)
            {
                // Dispose pipe first to unblock ReadLoop, then join thread
                _pipeServer?.Dispose();
                _pipeServer = null;
                if (_readThread is { IsAlive: true })
                {
                    _readThread.Join(1000);
                }
                _readThread = null;
                _process?.Dispose();
                _process = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
