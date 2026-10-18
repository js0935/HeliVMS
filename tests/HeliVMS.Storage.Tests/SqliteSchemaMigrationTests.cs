namespace HeliVMS.Storage.Tests;

/// <summary>
/// 結構遷移的收斂性（§4）。<see cref="SqliteStore.Initialize"/> 只在最後才寫
/// <c>PRAGMA user_version</c>，而每個步驟的 DDL 都會各自提交；因此「執行到一半被中斷」
/// 會留下「欄位／資料表已經在、但 user_version 還停在舊值」的資料庫，下次啟動就會重跑
/// 那一段。
///
/// <para>
/// 真實事故：某台機器每次開機都停在啟動畫面（logo），因為前一次啟動在 <b>最後一個</b>
/// 遷移被中斷，user_version 卡在 51，而 52–55 的成果（settings_versions、索引、
/// checked_at_utc、recording_lease）都已存在。下一次啟動重跑到 v54 的
/// <c>ALTER TABLE segments ADD COLUMN checked_at_utc</c> 就擲出
/// <c>SQLite Error 1: duplicate column name</c>，而且例外被啟動流程吞掉，
/// 畫面永遠停在 logo，log 之外沒有任何線索。
/// </para>
///
/// <para>
/// 這裡守的性質是：<b>無論前一次在哪一步中斷，下一次 Initialize 都能安全收斂到
/// <see cref="SqliteStore"/> 的當前版本</b>。方法是讓每段遷移冪等，而不是靠
/// user_version 精準。
/// </para>
/// </summary>
public class SqliteSchemaMigrationTests : IDisposable
{
    private const int CurrentVersion = 56;
    private readonly string _dbPath;

    public SqliteSchemaMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-schema-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private int ReadVersion(SqliteStore store) =>
        store.Query<int>("PRAGMA user_version;", r => r.Read() ? r.GetInt32(0) : -1);

    private bool TableExists(SqliteStore store, string table) =>
        store.Query<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;",
            r => r.Read() ? r.GetInt32(0) : 0,
            c => c.Parameters.AddWithValue("$n", table)) > 0;

    private bool ColumnExists(SqliteStore store, string table, string column) =>
        store.Query<int>(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$n;",
            r => r.Read() ? r.GetInt32(0) : 0,
            c => c.Parameters.AddWithValue("$n", column)) > 0;

    [Fact]
    public void 重複初始化不會因欄位已存在而失敗()
    {
        using var store = new SqliteStore(_dbPath);

        store.Initialize();
        store.Initialize();

        Assert.Equal(CurrentVersion, ReadVersion(store));
    }

    /// <summary>
    /// 直接重現線上事故：欄位／資料表已由前一次啟動建立，但 user_version 因中斷而停在 51。
    /// 舊程式的 v54 會擲出 duplicate column；修好後必須一路收斂到最新版。
    /// </summary>
    [Fact]
    public void 版本落後但欄位已存在時初始化仍會收斂()
    {
        using var store = new SqliteStore(_dbPath);
        store.Initialize();

        // 把版本退回 51（欄位與 52–55 的資料表都留著），等同「上次跑到最後一步被中斷」。
        store.Execute("PRAGMA user_version = 51;");

        store.Initialize();

        Assert.Equal(CurrentVersion, ReadVersion(store));
        Assert.True(ColumnExists(store, "segments", "checked_at_utc"));
    }

    /// <summary>
    /// 最貼近事故現場的版本：中斷發生在最後一個遷移之前，所以 bookmarks 還沒建立，
    /// 但 v52–v55 的成果都在。下一次啟動必須補上 bookmarks 並把版本推到最新。
    /// </summary>
    [Fact]
    public void 最後一步中斷後下次初始化會補上缺少的資料表()
    {
        using var store = new SqliteStore(_dbPath);
        store.Initialize();

        store.Execute("DROP TABLE IF EXISTS bookmarks;");
        store.Execute("PRAGMA user_version = 51;");

        store.Initialize();

        Assert.Equal(CurrentVersion, ReadVersion(store));
        Assert.True(TableExists(store, "bookmarks"));
        Assert.True(ColumnExists(store, "segments", "checked_at_utc"));
        Assert.True(TableExists(store, "recording_lease"));
    }
}
