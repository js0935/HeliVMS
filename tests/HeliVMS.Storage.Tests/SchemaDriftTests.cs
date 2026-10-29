using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 索引結構漂移：<c>user_version</c> 說「已經是最新版」，但表裡其實少欄位。
///
/// <para>
/// 這不是假想情況。現場那份 <c>index.db</c> 的版號是 56、<c>segments</c> 卻沒有
/// <c>tier</c>／<c>origin_path</c>／<c>checked_at_utc</c>；版本判定因此整段跳過，
/// <see cref="SegmentRepository"/> 的 SELECT 擲出 <c>no such column: tier</c>，
/// 而它為了容忍磁碟問題把例外吞掉，於是回放清單永遠是空的。操作員看到的是
/// 「磁碟裡有錄影、回放裡沒有資料」，日誌一片乾淨——錄影功能本身完全正常，
/// 壞的只有讀。
/// </para>
///
/// <para>
/// 這條測試刻意「先建好完整的庫、再把欄位拆掉、版號留著」：這正是重建、還原、
/// 或由較新元件先建立資料庫之後的樣子。
/// </para>
/// </summary>
public sealed class SchemaDriftTests : IDisposable
{
    private readonly string _dbPath;

    public SchemaDriftTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-schema-drift-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public void 版本號已是最新但缺欄位時初始化會補回來且回放查得到()
    {
        var startUtc = new DateTime(2026, 10, 29, 14, 20, 30, DateTimeKind.Utc);
        int schemaVersion;

        using (var store = new SqliteStore(_dbPath))
        {
            store.Initialize();
            schemaVersion = store.Query(
                "PRAGMA user_version;",
                static r =>
                {
                    r.Read();
                    return r.GetInt32(0);
                });

            store.Execute(
                "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES (7, '攝影機07', 'rtsp://127.0.0.1/ch7', 0);");
            var repo = new SegmentRepository(store);
            var id = repo.BeginSegment(
                7,
                "main",
                @"D:\HeliVMSRecordings\攝影機07\20261029\14\seg-142030311.mp4",
                startUtc);
            repo.CompleteSegment(id, startUtc.AddSeconds(50), 1234, 50, "sha");

            // 漂移的長相：欄位沒了，版號卻還是最新。
            store.Execute("DROP INDEX IF EXISTS idx_seg_unchecked;");
            store.Execute("DROP INDEX IF EXISTS idx_seg_checked_null;");
            store.Execute("ALTER TABLE segments DROP COLUMN checked_at_utc;");
            store.Execute("ALTER TABLE segments DROP COLUMN tier;");
            store.Execute("ALTER TABLE segments DROP COLUMN origin_path;");
            store.Execute($"PRAGMA user_version = {schemaVersion};");
        }

        using var reopened = new SqliteStore(_dbPath);
        reopened.Initialize(); // 修正前這一步什麼都不做：版號已經是最新

        Assert.True(HasColumn(reopened, "segments", "tier"), "tier 沒有補回來");
        Assert.True(HasColumn(reopened, "segments", "origin_path"), "origin_path 沒有補回來");
        Assert.True(HasColumn(reopened, "segments", "checked_at_utc"), "checked_at_utc 沒有補回來");
        Assert.True(IndexExists(reopened, "idx_seg_unchecked"), "相依的抽驗索引沒有重建");

        var segments = new SegmentRepository(reopened).ListByRange(
            7,
            "main",
            startUtc.AddHours(-1),
            startUtc.AddHours(1),
            out var error);

        // 失敗原因是「查詢擲例外」，修好之後必須是 null 而不是被吞掉的 no such column。
        Assert.Null(error);
        var row = Assert.Single(segments);
        Assert.Equal(startUtc, row.StartUtc);
    }

    private static bool HasColumn(SqliteStore store, string table, string column)
        => store.Query(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';",
            static r =>
            {
                r.Read();
                return r.GetInt64(0) > 0;
            });

    private static bool IndexExists(SqliteStore store, string index)
        => store.Query(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n;",
            static r =>
            {
                r.Read();
                return r.GetInt64(0) > 0;
            },
            cmd => cmd.Parameters.AddWithValue("$n", index));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 測試資料庫清不掉不影響結果。
        }
    }
}
