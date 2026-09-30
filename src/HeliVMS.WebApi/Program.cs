using HeliVMS.Licensing;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var dbPath = builder.Configuration["HELIVMS_DB"]
    ?? Path.Combine(Path.GetTempPath(), "helivms-webapi.db");

builder.Services.AddSingleton(sp =>
{
    var store = new SqliteStore(dbPath);
    store.Initialize();
    new ChannelRepository(store).EnsureSeedChannels();
    return store;
});

builder.Services.AddSingleton(static sp => new ChannelRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmEventRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmTriageRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new POSEventRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new EventSearchRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new UnifiedEventSearch(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new SegmentRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new UserRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new SettingsRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AuditLogRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ReportRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new RecordingScheduleRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new PatrolRepository(sp.GetRequiredService<SqliteStore>()));

builder.Services.AddSingleton(sp => new RetentionService(
    sp.GetRequiredService<SqliteStore>(),
    sp.GetRequiredService<LegalHoldRepository>(),
    sp.GetRequiredService<IConfiguration>()[RetentionService.RecordingsRootKey]));
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddSingleton(static sp => new EvidenceManifestRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new BackupService(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new BackupRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new DoorEventRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new DetectionRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new NotificationLogRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ExportJobRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AuthProviderRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new LegalHoldRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlertRuleRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ShareLinkRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ShareLinkService(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new PathAccessPolicy(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(static sp => new RedactionRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new OffsiteReplicationRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new OffsiteReplicationService(sp.GetRequiredService<OffsiteReplicationRepository>()));
builder.Services.AddSingleton(static sp => new EmbeddingRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new DeviceRepository(
    sp.GetRequiredService<SqliteStore>(),
    sp.GetRequiredService<AuditLogRepository>()));
builder.Services.AddSingleton(static sp => new EventAudioRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmTriageRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmEventRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton<AlertBroadcastHub>();
builder.Services.AddSingleton<AuthService>();

// 遠程與本機共用同一份授權結論（M210／§19.4「合併檢查」）。公鑰可由
// HELIVMS_LICENSE_PUBLIC_KEY 覆寫以支援金鑰輪替；未設定時用內嵌公鑰。
builder.Services.AddSingleton(sp =>
{
    var publicKeyPem = sp.GetRequiredService<IConfiguration>()["HELIVMS_LICENSE_PUBLIC_KEY"];
    var manager = string.IsNullOrWhiteSpace(publicKeyPem)
        ? new LicenseManager()
        : new LicenseManager(publicKeyPem);
    return new LicenseService(sp.GetRequiredService<SqliteStore>(), manager);
});
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// 啟動時重新驗證授權列（M213，§19.4「合併檢查」）：桌面端與遠端服務共用同一條 RefreshDefault，
// 兩邊都做才不會出現「UI 端驗過、API 端信任舊列」的缺口。
app.Services.GetRequiredService<LicenseService>()
    .RefreshDefault("startup", app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

var webRoot = Program.FindWebRoot(builder.Configuration["HELIVMS_WEB"]);

app.Use(ApiEndpoints.ErrorFilter);
if (webRoot is not null)
{
    var provider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

app.UseWebSockets();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseMiddleware<LicenseGateMiddleware>();
ApiEndpoints.MapAll(app);

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") || webRoot is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
});

app.Run();

/// <summary>Test entry point (M117, section 14.3 P0).</summary>
public partial class Program
{
    /// <summary>Resolves the HeliVms.Web SPA root (config override, else walk up to src/HeliVMS.Web).</summary>
    public static string? FindWebRoot(string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "index.html")))
        {
            return configured;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 14 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "HeliVMS.Web");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }

        return null;
    }
}