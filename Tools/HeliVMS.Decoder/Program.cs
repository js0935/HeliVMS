// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================
//
// HeliVMS.Decoder — 獨立解碼處理序
// 透過 Named Pipe 與主處理序通訊，將 FFmpeg native crash 隔離於子處理序內
//

using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using HeliVMS.Decoder;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "decoder-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 3,
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message}{NewLine}{Exception}")
    .CreateLogger();

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "CRASH: Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
    Log.CloseAndFlushAsync().GetAwaiter().GetResult();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Error(e.Exception, "CRASH: Unobserved task exception");
    e.SetObserved();
    Log.CloseAndFlushAsync().GetAwaiter().GetResult();
};

try
{
    var pipeName = ParseArg(args, "--pipe");
    var ffmpegPath = ParseArg(args, "--ffmpeg");
    var cameraId = ParseArg(args, "--camera");
    var hwaccel = ParseArg(args, "--hwaccel");

    if (pipeName == null || ffmpegPath == null || cameraId == null)
    {
        var err = "Missing required arguments: --pipe <name> --ffmpeg <path> --camera <id>";
        Log.Error(err);
        Console.Error.WriteLine(err);
        Console.Error.WriteLine("Usage: HeliVMS.Decoder --pipe <name> --ffmpeg <path> --camera <id> [--hwaccel auto|d3d11va|cuda|qsv|dxva2|none]");
        return 1;
    }

    Log.Information("Decoder starting: camera={CameraId}, pipe={PipeName}, ffmpeg={FfmpegPath}",
        cameraId, pipeName, ffmpegPath);

    if (!Directory.Exists(ffmpegPath))
    {
        var err = $"FFmpeg directory not found: {ffmpegPath}";
        Log.Error(err);
        Console.Error.WriteLine(err);
        return 1;
    }
    DynamicallyLoadedBindings.LibrariesPath = ffmpegPath;
    DynamicallyLoadedBindings.Initialize();
    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

    // 連線到主處理序的 Named Pipe
    using var pipeClient = new NamedPipeClientStream(".", pipeName,
        PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        pipeClient.Connect(TimeSpan.FromSeconds(10));
        Log.Information("Connected to pipe: {PipeName}", pipeName);
    }
    catch (Exception ex)
    {
        var err = $"Failed to connect to pipe {pipeName}: {ex.Message}";
        Log.Error(ex, "Failed to connect to pipe {PipeName}", pipeName);
        Console.Error.WriteLine(err);
        return 1;
    }

    using var reader = new BinaryReader(pipeClient, Encoding.UTF8, leaveOpen: true);
    using var writer = new BinaryWriter(pipeClient, Encoding.UTF8, leaveOpen: true);
    using var decodeService = new FileDecodeService(cameraId);

    // Configure HW acceleration
    var hwType = ParseHwAccel(hwaccel);
    if (hwType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE || IsAutoDetect(hwaccel))
    {
        if (IsAutoDetect(hwaccel))
        {
            decodeService.SetHwAccel(AutoDetectHwAccel());
        }
        else
        {
            decodeService.SetHwAccel(hwType);
        }
        if (hwaccel != null)
            Log.Information("HW acceleration configured: {Type}", hwaccel);
    }

    var pipeClosed = false;

    // 訂閱 decode service 事件 → 序列化為 pipe message
    decodeService.FrameDecoded += (pts, width, height, data, dataSize) =>
    {
        if (pipeClosed) return;
        lock (writer)
        {
            try
            {
                // Binary frame info header (zero allocation — no JSON)
                var header = new FrameInfoHeader(pts, width, height, dataSize);
                var headerBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<FrameInfoHeader>(in header));
                writer.Write((int)DecoderMessageType.EvtFrameInfo);
                writer.Write(headerBytes.Length);
                writer.Write(headerBytes);

                // Frame pixel data — only actual frame bytes, not pooled buffer capacity
                writer.Write((int)DecoderMessageType.EvtFrameData);
                writer.Write(dataSize);
                writer.Write(data, 0, dataSize);
                writer.Flush();
            }
            catch (Exception ex)
            {
                pipeClosed = true;
                Log.Warning(ex, "Failed to send frame for {CameraId}", cameraId);
            }
        }
    };

    decodeService.PositionChanged += (pts, duration) =>
    {
        if (pipeClosed) return;
        Log.Debug("[Position] Sending pts={Pts}us dur={Dur}us", pts, duration);
        lock (writer)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new PositionPayload
                {
                    Pts = pts,
                    Duration = duration
                }, DecoderProtocolContext.Default.PositionPayload);
                writer.Write((int)DecoderMessageType.EvtPosition);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Flush();
            }
            catch (Exception ex)
            {
                pipeClosed = true;
                Log.Warning(ex, "[Position] Failed to write position to pipe");
            }
        }
    };

    decodeService.StatusChanged += (playing) =>
    {
        if (pipeClosed) return;
        lock (writer)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new StatusPayload { Playing = playing }, DecoderProtocolContext.Default.StatusPayload);
                writer.Write((int)DecoderMessageType.EvtStatus);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Flush();
            }
            catch
            {
                pipeClosed = true;
            }
        }
    };

    decodeService.EofReached += () =>
    {
        if (pipeClosed) return;
        lock (writer)
        {
            try
            {
                writer.Write((int)DecoderMessageType.EvtEof);
                writer.Write(0); // no payload
                writer.Flush();
            }
            catch
            {
                pipeClosed = true;
            }
        }
    };

    decodeService.ErrorOccurred += (msg) =>
    {
        if (pipeClosed) return;
        lock (writer)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new ErrorPayload { Message = msg }, DecoderProtocolContext.Default.ErrorPayload);
                writer.Write((int)DecoderMessageType.EvtError);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Flush();
            }
            catch
            {
                pipeClosed = true;
            }
        }
    };

    // 主命令迴圈：讀取主處理序命令
    var commandBuffer = new byte[4];
    Log.Information("Decoder ready, waiting for commands");

    while (pipeClient.IsConnected)
    {
        int bytesRead;
        try
        {
            bytesRead = await pipeClient.ReadAsync(commandBuffer.AsMemory(0, 4));
        }
        catch (IOException)
        {
            break; // pipe closed
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        if (bytesRead < 4) break;

        var msgType = (DecoderMessageType)BitConverter.ToInt32(commandBuffer, 0);

        // Read payload length
        var lenBuf = new byte[4];
        var lenRead = await pipeClient.ReadAsync(lenBuf.AsMemory(0, 4));
        if (lenRead < 4) break;
        var payloadLen = BitConverter.ToInt32(lenBuf, 0);

        byte[]? payload = null;
        if (payloadLen > 0)
        {
            payload = new byte[payloadLen];
            int totalRead = 0;
            while (totalRead < payloadLen)
            {
                var chunk = await pipeClient.ReadAsync(payload.AsMemory(totalRead, payloadLen - totalRead));
                if (chunk <= 0) break;
                totalRead += chunk;
            }
            if (totalRead < payloadLen) break;
        }

        switch (msgType)
        {
            case DecoderMessageType.CmdOpen:
                var openCmd = JsonSerializer.Deserialize(payload!.AsSpan(), DecoderProtocolContext.Default.OpenPayload)!;
                Log.Information("CmdOpen: {FilePath} seek={SeekUs} targetH={TargetH}",
                    openCmd.FilePath, openCmd.SeekMicroseconds, openCmd.TargetDecodeHeight);
                decodeService.Open(openCmd.FilePath, openCmd.TargetDecodeHeight);
                if (openCmd.SeekMicroseconds > 0)
                    decodeService.Seek(openCmd.SeekMicroseconds);
                break;

            case DecoderMessageType.CmdSeek:
                var seekCmd = JsonSerializer.Deserialize(payload!.AsSpan(), DecoderProtocolContext.Default.SeekPayload)!;
                decodeService.Seek(seekCmd.Microseconds);
                break;

            case DecoderMessageType.CmdSetRate:
                var ratePayload = JsonSerializer.Deserialize(payload!.AsSpan(), DecoderProtocolContext.Default.RatePayload)!;
                decodeService.SetPlaybackRate(ratePayload.Rate);
                break;

            case DecoderMessageType.CmdSetTargetFps:
                var fpsPayload = JsonSerializer.Deserialize(payload!.AsSpan(), DecoderProtocolContext.Default.FpsPayload)!;
                decodeService.SetTargetFps(fpsPayload.Fps);
                break;

            case DecoderMessageType.CmdPause:
                decodeService.Pause();
                break;

            case DecoderMessageType.CmdResume:
                decodeService.Resume();
                break;

            case DecoderMessageType.CmdStop:
                Log.Information("CmdStop");
                decodeService.Stop();
                break;

            case DecoderMessageType.CmdExit:
                Log.Information("CmdExit");
                decodeService.Stop();
                return 0;
        }
    }

    Log.Information("Decoder pipe disconnected, shutting down");
    try { decodeService.Stop(); } catch (Exception ex) { Log.Warning(ex, "Stop error during shutdown"); }
    return 0;
}
catch (Exception ex)
{
    Log.Error(ex, "Decoder fatal error");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string? ParseArg(string[] args, string key)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static bool IsAutoDetect(string? value) =>
    string.IsNullOrEmpty(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);

static AVHWDeviceType ParseHwAccel(string? value) => (value?.ToLowerInvariant()) switch
{
    "d3d11va" => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
    "cuda" => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
    "qsv" => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
    "dxva2" => AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
    "amf" => AVHWDeviceType.AV_HWDEVICE_TYPE_AMF,
    "vulkan" => AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,
    "d3d12va" => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA,
    "videotoolbox" => AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
    _ => AVHWDeviceType.AV_HWDEVICE_TYPE_NONE,
};

/// <summary>Auto-detect best available HW decoder: D3D11VA &gt; CUDA &gt; QSV &gt; DXVA2 &gt; software fallback</summary>
static AVHWDeviceType AutoDetectHwAccel()
{
    // Collect available HW device types from FFmpeg
    var available = new HashSet<AVHWDeviceType>();
    var t = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    while ((t = ffmpeg.av_hwdevice_iterate_types(t)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        _ = available.Add(t);

    var preferred = new[]
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
    };

    foreach (var type in preferred)
    {
        if (available.Contains(type))
        {
            Log.Information("Auto-detected HW acceleration: {Type}", ffmpeg.av_hwdevice_get_type_name(type));
            return type;
        }
    }

    Log.Information("No HW acceleration available, using software decode");
    return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
}
