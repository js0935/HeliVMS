using System.IO;
using System.Runtime;
using System.Threading.Tasks;
using System.Windows;
using FlyleafLib;
using HeliVMS.Controls;
using HeliVMS.Services;
using HeliVMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Text;

namespace HeliVMS;

public partial class App : Application {
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Safely execute async void event handler, catch unhandled exceptions</summary>
    public static async void SafeFireAndForget(Func<Task> asyncAction, string context = "") {
        try {
            await asyncAction();
        } catch (Exception ex) {
            Log.Error(ex, "[HeliVMS] Unhandled exception in async event handler: {Context}", context);
        }
    }

    private void Application_Startup(object sender, StartupEventArgs e) {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Tune ThreadPool for 64-channel concurrent workloads
        var cpu = Environment.ProcessorCount;
        ThreadPool.SetMinThreads(Math.Max(cpu * 2, 64), Math.Max(cpu, 32));
        ThreadPool.SetMaxThreads(Math.Max(cpu * 4, 128), Math.Max(cpu * 2, 64));

        // Set GC modes for large-64-camera deployment: compact LOH, low-latency during startup
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "helivms-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                buffered: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}",
                encoding: System.Text.Encoding.UTF8)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "AppDomain unhandled exception. Terminating: {IsTerminating}", args.IsTerminating);
        };
        DispatcherUnhandledException += (_, args) => {
            Log.Error(args.Exception, "Dispatcher unhandled exception, Handled={Handled}", args.Handled);
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Log.Error("Runtime info — BaseDir: {Base}, Data/: {Data}, FFmpeg/: {Ffmpeg}",
                baseDir,
                Directory.Exists(Path.Combine(baseDir, "Data")),
                Directory.Exists(Path.Combine(baseDir, "FFmpeg")));
            // Only mark handled for non-fatal exceptions; let fatal ones crash for crash dump
            // but for now keep handled to avoid silent termination of "usable" windows
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => {
            Log.Error(args.Exception, "Task unobserved exception");
        };

        Log.Information("HeliVMS starting...");
        Log.Information("[TIMING] Step 1 — Log init: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // Build DI container
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        Log.Information("[TIMING] Step 2 — DI BuildServiceProvider: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // === Pre-flight checks for common startup failures ===
        try {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "Data");
            if (!Directory.Exists(dataDir)) {
                Directory.CreateDirectory(dataDir);
                Log.Information("Created Data directory: {DataDir}", dataDir);
            }
            var ffmpegDir = Path.Combine(baseDir, "FFmpeg");
            if (!Directory.Exists(ffmpegDir)) {
                Log.Warning("FFmpeg directory not found at {FfmpegDir} — decoding will fail until installed", ffmpegDir);
            }
        } catch (Exception ex) {
            Log.Warning(ex, "Pre-flight check failed (non-fatal)");
        }

        // === Show main window first, avoid blocking on background init ===
        MainWindow? mainWindow = null;
        try {
            mainWindow = new MainWindow();
            Log.Information("[TIMING] Step 3 — MainWindow constructed: {ElapsedMs}ms", sw.ElapsedMilliseconds);
            mainWindow.Show();
            Log.Information("[TIMING] Step 4 — MainWindow shown: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        } catch (Exception ex) {
            Log.Fatal(ex, "MainWindow creation failed — showing startup error dialog");
            var sb = new StringBuilder();
            sb.AppendLine("HeliVMS 啟動時發生未處理的例外狀況：");
            sb.AppendLine();
            sb.AppendLine($"例外類型：{ex.GetType().FullName}");
            sb.AppendLine($"訊息：{ex.Message}");
            sb.AppendLine();
            if (ex.InnerException is not null) {
                sb.AppendLine($"內部例外：{ex.InnerException.GetType().FullName}");
                sb.AppendLine($"內部訊息：{ex.InnerException.Message}");
                sb.AppendLine();
            }
            sb.AppendLine("呼叫堆疊：");
            sb.AppendLine(ex.StackTrace);
            sb.AppendLine();
            sb.AppendLine("--- 載入的組件 ---");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName)) {
                try {
                    sb.AppendLine($"  {asm.GetName().Name} v{asm.GetName().Version}");
                } catch { }
            }

            var errorWindow = new StartupErrorWindow(sb.ToString());
            errorWindow.ShowDialog();
            // If we reach here, the user closed the error dialog → terminate
            Application.Current.Shutdown();
            return;
        }

        // === Initialize Flyleaf media engine ===
        InitializeFlyleafEngine();
        Log.Information("[TIMING] Step 4a — Flyleaf init: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // === Remaining services initialized in background, non-blocking ===
        _ = StartBackgroundServicesAsync();
        Log.Information("[TIMING] Step 5 — Startup complete: {ElapsedMs}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>Run startup initialization in background, non-blocking UI thread</summary>
    private static async Task StartBackgroundServicesAsync() {
        // Start camera health monitoring
        try {
            var healthService = Services.GetRequiredService<ICameraHealthService>();
            healthService.StartMonitoring();
        } catch (Exception ex) { Log.Warning(ex, "Camera health service start failed"); }

        // Start recording integrity check
        try {
            var integrityService = Services.GetRequiredService<IRecordingIntegrityService>();
            integrityService.StartMonitoring();
        } catch (Exception ex) { Log.Warning(ex, "Recording integrity service start failed"); }

        // Start motion detection analysis service
        try {
            var motionService = Services.GetRequiredService<IMotionAnalysisService>();
            motionService.StartMonitoring();
        } catch (Exception ex) { Log.Warning(ex, "Motion analysis service start failed"); }

        // Log system startup event
        try {
            var eventService = Services.GetRequiredService<IEventService>();
            eventService.LogInfo("系統", "HeliVMS", "系統啟動完成");
        } catch { }

        // Start recording scheduler
        try {
            var scheduler = Services.GetRequiredService<RecordingSchedulerService>();
            scheduler.Start();
            Log.Information("Recording scheduler started");
        } catch (Exception ex) {
            Log.Error(ex, "Failed to start recording scheduler");
        }

        // Initialize video index database (SQLite + composite indices)
        IVideoIndexService? indexService = null;
        try {
            indexService = Services.GetRequiredService<IVideoIndexService>();
            await indexService.EnsureDatabaseCreatedAsync().ConfigureAwait(false);
            Log.Information("Video index database initialized");
        } catch (Exception ex) {
            Log.Warning(ex, "Video index database initialization failed (non-fatal)");
        }

        // Auto-purge recordings at startup based on retention policy
        if (indexService is not null) {
            try {
                var settings = Services.GetRequiredService<ISettingsService>();
                var recordingService = Services.GetRequiredService<IRecordingService>();
                if (settings.Settings.EnableAutoPurge) {
                    var (del, freed) = await indexService.PurgeByRetentionPolicyAsync(
                        settings.Settings.RetentionDays,
                        settings.Settings.MaxStorageGB,
                        recordingService.GetBasePath()
                    ).ConfigureAwait(false);
                    if (del > 0) {
                        Log.Information("Startup auto-purge: deleted {Count} segments, freed {FreedMB:F1} MB", del, freed / (1024.0 * 1024.0));
                    }
                }
            } catch (Exception ex) {
                Log.Warning(ex, "Startup auto-purge failed (non-fatal)");
            }
        }

        // Migrate old directory structure CH{ch}/yyyy-MM-dd/ → yyyy-MM-dd/CH{ch}/
        try {
            var recordingService2 = Services.GetRequiredService<IRecordingService>();
            var (moved, updated) = await recordingService2.MigrateToDateFirstStructureAsync()
                .ConfigureAwait(false);
            if (moved > 0) {
                Log.Information("Directory structure migration: moved {Moved} dirs, updated {Updated} DB paths", moved, updated);
            }
        } catch (Exception ex) {
            Log.Warning(ex, "Directory structure migration failed (non-fatal)");
        }

        // FFmpeg engine will be lazily initialized on first use

        // Start MediaMTX media relay
        try {
            var mtx = Services.GetRequiredService<MediaMTXService>();
            mtx.Start();
            Log.Information("MediaMTX relay started");
        } catch (Exception ex) {
            Log.Warning(ex, "MediaMTX relay start failed (non-fatal)");
        }

        // Migrate camera settings from old project
        TryMigrateCameras();
    }

    private static void InitializeFlyleafEngine() {
        var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");
        if (!Directory.Exists(ffmpegPath)) {
            Log.Warning("Flyleaf FFmpeg directory not found at {Path} — Flyleaf disabled", ffmpegPath);
            return;
        }

        // Quick check that key FFmpeg DLLs exist
        var avCodecPath = Path.Combine(ffmpegPath, "avcodec-62.dll");
        if (!File.Exists(avCodecPath)) {
            Log.Warning("Core FFmpeg DLL missing ({Dll}) — Flyleaf disabled", avCodecPath);
            return;
        }

        try {
            Engine.Start(new EngineConfig {
                FFmpegPath = ffmpegPath,
                DisableAudio = true,
                UIRefresh = false,
                UIRefreshInterval = 250,
            });
            VideoPlayer.FlyleafEngineReady = true;
            Log.Information("Flyleaf Engine initialized (FFmpeg: {Path})", ffmpegPath);
        } catch (Exception ex) {
            Log.Error(ex, "Failed to initialize Flyleaf Engine — falling back to in-process decoder");
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e) {
        Log.Information("HeliVMS shutting down...");

        try {
            var scheduler = Services.GetRequiredService<RecordingSchedulerService>();
            scheduler.Stop();
            Log.Information("Recording scheduler stopped");
        } catch (Exception ex) {
            Log.Error(ex, "Error stopping recording scheduler during shutdown");
        }

        // Safety net: ensure all ffmpeg.exe processes are terminated
        RecordingService.KillAllFfmpegProcesses();

        // Stop MediaMTX media relay
        try {
            var mtx = Services.GetRequiredService<MediaMTXService>();
            mtx.Stop();
            Log.Information("MediaMTX relay stopped");
        } catch (Exception ex) {
            Log.Warning(ex, "Error stopping MediaMTX relay");
        }

        // Stop all decoder processes
        VideoPlayer.CleanupAllDecoderSessions();
        VideoPlayer.CleanupAllFlyleafPlayers();

        Log.Information("HeliVMS shutdown complete");
        Log.CloseAndFlush();
    }

    private static void ConfigureServices(IServiceCollection services) {
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddSingleton<IRecordingService, RecordingService>();
        services.AddSingleton<IEventService, EventService>();
        services.AddSingleton<IEventRuleService, EventRuleService>();
        services.AddSingleton<ILayoutService, LayoutService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IBrandConfigService, BrandConfigService>();
        services.AddSingleton<ILicenseService, LicenseService>();

        // QCTek fallback engine — injected into OnvifService constructor
        services.AddSingleton<QCTekService>();
        services.AddSingleton<RtspUrlResolver>();
        services.AddSingleton<RecordingSchedulerService>();
        services.AddSingleton<IVideoIndexService, VideoIndexService>();
        services.AddSingleton<ISystemStatusService, SystemStatusService>();
        services.AddSingleton<ICameraHealthService, CameraHealthService>();
        services.AddSingleton<IRecordingIntegrityService, RecordingIntegrityService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IMotionAnalysisService, MotionAnalysisService>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        services.AddSingleton<MediaMTXService>();
        services.AddSingleton<EMapService>();

        // OnvifService (depends on QCTekService + RtspUrlResolver)
        services.AddSingleton<IOnvifService>(sp => {
            var qctek = sp.GetRequiredService<QCTekService>();
            var resolver = sp.GetRequiredService<RtspUrlResolver>();
            return new OnvifService(qctek, resolver);
        });
    }

    private static void TryMigrateCameras() {
        try {
            var cameraService = Services.GetRequiredService<ICameraService>();
            var existing = cameraService.GetAllCameras();
            if (existing.Count > 0) { return; }

            var legacyPaths = new[]
            {
                @"C:\Users\JS\Desktop\HeliNVR\bin\Debug\net8.0-windows\Data\cameras.json",
                @"C:\Users\JS\Desktop\HeliNVR\bin\Debug\net10.0-windows\Data\cameras.json",
            };

            foreach (var path in legacyPaths) {
                if (File.Exists(path)) {
                    Log.Information("Migrating cameras from {Path}", path);
                    cameraService.MigrateFromLegacy(path);
                    return;
                }
            }

            Log.Information("No legacy camera data found, starting fresh");
        } catch (Exception ex) {
            Log.Error(ex, "Failed to migrate camera data");
        }
    }
}
