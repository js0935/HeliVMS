using HeliVMS.Storage;
using HeliVMS.WebApi;

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
builder.Services.AddSingleton<AlertBroadcastHub>();

var app = builder.Build();

app.Use(ApiEndpoints.ErrorFilter);
app.UseWebSockets();
app.UseMiddleware<ApiKeyAuthMiddleware>();
ApiEndpoints.MapAll(app);

app.Run();

/// <summary>Test entry point (M117, section 14.3 P0).</summary>
public partial class Program
{
}