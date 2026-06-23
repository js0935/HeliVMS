using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace HeliVMS.Services;

public sealed partial class MotionAnalysisService(IVideoIndexService videoIndexService, IEventService eventService) : IMotionAnalysisService, IDisposable {
    private readonly IVideoIndexService _videoIndexService = videoIndexService;
    private readonly IEventService _eventService = eventService;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _disposed;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(120);

    private static readonly Regex SceneScoreRegex = MyRegex();

    public void StartMonitoring() {
        if (_monitorTask is not null) { return; }
        _cts = new CancellationTokenSource();
        _monitorTask = Task.Factory.StartNew(
            () => MonitorLoopAsync(_cts.Token),
            TaskCreationOptions.LongRunning).Unwrap();
        Log.Debug("[HeliVMS] MotionAnalysisService started");
    }

    public void StopMonitoring() {
        _cts?.Cancel();
        if (_monitorTask != null) {
            try { _monitorTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (Exception ex) { Log.Warning(ex, "[HeliVMS] MotionAnalysisService stop error"); }
            _monitorTask = null;
        }
        Log.Debug("[HeliVMS] MotionAnalysisService stopped");
    }

    public async Task StopMonitoringAsync() {
        _cts?.Cancel();
        if (_monitorTask != null) {
            try { await _monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { Log.Warning(ex, "[HeliVMS] MotionAnalysisService stop error"); }
            _monitorTask = null;
        }
        Log.Debug("[HeliVMS] MotionAnalysisService stopped");
    }

    private async Task MonitorLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                await AnalyzePendingSegmentsAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                Log.Warning(ex, "[HeliVMS] MotionAnalysisService loop error");
            }
        }
    }

    private async Task AnalyzePendingSegmentsAsync(CancellationToken ct) {
        var segments = await _videoIndexService.QueryUnanalyzedMotionSegmentsAsync(20)
            .ConfigureAwait(false);
        if (segments.Count == 0) { return; }

        Log.Debug("[HeliVMS] MotionAnalysis: analyzing {Count} segments", segments.Count);

        var ffmpeg = FFmpegBinariesHelper.FindFfmpegExecutable();

        foreach (var seg in segments) {
            if (ct.IsCancellationRequested) { break; }

            try {
                var score = await AnalyzeSingleSegmentAsync(ffmpeg, seg.FilePath, ct)
                    .ConfigureAwait(false);
                await _videoIndexService.UpdateMotionScoreAsync(seg.Id, score)
                    .ConfigureAwait(false);
            } catch (Exception ex) {
                Log.Warning(ex, "[HeliVMS] MotionAnalysis failed for {Path}", seg.FilePath);
                try {
                    await _videoIndexService.UpdateMotionScoreAsync(seg.Id, -1)
                        .ConfigureAwait(false);
                } catch { }
            }
        }
    }

    private static async Task<double> AnalyzeSingleSegmentAsync(string ffmpeg, string filePath, CancellationToken ct) {
        var args = $"-hide_banner -i \"{filePath}\" -vf \"select='gt(scene,0.4)',metadata=print\" -an -f null NUL";
        var psi = new ProcessStartInfo(ffmpeg, args) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var proc = Process.Start(psi);
        if (proc is null) { return 0; }

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var completed = await WaitForExitAsync(proc, (int)FfmpegTimeout.TotalMilliseconds, ct).ConfigureAwait(false);

        if (!completed) {
            try { proc.Kill(); } catch { }
            return -1;
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        var matches = SceneScoreRegex.Matches(stderr);
        if (matches.Count == 0) { return 0; }

        double maxScore = 0;
        foreach (Match m in matches) {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var score)) {
                if (score > maxScore) { maxScore = score; }
            }
        }

        return Math.Clamp(maxScore, 0, 1);
    }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            StopMonitoring();
            _cts?.Dispose();
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs, CancellationToken ct) {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    [GeneratedRegex(@"lavfi\.scene_score=([\d\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-TW")]
    private static partial Regex MyRegex();
}
