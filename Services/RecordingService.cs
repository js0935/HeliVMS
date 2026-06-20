using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Serilog;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeliVMS.Models;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Services;

public sealed class RecordingService : IRecordingService, IDisposable
{
    private readonly ConcurrentDictionary<string, RecordingSession> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, List<(int Id, string FilePath)>> _segmentIds = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _recordingWatchers = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _processedTsFiles = new();
    private readonly IEventService _eventLog;
    private readonly ISettingsService _settingsService;
    private readonly IVideoIndexService _videoIndexService;
    private string _basePath;
    private int _segmentLengthSeconds = 600;

    // Disk space protection
    private const long MinFreeSpaceBytes = 500L * 1024 * 1024;       // 500 MB
    private static readonly TimeSpan PurgeCheckInterval = TimeSpan.FromMinutes(5);
    private const double LowSpaceFreePercent = 10.0;                 // Triggers purge when free space <10%

    private readonly ConcurrentDictionary<string, FailedRecordingState> _failedAttempts = new();
    private Timer? _purgeTimer;

    private sealed record FailedRecordingState(int RetryCount, DateTime LastAttempt);

    public int SegmentLengthSeconds
    {
        get => _segmentLengthSeconds;
        set => _segmentLengthSeconds = value > 0 ? value : 600;
    }

    private static readonly string StorageConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "storage_locations.json");

    public RecordingService(IEventService eventLog, ISettingsService settingsService, IVideoIndexService videoIndexService)
    {
        _eventLog = eventLog;
        _settingsService = settingsService;
        _videoIndexService = videoIndexService;
        _basePath = LoadStoragePath();
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }

        CleanupEmptyRecordingDirs();

        // Start periodic disk space check (every 5 min), auto-purge below threshold
        _purgeTimer = new Timer(async _ => await CheckAndPurgeIfNeededAsync(), null, PurgeCheckInterval, PurgeCheckInterval);
    }

    private static string LoadStoragePath()
    {
        try
        {
            if (File.Exists(StorageConfigPath))
            {
                var json = File.ReadAllText(StorageConfigPath);
                var locations = JsonSerializer.Deserialize<List<StorageLocation>>(json);
                StorageLocation? active = null;
                if (locations is not null)
                {
                    for (int li = 0; li < locations.Count; li++)
                    {
                        var l = locations[li];
                        if (l.IsActive && !string.IsNullOrWhiteSpace(l.Path))
                        { active = l; break; }
                    }
                }
                if (active is not null)
                    return active.Path;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: failed to load storage config: {Msg}", ex.Message);
        }
        return @"D:\Recordings";
    }

    public string GetBasePath() => _basePath;

    public void SetBasePath(string path)
    {
        _basePath = path;
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    public bool IsRecording(string cameraId) =>
        _activeRecordings.TryGetValue(cameraId, out _);

    public bool StartRecording(Camera camera)
    {
        if (string.IsNullOrEmpty(camera.Id) || string.IsNullOrEmpty(camera.RtspUrl))
        {
            return false;
        }
        if (_activeRecordings.TryGetValue(camera.Id, out _))
        {
            return false;
        }

        // Disk space check — reject recording if below 500 MB
        if (!HasSufficientDiskSpace(_basePath, out var freeMB))
        {
            _eventLog.LogWarning(EventCategories.System, "RecordingService",
                $"磁碟空間不足，無法啟動錄影: {camera.Name}",
                $"剩餘 {freeMB:F0} MB / 下限 500 MB，路徑: {_basePath}");
            Log.Debug("[HeliVMS] RecordingService: disk full, rejected {Name} ({FreeMB:F0} MB free)", camera.Name, freeMB);
            RecordFailure(camera.Id);
            return false;
        }

        var dateDir = DateTime.Now.ToString("yyyy-MM-dd");
        var channelDir = camera.ChannelNumber.HasValue
            ? $"CH{camera.ChannelNumber.Value}"
            : SanitizeFileName(camera.Name);
        var dir = Path.Combine(_basePath, dateDir, channelDir);
        Directory.CreateDirectory(dir);

        var outputPattern = Path.Combine(dir, $"{camera.CameraId}_%Y%m%d_%H%M%S.ts");

        var ffmpegExe = FFmpegBinariesHelper.FindFfmpegExecutable();
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            Arguments = BuildFfmpegCommand(camera, _segmentLengthSeconds, outputPattern),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        Process? process;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();

            var cameraName = camera.Name;
            var stderrReader = process.StandardError;
            _ = Task.Factory.StartNew(
                () => ConsumeStream(stderrReader, cameraName),
                TaskCreationOptions.LongRunning).Unwrap();
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: failed to start ffmpeg for {Name}: {Msg}", camera.Name, ex.Message);
            return false;
        }

        var session = new RecordingSession
        {
            CameraId = camera.Id,
            CameraName = camera.Name,
            StartedAt = DateTime.Now,
            FilePath = dir,
            ProcessId = process.Id,
            Process = process
        };

        if (_activeRecordings.TryAdd(camera.Id, session))
        {
            _ = MonitorProcess(camera.Id, process);
            RecordingStatusChanged?.Invoke(camera.Id, true);
            _eventLog.LogInfo(EventCategories.Operation, "RecordingService", $"Started recording: {camera.Name}", $"Path: {dir}");
            Notify($"錄影開始: {camera.Name}", "INFO");

            var recordType = GetRecordTypeFromCamera(camera);

            try
            {
                var watcher = new FileSystemWatcher(dir, "*.ts")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 65536
                };
                watcher.Created += (_, e) =>
                    OnNewTsFileDetected(camera.Id, e.FullPath, session.StartedAt, recordType);
                _recordingWatchers[camera.Id] = watcher;
                _processedTsFiles[camera.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Debug("[HeliVMS] RecordingService: failed to start FSW for {Name}: {Msg}", camera.Name, ex.Message);
            }

            return true;
        }

        try { process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
        _eventLog.LogWarning(EventCategories.Operation, "RecordingService", $"Recording start failed: {camera.Name} (may already be recording)");
        return false;
    }

    public bool StopRecording(string cameraId)
    {
        if (!_activeRecordings.TryRemove(cameraId, out var session))
            return false;

        // Dispose FSW (no longer need to monitor new .ts files)
        DisposeWatcher(cameraId);

        var process = session.Process;
        if (process is not null)
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    session.Process = null;
                }
                else
                {
                    try
                    {
                        process.StandardInput.WriteLine("q");
                        process.StandardInput.Flush();

                        if (!process.WaitForExit(2000))
                        {
                            Log.Debug("[HeliVMS] RecordingService: ffmpeg did not exit gracefully, killing {CameraId}", cameraId);
                            try { process.Kill(); } catch { }
                            process.WaitForExit(2000);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("[HeliVMS] RecordingService: error stopping ffmpeg for {CameraId}: {Msg}", cameraId, ex.Message);
                        try { process.Kill(); } catch { }
                    }
                    finally
                    {
                        process.Dispose();
                        session.Process = null;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                session.Process = null;
            }
        }

        RecordingStatusChanged?.Invoke(cameraId, false);
        _eventLog.LogInfo(EventCategories.Operation, "RecordingService", $"Stopped recording: {session.CameraName}", $"Duration: {(DateTime.Now - session.StartedAt).TotalMinutes:F1} min");
        Notify($"錄影結束: {session.CameraName}", "INFO");

        // Finalize all segments asynchronously
        _ = FinalizeAllSegmentsAsync(cameraId, session.FilePath);

        return true;
    }

    public void StopAll(Action<string>? onProgress = null)
    {
        var ids = new List<string>(_activeRecordings.Count);
        foreach (var kv in _activeRecordings)
        {
            ids.Add(kv.Key);
        }
        if (ids.Count == 0)
        {
            onProgress?.Invoke("No active recordings to stop");
            return;
        }

        onProgress?.Invoke($"Stopping {ids.Count} ffmpeg recording processes...");
        Parallel.ForEach(ids, id =>
        {
            var cameraName = _activeRecordings.TryGetValue(id, out var s) ? s.CameraName : id;
            onProgress?.Invoke($"Stopping {cameraName}");
            StopRecording(id);
        });

        // Kill any lingering ffmpeg.exe processes
        KillAllFfmpegProcesses();
        onProgress?.Invoke("All ffmpeg processes terminated");
    }

    /// <summary>Kill all ffmpeg.exe processes owned by this application</summary>
    public static void KillAllFfmpegProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("ffmpeg");
            foreach (var proc in processes)
            {
                try
                {
                    proc.Kill();
                    if (!proc.WaitForExit(3000))
                    {
                        Log.Debug("[HeliVMS] KillAllFfmpegProcesses: timeout waiting for ffmpeg to exit");
                    }
                    proc.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug("[HeliVMS] KillAllFfmpegProcesses: error killing ffmpeg PID {Pid}: {Msg}", proc.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] KillAllFfmpegProcesses: enumeration error: {Msg}", ex.Message);
        }
    }

    public string? GetRecordingPath(string cameraId)
    {
        return _activeRecordings.TryGetValue(cameraId, out var session)
            ? session.FilePath
            : null;
    }

    public List<RecordingSession> GetActiveRecordings()
    {
        var list = new List<RecordingSession>(_activeRecordings.Count);
        foreach (var kv in _activeRecordings)
        {
            list.Add(kv.Value);
        }
        return list;
    }

    public event Action<string, bool>? RecordingStatusChanged;

    /// <summary>
    /// Migrate old directory structure CH{ch}/yyyy-MM-dd/ to new structure yyyy-MM-dd/CH{ch}/,
    /// and update file paths in the VideoIndex DB.
    /// </summary>
    public async Task<(int MovedDirs, int UpdatedSegments)> MigrateToDateFirstStructureAsync()
    {
        int movedDirs = 0;
        int updatedSegments = 0;

        var chDirs = Directory.GetDirectories(_basePath, "CH*");
        foreach (var chDir in chDirs)
        {
            var channelName = Path.GetFileName(chDir);

            foreach (var dateDir in Directory.GetDirectories(chDir))
            {
                var dateName = Path.GetFileName(dateDir);
                var newDir = Path.Combine(_basePath, dateName, channelName);

                if (Directory.Exists(newDir))
                {
                    Log.Debug("[HeliVMS] Migrate: target exists, merging {DateDir} -> {NewDir}", dateDir, newDir);
                    foreach (var file in Directory.GetFiles(dateDir))
                    {
                        var dest = Path.Combine(newDir, Path.GetFileName(file));
                        if (!File.Exists(dest))
                            File.Move(file, dest);
                        else
                            File.Delete(file);
                    }
                    Directory.Delete(dateDir);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
                    Directory.Move(dateDir, newDir);
                }
                movedDirs++;

                var updated = await _videoIndexService.BulkUpdateFilePathPrefixAsync(dateDir, newDir).ConfigureAwait(false);
                updatedSegments += updated;
            }

            if (Directory.Exists(chDir) && Directory.GetFileSystemEntries(chDir).Length == 0)
                Directory.Delete(chDir);
        }

        return (movedDirs, updatedSegments);
    }

    // ── Disk Space Check ──────────────────────────────────────

    private static bool HasSufficientDiskSpace(string path, out double freeMB)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null) { freeMB = 0; return false; }
            var drive = new DriveInfo(root);
            if (!drive.IsReady) { freeMB = 0; return false; }
            freeMB = drive.AvailableFreeSpace / (1024.0 * 1024.0);
            return drive.AvailableFreeSpace >= MinFreeSpaceBytes;
        }
        catch
        {
            freeMB = 0;
            return false;
        }
    }

    public bool IsDiskLowSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_basePath);
            if (root is null) return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return false;
            double pct = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;
            return pct < LowSpaceFreePercent;
        }
        catch { return false; }
    }

    public async Task CheckAndPurgeIfNeededAsync()
    {
        try
        {
            if (!IsDiskLowSpace()) return;

            var settings = _settingsService.Settings;
            if (!settings.EnableAutoPurge) return;

            _eventLog.LogWarning(EventCategories.System, "RecordingService",
                "磁碟空間不足，自動執行保留政策清除",
                $"路徑: {_basePath}，剩餘 < {LowSpaceFreePercent:F0}%");

            var (del, freed) = await _videoIndexService.PurgeByRetentionPolicyAsync(
                settings.RetentionDays, settings.MaxStorageGB, _basePath).ConfigureAwait(false);

            if (del > 0)
            {
                _eventLog.LogInfo(EventCategories.System, "RecordingService",
                    $"自動清除完成: 刪除 {del} 個錄影檔，釋放 {freed / (1024.0 * 1024.0):F1} MB");
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: CheckAndPurgeIfNeededAsync error: {Msg}", ex.Message);
        }
    }

    // ── Backoff Mechanism ────────────────────────────────────

    private static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(30);

    private void RecordFailure(string cameraId)
    {
        _failedAttempts.AddOrUpdate(cameraId,
            _ => new FailedRecordingState(1, DateTime.UtcNow),
            (_, state) => state with { RetryCount = state.RetryCount + 1, LastAttempt = DateTime.UtcNow });
    }

    public void ResetBackoff(string cameraId)
    {
        _failedAttempts.TryRemove(cameraId, out _);
    }

    public bool IsInBackoff(string cameraId)
    {
        if (!_failedAttempts.TryGetValue(cameraId, out var state))
            return false;

        var elapsed = DateTime.UtcNow - state.LastAttempt;
        return elapsed < GetBackoffDelay(state.RetryCount);
    }

    public TimeSpan GetBackoffDelay(string cameraId)
    {
        if (!_failedAttempts.TryGetValue(cameraId, out var state))
            return TimeSpan.Zero;
        return GetBackoffDelay(state.RetryCount);
    }

    private static TimeSpan GetBackoffDelay(int retryCount)
    {
        // 30s, 60s, 120s, 240s, cap at 300s (5 min)
        var delay = TimeSpan.FromSeconds(BackoffBase.TotalSeconds * Math.Pow(2, retryCount - 1));
        return delay > BackoffMax ? BackoffMax : delay;
    }

    // ── Cleanup ──────────────────────────────────────────────

    /// <summary>
    /// Startup cleanup: remove orphaned named folders (no CH number) and empty CH folders
    /// </summary>
    public void CleanupEmptyRecordingDirs()
    {
        try
        {
            if (!Directory.Exists(_basePath)) return;

            var dateDirs = Directory.GetDirectories(_basePath);

            // Get all camera names with CH numbers for identifying orphaned folders
            var camerasWithCh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dateDir in dateDirs)
            {
                var chDirs = Directory.GetDirectories(dateDir);
                foreach (var chDir in chDirs)
                {
                    var dirName = Path.GetFileName(chDir);
                    // CH{number} folder
                    if (dirName.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
                    {
                        // Delete if empty
                        if (Directory.GetFileSystemEntries(chDir).Length == 0)
                        {
                            try { Directory.Delete(chDir); } catch { }
                        }
                    }
                    else
                    {
                        // Named folder (e.g. VIVOTEK IP9167-HP) — no CH number, orphaned
                        if (Directory.GetFileSystemEntries(chDir).Length == 0)
                        {
                            try { Directory.Delete(chDir); } catch { }
                        }
                    }
                }

                // If date folder has no subfolders, delete it
                if (Directory.GetDirectories(dateDir).Length == 0
                    && Directory.GetFiles(dateDir).Length == 0)
                {
                    try { Directory.Delete(dateDir); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: CleanupEmptyRecordingDirs error: {Msg}", ex.Message);
        }
    }

    public void Dispose()
    {
        _purgeTimer?.Dispose();
        try { StopAll(); }
        catch (Exception ex) { Log.Warning(ex, "[HeliVMS] RecordingService Dispose StopAll error"); }
    }

    private static void Notify(string message, string severity = "WARN")
    {
        try { App.Services.GetRequiredService<INotificationService>().Show(message, severity); }
        catch { }
    }

    private async Task MonitorProcess(string cameraId, Process process)
    {
        try
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Process already disposed — expected during normal shutdown
            }

            DisposeWatcher(cameraId);

            if (!_activeRecordings.TryRemove(cameraId, out var session))
                return; // Already removed by StopRecording

            var crashed = false;
            int? exitCode = null;
            try
            {
                crashed = process.HasExited && process.ExitCode != 0;
                exitCode = crashed ? process.ExitCode : null;
            }
            catch
            {
                crashed = true; // Process disposed before check — treat as crash
            }

            try { process.Dispose(); } catch { }
            try { RecordingStatusChanged?.Invoke(cameraId, false); } catch { }

            if (crashed)
            {
                _eventLog.LogWarning(EventCategories.System, "RecordingService",
                    $"錄影異常終止: {session.CameraName}",
                    $"ExitCode={exitCode}，ffmpeg 可能因磁碟空間不足或串流中斷而崩潰");
                RecordFailure(cameraId);
            }

            await FinalizeAllSegmentsAsync(cameraId, session.FilePath);
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: MonitorProcess unexpected error for {CameraId}: {Msg}", cameraId, ex.Message);
        }
    }

    private static string BuildRtspUrl(Camera camera)
    {
        if (string.IsNullOrEmpty(camera.Username) || string.IsNullOrEmpty(camera.Password))
            return camera.RtspUrl;

        var uri = new Uri(camera.RtspUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        var query = uri.Query;

        // Fix known-wrong VIVOTEK paths: generic "live" should be "live1s1.sdp"
        var mfr = camera.Manufacturer?.ToLowerInvariant() ?? "";
        if (mfr.Contains("vivotek"))
        {
            if (path == "/live" || path == "")
                path = "/live1s1.sdp";
            else if (path == "/live2")
                path = "/live1s2.sdp";
        }

        var userInfo = $"{Uri.EscapeDataString(camera.Username)}:{Uri.EscapeDataString(camera.Password)}";
        return $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{path}{query}";
    }

    private string BuildFfmpegCommand(Camera camera, int segmentSeconds, string outputPattern)
    {
        var rtsp = BuildRtspUrl(camera);
        var fontFile = "C\\:/Windows/Fonts/arial.ttf";
        var fontSize = _settingsService.Settings.OverlayFontSize;

        var filters = new List<string>(2);

        if (_settingsService.Settings.EnableCameraNameOverlay)
        {
            var safeName = SanitizeDrawText(camera.Name);
            filters.Add($"drawtext=text='{safeName}':fontfile='{fontFile}':x=10:y=10:fontsize={fontSize}:fontcolor=white:box=1:boxcolor=black@0.5");
        }

        if (_settingsService.Settings.EnableTimestampOverlay)
        {
            // %F = %Y-%m-%d, %T = %H:%M:%S (single strftime format avoids drawtext %{} parser conflict)
            filters.Add($"drawtext=text='%{{localtime\\:%F %T}}':fontfile='{fontFile}':x=w-text_w-10:y=10:fontsize={fontSize}:fontcolor=white:box=1:boxcolor=black@0.5");
        }

        var vf = filters.Count > 0 ? $"-vf \"{string.Join(",", filters)}\" " : "-c:v copy ";

        return $"-hide_banner -loglevel error " +
               $"-rtsp_transport tcp -use_wallclock_as_timestamps 1 -i \"{rtsp}\" " +
               (filters.Count > 0
                   ? $"-c:v libx264 -preset ultrafast -crf 28 -tune zerolatency {vf}-g 30 "
                   : $"{vf}") +
               $"-an -f segment -segment_time {segmentSeconds} " +
               $"-segment_atclocktime 1 -strftime 1 -reset_timestamps 1 " +
               $"\"{outputPattern}\"";
    }

    /// <summary>Escape special chars (: \) in drawtext text filter</summary>
    private static string SanitizeDrawText(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace(":", "\\:");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }

    private static readonly Regex TsSegmentPattern = new(@"\.ts$", RegexOptions.Compiled);

    private async Task AddIndexSegmentAsync(string cameraId, VideoSegment segment)
    {
        try
        {
            await _videoIndexService.AddSegmentAsync(segment).ConfigureAwait(false);
            var list = _segmentIds.GetOrAdd(cameraId, _ => new List<(int, string)>());
            lock (list)
                list.Add((segment.Id, segment.FilePath));
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndex: failed to add segment for {CameraId}: {Msg}", cameraId, ex.Message);
        }
    }

    private void OnNewTsFileDetected(string cameraId, string fullPath, DateTime sessionStart, int recordType)
    {
        try
        {
            var fileName = Path.GetFileName(fullPath);
            var processed = _processedTsFiles.GetOrAdd(cameraId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            lock (processed)
            {
                if (!processed.Add(fileName))
                    return;
            }

            var match = TsSegmentPattern.Match(fileName);
            if (!match.Success) return;

            // Use actual file last write time instead of formula estimate, applies to all segments (incl. 0/1)
            var fileTime = File.GetLastWriteTime(fullPath);
            var segmentStart = fileTime.AddSeconds(-_segmentLengthSeconds);
            if (segmentStart < sessionStart)
                segmentStart = sessionStart;

            var segment = new VideoSegment
            {
                CameraId = cameraId,
                StartTime = segmentStart,
                EndTime = fileTime,
                FilePath = fullPath,
                RecordType = recordType,
            };
            _ = AddIndexSegmentAsync(cameraId, segment);

            Log.Debug("[HeliVMS] RecordingService: indexed segment from file {FileName} ({SegmentStart:HH:mm:ss} ~ {FileTime:HH:mm:ss})", fileName, segmentStart, fileTime);
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] RecordingService: error processing new .ts file for {CameraId}: {Msg}", cameraId, ex.Message);
        }
    }

    private void DisposeWatcher(string cameraId)
    {
        if (_recordingWatchers.TryRemove(cameraId, out var watcher))
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Dispose may throw — safe to ignore
            }
        }
        _processedTsFiles.TryRemove(cameraId, out _);
    }

    private async Task FinalizeAllSegmentsAsync(string cameraId, string dirPath)
    {
        try
        {
            if (_segmentIds.TryRemove(cameraId, out var entries))
            {
                var now = DateTime.Now;
                int kept = 0, deleted = 0;

                foreach (var (segId, filePath) in entries)
                {
                    var hasRealFile = !string.IsNullOrEmpty(filePath)
                                   && File.Exists(filePath)
                                   && filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

                    if (hasRealFile)
                    {
                        ResetBackoff(cameraId);
                        await _videoIndexService.UpdateEndTimeAsync(segId, now, 0).ConfigureAwait(false);
                        kept++;
                    }
                    else
                    {
                        await _videoIndexService.DeleteSegmentAsync(segId).ConfigureAwait(false);
                        deleted++;
                    }
                }

                if (deleted > 0)
                    Log.Debug("[HeliVMS] VideoIndex: finalized {Kept} segments, cleaned {Deleted} orphans for {CameraId}", kept, deleted, cameraId);

                // If no successful recordings, delete the empty directory
                if (kept == 0 && !string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dirPath).Length == 0)
                            Directory.Delete(dirPath);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] VideoIndex: failed to finalize segments for {CameraId}: {Msg}", cameraId, ex.Message);
        }
    }


    /// <summary>Map camera schedule mode to RecordType: 0=Motion, 1=Scheduled, 2=AI Event</summary>
    private static int GetRecordTypeFromCamera(Camera camera)
    {
        var config = CameraRecordingConfigData.Deserialize(camera.RecordingConfigJson);
        if (config is null) return 0;
        var mode = config.GetCurrentMode(DateTime.Now);
        return mode switch
        {
            ScheduleMode.Motion or ScheduleMode.Weighted => 1,
            ScheduleMode.Alarm or ScheduleMode.Smart => 2,
            _ => 0,
        };
    }

    /// <summary>Consume stderr asynchronously to prevent .NET Process buffer deadlock</summary>
    private static async Task ConsumeStream(StreamReader reader, string cameraName)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                Log.Debug("[HeliVMS][ffmpeg:{Name}] {Line}", cameraName, line);
            }
        }
        catch (ObjectDisposedException)
        {
            // Process was disposed, stream closed — expected
        }
        catch (InvalidOperationException)
        {
            // Stream not readable — process exited before read started
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS][ffmpeg:{Name}] stderr read error: {Msg}", cameraName, ex.Message);
        }
    }
}

