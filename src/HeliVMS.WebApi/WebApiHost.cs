using HeliVMS.Licensing;
using HeliVMS.Licensing.Crypto;
using HeliVMS.Rtc;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Extensions.FileProviders;

namespace HeliVMS.WebApi;

/// <summary>建立 WebApi 主機的選項。</summary>
public sealed record WebApiHostOptions
{
    /// <summary>
    /// 是否註冊背景服務（保留清理、即時串流維護）。
    ///
    /// 桌面程式內嵌主機時設為 <c>false</c>：桌面程序本身已經有保留迴圈
    /// （<c>MainWindow.RunRetentionLoopAsync</c>），再掛一份 <see cref="RetentionService"/>
    /// 會讓同一份資料庫與同一個 recordings 目錄被兩個清理器同時掃，白白重工又可能互搶刪檔。
    /// 獨立執行的 WebApi 仍維持 <c>true</c>（與過去行為一致）。
    /// </summary>
    public bool IncludeHostedServices { get; init; } = true;
}

/// <summary>
/// WebApi 的組態與啟動邏輯（M305）。抽成這個型別是為了讓桌面程式能用同一份程式碼在行程內
/// 啟停 Web 服務（設定中心「網路」頁），不必另外帶一顆 WebApi 執行檔、猜它裝在哪，也不必
/// 處理子行程的生命週期。頂層 <c>Program.cs</c> 只是呼叫它，獨立執行路徑維持不變。
/// </summary>
public static class WebApiHost
{
    /// <summary>
    /// 建立（尚未啟動）的 Web 主機。
    /// </summary>
    /// <param name="args">命令列引數；內嵌時用來傳入 <c>--urls</c>、資料庫路徑與金鑰開關。</param>
    /// <param name="options">主機選項。</param>
    /// <param name="contentRoot">
    /// 內容根目錄；<c>null</c> 時沿用 <see cref="WebApplication.CreateBuilder(string[])"/> 的預設
    /// （目前工作目錄），讓獨立執行與測試的行為與過去完全一致。內嵌時傳入應用程式基底目錄。
    /// </param>
    public static WebApplication Create(string[] args, WebApiHostOptions? options = null, string? contentRoot = null)
    {
        options ??= new WebApiHostOptions();
        var builder = contentRoot is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        RegisterServices(builder, options);

        var app = builder.Build();
        Configure(app, builder.Configuration);
        return app;
    }

    private static void RegisterServices(WebApplicationBuilder builder, WebApiHostOptions options)
    {
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
                recordingsRoot,
                logger: sp.GetRequiredService<ILogger<RetentionService>>());
        });

        // 保留清理的單例永遠註冊（API 的 /config/usage、/retention/run 需要解析它），
        // 只有背景執行本身受 IncludeHostedServices 控制。
        if (options.IncludeHostedServices)
        {
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
        }

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
        builder.Services.AddSingleton(sp =>
        {
            // 遠端回放路徑政策要涵蓋「所有」錄影磁碟：桌面在設定中心新增多顆碟後，影像可能落在
            // 主要碟以外的磁碟；政策若只看設定的單一根目錄，Web 端播放次要碟上的片段會被判成
            // 不在允許範圍而 404。與 RetentionService 一樣於啟動時讀一次。
            var config = sp.GetRequiredService<IConfiguration>();
            var store = sp.GetRequiredService<SqliteStore>();
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

            var fallback = RecordingStorage.ResolveConfiguredRoot(dataRoot, configured, name => config[name]);
            return new RecordedSegmentPolicy(RecordingStorage.ConfiguredRoots(store, fallback));
        });
        builder.Services.AddSingleton(static sp => new RedactionRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton(static sp => new OffsiteReplicationRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton(static sp => new OffsiteReplicationService(sp.GetRequiredService<OffsiteReplicationRepository>()));

        // §14.1 #10 中央/多機集群管理（M295）。真實 HTTP 用戶端以 SocketsHttpHandler 為底，
        // 逾時 5 秒；測試改注入假 ICentralNodeClient，因此不需真的連上子節點。
        builder.Services.AddSingleton(static sp => new CentralNodeRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton<ICentralNodeClient>(
            static _ => new HttpCentralNodeClient(new SocketsHttpHandler(), TimeSpan.FromSeconds(5)));
        builder.Services.AddSingleton(static sp => new CentralNodeService(
            sp.GetRequiredService<CentralNodeRepository>(),
            sp.GetRequiredService<ICentralNodeClient>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(static sp => new EmbeddingRepository(sp.GetRequiredService<SqliteStore>()));

        // §14.1 #8：伺服器端就地產生嵌入。真機可換成 CLIP/ONNX 實作（同一介面），
        // 預設用決定性雜湊嵌入器，讓只帶文字的查詢／索引也能走向量路徑而無需外部模型。
        builder.Services.AddSingleton<IEmbeddingProvider>(static _ => HashingEmbeddingProvider.Default);
        builder.Services.AddSingleton(static sp => new ForensicSemanticSearch(
            sp.GetRequiredService<SqliteStore>(),
            sp.GetRequiredService<IEmbeddingProvider>()));
        builder.Services.AddSingleton(static sp => new DeviceRepository(
            sp.GetRequiredService<SqliteStore>(),
            sp.GetRequiredService<AuditLogRepository>()));
        builder.Services.AddSingleton(static sp => new EventAudioRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton(static sp => new AlarmTriageRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton(static sp => new AlarmEventRepository(sp.GetRequiredService<SqliteStore>()));
        builder.Services.AddSingleton<AlertBroadcastHub>();
        builder.Services.AddSingleton<AuthService>();

        // M306：網頁帳號登入的工作階段暫存區（登入成功簽發 token、ApiKeyAuthMiddleware 認可）。
        builder.Services.AddSingleton<AccountSessionStore>();

        // Web 端 PTZ（M25 桌機 PTZ 的遠端對應）。預設實作只是 OnvifDeviceService 的薄配接；
        // 測試可覆寫成假 gateway，避免在 API 測試裡再養一份 ONVIF 假伺服器。
        builder.Services.AddSingleton<IOnvifPtzGateway, OnvifPtzGateway>();

        // Web 端攝影機 IO（乾接點）：列出 DI/DO、切換繼電器。與 PTZ 同一條薄配接路徑。
        builder.Services.AddSingleton<IOnvifIoGateway, OnvifIoGateway>();

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

        if (options.IncludeHostedServices)
        {
            builder.Services.AddHostedService<LiveStreamMaintenance>();
        }

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
    }

    private static void Configure(WebApplication app, IConfiguration configuration)
    {
        // 啟動時重新驗證授權列（M213，§19.4「合併檢查」）：桌面端與遠端服務共用同一條 RefreshDefault，
        // 兩邊都做才不會出現「UI 端驗過、API 端信任舊列」的缺口。
        app.Services.GetRequiredService<LicenseService>()
            .RefreshDefault("startup", app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var webRoot = FindWebRoot(configuration["HELIVMS_WEB"]);

        app.Use(ApiEndpoints.ErrorFilter);
        if (webRoot is not null)
        {
            var provider = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                // SPA 是直接由原始碼目錄供應的檔案，而 StaticFileMiddleware 預設只送
                // ETag／Last-Modified、不送 Cache-Control，瀏覽器便改用啟發式新鮮度
                // （約「檔案年齡的 10%」）自己判斷「這份還很新」，於是修好的 JS 可能被
                // 舊快取蓋掉。現場症狀是「明明修好了，瀏覽器還在跑舊版」——例如瀏覽器
                // 用舊的 live.js（sendonly）去協商，就得到 Incompatible receive direction，
                // 但伺服器與原始碼其實都是對的，很難從畫面看出是快取。
                // no-cache 不是不快取，是每次都回伺服器驗證：沒變仍回 304，負擔很小。
                OnPrepareResponse = ctx =>
                    ctx.Context.Response.Headers.CacheControl = "no-cache",
            });
        }

        app.UseWebSockets();
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseMiddleware<LicenseGateMiddleware>();
        ApiEndpoints.MapAll(app);
        PtzEndpoints.Map(app);
        IoEndpoints.Map(app);
        WhepEndpoints.Map(app);

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || webRoot is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            // index.html 也一樣要每次驗證：它決定瀏覽器載入哪一版 JS／CSS。
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
        });
    }

    /// <summary>Resolves the HeliVMS.Web SPA root (config override, else walk up to src/HeliVMS.Web).</summary>
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
