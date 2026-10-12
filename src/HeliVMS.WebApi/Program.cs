using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;
using HeliVMS.Rtc;
using HeliVMS.Shared;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// 資料庫預設與桌面程式指向同一個檔案（§2.1）。
//
// 這裡以前落到 %TEMP%\helivms-webapi.db，於是同一台機器上的桌面程式與 Web 服務各看各的：
// Web 寫的錄影排程進不了桌面排程器讀的那份資料庫，設定頁顯示儲存成功但機器一台都沒錄；
// 兩邊的保留清理也各拿一份區段索引，卻對同一個 recordings 目錄下手。
// 路徑規則的單一來源是 DataRootLayout，兩邊不會再各自漂移。
var dbPath = DataRootLayout.ResolveDatabasePath(name => builder.Configuration[name]);

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
builder.Services.AddSingleton(static sp => new ExportReceiptService(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new UserRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new SettingsRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AuditLogRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ReportRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new RecordingScheduleRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new PatrolRepository(sp.GetRequiredService<SqliteStore>()));

builder.Services.AddSingleton(sp =>
{
    // 錄影根目錄與桌面端、Agent 共用同一條規則：以設定中心（共用資料庫的 recording.root）為準，
    // 未設定時依序退回 HELIVMS_RECORDINGS_ROOT 與「避開系統碟」的推導值（見 RecordingStorage）。
    // Web 端於啟動時讀一次；桌面切換磁碟後，Web 下次啟動就會跟著切換。
    var config = sp.GetRequiredService<IConfiguration>();
    var dataRoot = DataRootLayout.ResolveRoot(name => config[name]);
    string? configured = null;
    try
    {
        configured = sp.GetRequiredService<SettingsRepository>().Get(RecordingStorage.RootSettingKey);
    }
    catch (Exception)
    {
        // 資料庫尚未初始化時退回推導值，不讓 Web 服務因讀不到設定而啟動失敗。
    }

    var recordingsRoot = RecordingStorage.ResolveConfiguredRoot(dataRoot, configured, name => config[name]);
    return new RetentionService(
        sp.GetRequiredService<SqliteStore>(),
        sp.GetRequiredService<LegalHoldRepository>(),
        recordingsRoot);
});
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
builder.Services.AddSingleton(static sp => new RecordedSegmentPolicy(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(static sp => new RedactionRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new OffsiteReplicationRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new OffsiteReplicationService(sp.GetRequiredService<OffsiteReplicationRepository>()));
builder.Services.AddSingleton(static sp => new EmbeddingRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new ForensicSemanticSearch(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new DeviceRepository(
    sp.GetRequiredService<SqliteStore>(),
    sp.GetRequiredService<AuditLogRepository>()));
builder.Services.AddSingleton(static sp => new EventAudioRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmTriageRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton(static sp => new AlarmEventRepository(sp.GetRequiredService<SqliteStore>()));
builder.Services.AddSingleton<AlertBroadcastHub>();
builder.Services.AddSingleton<AuthService>();

// M244 WebRTC 即時監看。設定解析放在這裡（WhepOptions 刻意不依賴
// IConfiguration），讓領域層維持純粹且設定解析仍可單元測試。
builder.Services.AddSingleton(sp => WhepOptions.FromLookup(
    key => sp.GetRequiredService<IConfiguration>()[key],
    (category, message) => Console.Error.WriteLine($"[{category}] {message}")));
builder.Services.AddSingleton<WhepSessionStore>();
builder.Services.AddSingleton<LiveStreamService>(sp => new LiveStreamService(
    sp.GetRequiredService<WhepOptions>(),
    sp.GetRequiredService<WhepSessionStore>(),
    PublisherStarters.StartFfmpeg));
builder.Services.AddHostedService<LiveStreamMaintenance>();

// 遠程與本機共用同一份授權結論（M210／§19.4「合併檢查」）。公鑰可由
// HELIVMS_LICENSE_PUBLIC_KEY 覆寫以支援金鑰輪替；未設定時用內嵌公鑰。
// HELIVMS_LICENSE 可把授權檔路徑指到別處：測試必須靠它隔離開發機上真正的
// %LOCALAPPDATA%\HeliVMS\license.lic，否則「未匯入授權」的情境會讀到人家已匯入的授權。
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var publicKeyPem = config["HELIVMS_LICENSE_PUBLIC_KEY"];
    var licensePath = config["HELIVMS_LICENSE"];
    var tokenSource = string.IsNullOrWhiteSpace(licensePath)
        ? (Func<string>?)null
        : () => File.Exists(licensePath) ? File.ReadAllText(licensePath) : string.Empty;
    var manager = string.IsNullOrWhiteSpace(publicKeyPem)
        ? new LicenseManager(EmbeddedPublicKey.Value, tokenSource)
        : new LicenseManager(publicKeyPem, tokenSource);
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
WhepEndpoints.Map(app);

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