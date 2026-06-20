using System.IO;
using System.Threading.Tasks;
using System.Windows;
using FlyleafLib;
using HeliVMS.Controls;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Safely execute async void event handler, catch unhandled exceptions</summary>
    public static async void SafeFireAndForget(Func<Task> asyncAction, string context = "")
    {
        try
        {
            await asyncAction();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[HeliVMS] Unhandled exception in async event handler: {Context}", context);
        }
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

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

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "AppDomain unhandled exception. Terminating: {IsTerminating}", args.IsTerminating);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Dispatcher unhandled exception, Handled={Handled}", args.Handled);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task unobserved exception");
        };

        Log.Information("HeliVMS starting...");
        Log.Information("[TIMING] Step 1 — Log init: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // Build DI container
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        Log.Information("[TIMING] Step 2 — DI BuildServiceProvider: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // === Show main window first, avoid blocking on background init ===
        var mainWindow = new MainWindow();
        Log.Information("[TIMING] Step 3 — MainWindow constructed: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        mainWindow.Show();
        Log.Information("[TIMING] Step 4 — MainWindow shown: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // === Initialize Flyleaf media engine ===
        InitializeFlyleafEngine();
        Log.Information("[TIMING] Step 4a — Flyleaf init: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // === Remaining services initialized in background, non-blocking ===
        _ = StartBackgroundServicesAsync();
        Log.Information("[TIMING] Step 5 — Startup complete: {ElapsedMs}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>Run startup initialization in background, non-blocking UI thread</summary>
    private async Task StartBackgroundServicesAsync()
    {
        // Start camera health monitoring
        try
        {
            var healthService = Services.GetRequiredService<ICameraHealthService>();
            healthService.StartMonitoring();
        }
        catch (Exception ex) { Log.Warning(ex, "Camera health service start failed"); }

        // Start recording integrity check
        try
        {
            var integrityService = Services.GetRequiredService<IRecordingIntegrityService>();
            integrityService.StartMonitoring();
        }
        catch (Exception ex) { Log.Warning(ex, "Recording integrity service start failed"); }

        // Start motion detection analysis service
        try
        {
            var motionService = Services.GetRequiredService<IMotionAnalysisService>();
            motionService.StartMonitoring();
        }
        catch (Exception ex) { Log.Warning(ex, "Motion analysis service start failed"); }

        // Log system startup event
        try
        {
            var eventService = Services.GetRequiredService<IEventService>();
            eventService.LogInfo("系統", "HeliVMS", "系統啟動完成");
        }
        catch { }

        // Start recording scheduler
        try
        {
            var scheduler = Services.GetRequiredService<RecordingSchedulerService>();
            scheduler.Start();
            Log.Information("Recording scheduler started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start recording scheduler");
        }

        // Initialize video index database (SQLite + composite indices)
        IVideoIndexService? indexService = null;
        try
        {
            indexService = Services.GetRequiredService<IVideoIndexService>();
            await indexService.EnsureDatabaseCreatedAsync().ConfigureAwait(false);
            Log.Information("Video index database initialized");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Video index database initialization failed (non-fatal)");
        }

        // Auto-purge recordings at startup based on retention policy
        if (indexService is not null)
        {
            try
            {
                var settings = Services.GetRequiredService<ISettingsService>();
                var recordingService = Services.GetRequiredService<IRecordingService>();
                if (settings.Settings.EnableAutoPurge)
                {
                    var (del, freed) = await indexService.PurgeByRetentionPolicyAsync(
                        settings.Settings.RetentionDays,
                        settings.Settings.MaxStorageGB,
                        recordingService.GetBasePath()
                    ).ConfigureAwait(false);
                    if (del > 0)
                    {
                        Log.Information("Startup auto-purge: deleted {Count} segments, freed {FreedMB:F1} MB", del, freed / (1024.0 * 1024.0));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Startup auto-purge failed (non-fatal)");
            }
        }

        // Migrate old directory structure CH{ch}/yyyy-MM-dd/ → yyyy-MM-dd/CH{ch}/
        try
        {
            var recordingService2 = Services.GetRequiredService<IRecordingService>();
            var (moved, updated) = await recordingService2.MigrateToDateFirstStructureAsync()
                .ConfigureAwait(false);
            if (moved > 0)
            {
                Log.Information("Directory structure migration: moved {Moved} dirs, updated {Updated} DB paths", moved, updated);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Directory structure migration failed (non-fatal)");
        }

        // FFmpeg engine will be lazily initialized on first use

        // Start MediaMTX media relay
        try
        {
            var mtx = Services.GetRequiredService<MediaMTXService>();
            mtx.Start();
            Log.Information("MediaMTX relay started");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MediaMTX relay start failed (non-fatal)");
        }

        // Migrate camera settings from old project
        TryMigrateCameras();
    }

    private static void InitializeFlyleafEngine()
    {
        try
        {
            var ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");
            if (Directory.Exists(ffmpegPath))
            {
                Engine.Start(new EngineConfig
                {
                    FFmpegPath = ffmpegPath,
                    DisableAudio = true,
                    UIRefresh = false,
                    UIRefreshInterval = 250,
                });
                Log.Information("Flyleaf Engine initialized (FFmpeg: {Path})", ffmpegPath);
            }
            else
            {
                Log.Warning("Flyleaf FFmpeg directory not found at {Path}", ffmpegPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize Flyleaf Engine");
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        Log.Information("HeliVMS shutting down...");

        try
        {
            var scheduler = Services.GetRequiredService<RecordingSchedulerService>();
            scheduler.Stop();
            Log.Information("Recording scheduler stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping recording scheduler during shutdown");
        }

        // Safety net: ensure all ffmpeg.exe processes are terminated
        RecordingService.KillAllFfmpegProcesses();

        // Stop MediaMTX media relay
        try
        {
            var mtx = Services.GetRequiredService<MediaMTXService>();
            mtx.Stop();
            Log.Information("MediaMTX relay stopped");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping MediaMTX relay");
        }

        // Stop all decoder processes
        VideoPlayer.CleanupAllDecoderSessions();
        VideoPlayer.CleanupAllFlyleafPlayers();

        Log.Information("HeliVMS shutdown complete");
        Log.CloseAndFlush();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddSingleton<IRecordingService, RecordingService>();
        services.AddSingleton<IEventService, EventService>();
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

        // OnvifService (depends on QCTekService + RtspUrlResolver)
        services.AddSingleton<IOnvifService>(sp =>
        {
            var qctek = sp.GetRequiredService<QCTekService>();
            var resolver = sp.GetRequiredService<RtspUrlResolver>();
            return new OnvifService(qctek, resolver);
        });
    }

    private static void TryMigrateCameras()
    {
        try
        {
            var cameraService = Services.GetRequiredService<ICameraService>();
            var existing = cameraService.GetAllCameras();
            if (existing.Count > 0) { return; }

            var legacyPaths = new[]
            {
                @"C:\Users\JS\Desktop\HeliNVR\bin\Debug\net8.0-windows\Data\cameras.json",
                @"C:\Users\JS\Desktop\HeliNVR\bin\Debug\net10.0-windows\Data\cameras.json",
            };

            foreach (var path in legacyPaths)
            {
                if (File.Exists(path))
                {
                    Log.Information("Migrating cameras from {Path}", path);
                    cameraService.MigrateFromLegacy(path);
                    return;
                }
            }

            Log.Information("No legacy camera data found, starting fresh");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate camera data");
        }
    }
}
