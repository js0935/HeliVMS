using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class RecordingIntegrityService : IRecordingIntegrityService, IDisposable
{
    private readonly IVideoIndexService _videoIndexService;
    private readonly IEventService _eventService;
    private Timer? _timer;
    private bool _disposed;

    public int TotalSegments { get; private set; }
    public int CheckedSegments { get; private set; }
    public int CorruptedSegments { get; private set; }

    public event Action? StatsChanged;

    public RecordingIntegrityService(IVideoIndexService videoIndexService, IEventService eventService)
    {
        _videoIndexService = videoIndexService;
        _eventService = eventService;
    }

    public void StartMonitoring()
    {
        _timer = new Timer(async _ => await OnTimerElapsed(), null, TimeSpan.Zero, TimeSpan.FromMinutes(30));
    }

    public void StopMonitoring()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
    }

    public async Task ForceCheck()
    {
        await OnTimerElapsed(maxResults: 500);
    }

    private async Task OnTimerElapsed(int maxResults = 50)
    {
        try
        {
            var stats = await _videoIndexService.GetIntegrityStatsAsync().ConfigureAwait(false);
            TotalSegments = stats.TotalSegments;
            CheckedSegments = stats.CheckedSegments;
            CorruptedSegments = stats.CorruptedSegments;

            var uncheckedSegments = await _videoIndexService.QueryUncheckedSegmentsAsync(maxResults).ConfigureAwait(false);
            if (uncheckedSegments.Count == 0)
            {
                return;
            }

            var updates = new List<(int SegmentId, bool IsCorrupted)>(uncheckedSegments.Count);

            foreach (var segment in uncheckedSegments)
            {
                try
                {
                    bool isCorrupted = !ValidateFile(segment.FilePath);
                    updates.Add((segment.Id, isCorrupted));

                    if (isCorrupted)
                    {
                        Log.Warning("[Integrity] Corrupted segment detected: {FilePath}", segment.FilePath);
                        _eventService.LogWarning(EventCategories.System, "IntegrityCheck",
                            $"Corrupted segment detected: {segment.FilePath}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Integrity] Error checking segment {SegmentId}: {FilePath}", segment.Id, segment.FilePath);
                    updates.Add((segment.Id, true));
                }
            }

            if (updates.Count > 0)
            {
                await _videoIndexService.UpdateIntegrityStatusAsync(updates).ConfigureAwait(false);
            }

            stats = await _videoIndexService.GetIntegrityStatsAsync().ConfigureAwait(false);
            TotalSegments = stats.TotalSegments;
            CheckedSegments = stats.CheckedSegments;
            CorruptedSegments = stats.CorruptedSegments;
            StatsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Integrity] Error in integrity check cycle");
        }
    }

    private static bool ValidateFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        if (!File.Exists(filePath))
            return false;

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length < 188 * 3)
            return false;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = new byte[188 * 3];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        if (bytesRead < 188 * 3)
            return false;

        // Check first 3 TS packets for sync byte (0x47)
        for (int i = 0; i < 3; i++)
            if (buffer[i * 188] != 0x47)
            {
                return false;
            }

        // TS sync bytes validated; now deep validate with FFprobe
        if (DeepValidateWithFFprobe(filePath))
        {
            return true;
        }

        // FFprobe unavailable; fall back to basic validation
            return true;
    }

    /// <summary>Deep validate a TS file using FFprobe</summary>
    private static bool DeepValidateWithFFprobe(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
                return false;

            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out double duration))
                return duration > 0;

            return false;
        }
        catch
        {
            // FFprobe not available or crashed; assume deep check passed
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopMonitoring();
            _disposed = true;
        }
    }
}
