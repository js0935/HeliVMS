using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 設定版本快照與一鍵回滾的不變式（v52，§9.5）。
///
/// <para>
/// 這裡守兩件在現場才看得出後果的事。第一是**還原必須整份取代**：只把快照裡的鍵補回去，
/// 會留下「快照之後才新增」的鍵，操作者以為回到舊版、實際是混種狀態，而混種設定最難追。
/// 第二是**還原要原子**：先清空再逐鍵補回，若中途失敗會讓整台 NVR 停在「設定全被刪光」
/// 的狀態；因此還原走單一交易，任何例外都整批復原。
/// </para>
/// </summary>
public sealed class SettingsVersionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SettingsRepository _settings;

    public SettingsVersionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-setver-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _settings = new SettingsRepository(_store);
    }

    public void Dispose()
    {
        AuditActor.Reset();
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 快照後改動設定可還原回當時狀態()
    {
        _settings.Set("retention_days", "30");
        _settings.Set("ntp_server", "pool.ntp.org");

        var id = _settings.Snapshot("改動前");

        _settings.Set("retention_days", "90");
        _settings.Set("ntp_server", "time.windows.com");

        var restored = _settings.Restore(id);

        Assert.Equal(2, restored);
        Assert.Equal("30", _settings.Get("retention_days"));
        Assert.Equal("pool.ntp.org", _settings.Get("ntp_server"));
    }

    [Fact]
    public void 還原會移除快照之後才新增的鍵()
    {
        // 只補差異會留下不屬於舊版的鍵；整份取代才是一致的舊狀態。
        _settings.Set("a", "1");
        var id = _settings.Snapshot();

        _settings.Set("b", "2");
        _settings.Set("a", "9");

        _settings.Restore(id);

        Assert.Equal("1", _settings.Get("a"));
        Assert.Null(_settings.Get("b"));
    }

    [Fact]
    public void 還原不存在的版本會拋出例外()
    {
        _settings.Set("a", "1");

        Assert.Throws<InvalidOperationException>(() => _settings.Restore(4242));

        Assert.Equal("1", _settings.Get("a"));
    }

    [Fact]
    public void 版本清單依時間新到舊且受上限約束()
    {
        _settings.Set("a", "1");
        var first = _settings.Snapshot("一");
        var second = _settings.Snapshot("二");
        var third = _settings.Snapshot("三");

        var all = _settings.ListVersions();

        Assert.Equal(new[] { third, second, first }, all.Select(v => v.Id));
        Assert.Equal("三", all[0].Reason);
        Assert.Equal(1, all[0].KeyCount);

        var topTwo = _settings.ListVersions(2);
        Assert.Equal(new[] { third, second }, topTwo.Select(v => v.Id));
        Assert.Empty(_settings.ListVersions(0));
    }

    [Fact]
    public void 匯出再匯入可把設定複製到另一台()
    {
        _settings.Set("retention_days", "45");
        _settings.Set("live_layout", "9");

        var json = _settings.ExportJson();

        var otherPath = Path.Combine(Path.GetTempPath(), $"helivms-setver-clone-{Guid.NewGuid():N}.db");
        try
        {
            using var other = new SqliteStore(otherPath);
            other.Initialize();
            var imported = new SettingsRepository(other);
            imported.Set("stale", "should-be-gone");

            var count = imported.ImportJson(json, "alice");

            Assert.Equal(2, count);
            Assert.Equal("45", imported.Get("retention_days"));
            Assert.Equal("9", imported.Get("live_layout"));
            Assert.Null(imported.Get("stale"));
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var p = otherPath + suffix;
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    [Fact]
    public void 匯入非法JSON會被拒且不動到現有設定()
    {
        _settings.Set("keep", "1");

        Assert.Throws<ArgumentException>(() => _settings.ImportJson("{ not json"));
        Assert.Equal("1", _settings.Get("keep"));

        // 合法 JSON 但不是鍵值物件也不行。
        Assert.Throws<ArgumentException>(() => _settings.ImportJson("[1,2,3]"));
        Assert.Equal("1", _settings.Get("keep"));
    }

    [Fact]
    public void 快照與還原都會留下稽核且記下操作者()
    {
        AuditActor.Set("alice");
        _settings.Set("a", "1");
        var id = _settings.Snapshot("手動快照");
        _settings.Restore(id);

        Assert.Equal(1, CountAudit("settings.snapshot"));
        Assert.Equal(1, CountAudit("settings.rollback"));
        Assert.Equal("alice", LastAuditActor("settings.rollback"));
    }

    [Fact]
    public void 空設定快照可還原成空()
    {
        var id = _settings.Snapshot("全新");

        _settings.Set("x", "1");
        var restored = _settings.Restore(id);

        Assert.Equal(0, restored);
        Assert.Null(_settings.Get("x"));
        Assert.Equal(0, _settings.ListVersions()[0].KeyCount);
    }

    [Fact]
    public void 還原損壞的快照不會留下半套設定()
    {
        // snapshot 內含 null 值（正常寫入不可能產生，故以原始 SQL 注入）→ 還原前就該被拒。
        _settings.Set("keep", "1");
        _store.Execute(
            """
            INSERT INTO settings_versions (snapshot, key_count, reason, actor, created_at)
            VALUES ('{"a":"1","b":null}', 2, NULL, 'system', '2026-01-01T00:00:00.000Z');
            """);
        var badId = _store.Query<long>(
            "SELECT MAX(id) FROM settings_versions;", static r => { r.Read(); return r.GetInt64(0); });

        Assert.Throws<ArgumentException>(() => _settings.Restore(badId));

        Assert.Equal("1", _settings.Get("keep"));
    }

    [Fact]
    public void 交易中發生例外會整批復原()
    {
        _settings.Set("keep", "1");

        Assert.Throws<InvalidOperationException>(() =>
            _store.InTransaction(() =>
            {
                _store.Execute("DELETE FROM app_settings;");
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("1", _settings.Get("keep"));
    }

    private int CountAudit(string action) => _store.Query<int>(
        "SELECT COUNT(*) FROM audit_log WHERE action = $a;",
        static r => { r.Read(); return r.GetInt32(0); },
        cmd => cmd.Parameters.AddWithValue("$a", action));

    private string? LastAuditActor(string action) => _store.Query<string?>(
        "SELECT actor FROM audit_log WHERE action = $a ORDER BY id DESC LIMIT 1;",
        static r => r.Read() ? r.GetString(0) : null,
        cmd => cmd.Parameters.AddWithValue("$a", action));
}
