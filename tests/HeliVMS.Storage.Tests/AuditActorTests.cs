namespace HeliVMS.Storage.Tests;

/// <summary>
/// M278（§7.6）：管理面倉儲的 <c>actor</c> 未指定時會補上「目前登入者」（<see cref="AuditActor"/>），
/// 明確傳入的值（例如背景工作的 <c>"system"</c>）優先。原先行為是預設字面值 <c>"system"</c>，
/// 於是稽核只記得到「有人改了」而記不到「誰改的」。
///
/// <para>與 <see cref="AuditLogRepositoryTests"/> 同集合：兩者都會改動全域 <see cref="AuditActor"/>，
/// 必須循序執行，否則並行時會互相覆蓋。</para>
/// </summary>
[Collection("audit-actor")]
public sealed class AuditActorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public AuditActorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-actor-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
    }

    public void Dispose()
    {
        AuditActor.Reset();
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void 未指定操作者時記為目前登入者()
    {
        AuditActor.Set("alice");

        var id = new ChannelRepository(_store).Add("前門", "rtsp://cam/main");

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.add" }));
        Assert.Equal("alice", row.Actor);
        Assert.Equal(id, row.TargetId);
    }

    [Fact]
    public void 明確的操作者優先於目前登入者()
    {
        AuditActor.Set("alice");

        new ChannelRepository(_store).Add("前門", "rtsp://cam/main", actor: "admin");

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.add" }));
        Assert.Equal("admin", row.Actor);
    }

    [Fact]
    public void 背景系統工作仍記為system()
    {
        AuditActor.Set("alice");

        new SettingsRepository(_store).Set("rec.buffer.sec", "30");

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "settings.set" }));
        Assert.Equal("system", row.Actor);
    }

    [Fact]
    public void 未設定目前登入者時預設為system()
    {
        var id = new ChannelRepository(_store).Add("側門", "rtsp://cam/2");

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.add" }));
        Assert.Equal("system", row.Actor);
        Assert.Equal(id, row.TargetId);
    }

    [Fact]
    public void 設定空白會回到system並去除前後空白()
    {
        AuditActor.Set("alice");
        AuditActor.Set("  bob  ");
        Assert.Equal("bob", AuditActor.Current);

        AuditActor.Set("   ");
        Assert.Equal("system", AuditActor.Current);
    }
}
