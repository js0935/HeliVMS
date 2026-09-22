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
builder.Services.AddSingleton<AlertBroadcastHub>();
builder.Services.AddSingleton<AuthService>();

var app = builder.Build();

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