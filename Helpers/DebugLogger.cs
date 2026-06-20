using System.IO;
using System.Runtime.CompilerServices;
using Serilog;

namespace HeliVMS.Helpers;

/// <summary>
/// Structured debug log with unified format for analyzing crashes and instability.
/// All logs include {Category} and {Context} properties for Serilog filtering.
/// </summary>
public static class DebugLogger
{
    private static long _eventSeq;

    // ── 類別常數（grep 用關鍵字）──
    public const string CatRTSP = "RTSP";
    public const string CatReconnect = "RECONNECT";
    public const string CatWatchdog = "WATCHDOG";
    public const string CatNetwork = "NETWORK";
    public const string CatCamera = "CAMERA";
    public const string CatLiveView = "LIVEVIEW";
    public const string CatCameraGrid = "CAMGRID";
    public const string CatPTZ = "PTZ";
    public const string CatPlayback = "PLAYBACK";
    public const string CatStartup = "STARTUP";
    public const string CatShutdown = "SHUTDOWN";
    public const string CatAsync = "ASYNC";

    public static void Info(string category, string context, string message, Exception? ex = null,
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        if (ex is not null)
        {
            Log.Information(ex, "[{Seq}] [{Cat}] {Ctx}: {Msg} (src:{File}:{Line})",
                seq, category, context, message, Path.GetFileName(srcFile), srcLine);
        }
        else
        {
            Log.Information("[{Seq}] [{Cat}] {Ctx}: {Msg} (src:{File}:{Line})",
                seq, category, context, message, Path.GetFileName(srcFile), srcLine);
        }
    }

    public static void Warn(string category, string context, string message, Exception? ex = null,
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        if (ex is not null)
        {
            Log.Warning(ex, "[{Seq}] [{Cat}] {Ctx}: {Msg} (src:{File}:{Line})",
                seq, category, context, message, Path.GetFileName(srcFile), srcLine);
        }
        else
        {
            Log.Warning("[{Seq}] [{Cat}] {Ctx}: {Msg} (src:{File}:{Line})",
                seq, category, context, message, Path.GetFileName(srcFile), srcLine);
        }
    }

    public static void Error(string category, string context, string message, Exception? ex = null,
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        Log.Error(ex, "[{Seq}] [{Cat}] {Ctx}: {Msg} (src:{File}:{Line})",
            seq, category, context, message, Path.GetFileName(srcFile), srcLine);
    }

    /// <summary>Quick log for RTSP connection events</summary>
    public static void RtspEvent(string cameraName, string eventType, string url, string detail = "",
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        Log.Information("[{Seq}] [{Cat}] camera={Camera} event={Event} url={Url} {Detail} (src:{File}:{Line})",
            seq, CatRTSP, cameraName, eventType, url, detail, Path.GetFileName(srcFile), srcLine);
    }

    /// <summary>Quick log for reconnection events</summary>
    public static void ReconnectEvent(string cameraName, int attempt, int maxAttempts, int delayMs,
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        Log.Information("[{Seq}] [{Cat}] camera={Camera} attempt={Attempt}/{Max} delay={Delay}ms (src:{File}:{Line})",
            seq, CatReconnect, cameraName, attempt, maxAttempts, delayMs, Path.GetFileName(srcFile), srcLine);
    }

    /// <summary>Quick log for watchdog check results</summary>
    public static void WatchdogCheck(string cameraName, bool isPlaying, string detail,
        [CallerFilePath] string srcFile = "", [CallerLineNumber] int srcLine = 0)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        Log.Information("[{Seq}] [{Cat}] camera={Camera} playing={IsPlaying} {Detail} (src:{File}:{Line})",
            seq, CatWatchdog, cameraName, isPlaying, detail, Path.GetFileName(srcFile), srcLine);
    }
}
