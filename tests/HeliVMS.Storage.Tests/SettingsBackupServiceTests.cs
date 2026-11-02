using System.Text.Json;
using HeliVMS.Shared;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 設定檔匯出／匯入（§2.1 設定遷移）。釘四件事：
/// 往返不失真（含 Id 與跨表引用）、匯入是原子的（壞檔整份回滾，現場不會變半套）、
/// 未來版本拒絕、憑證與授權表（users／auth_providers／license）絕不出現在匯出檔。
/// </summary>
public class SettingsBackupServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly SettingsBackupService _service;
    private readonly string _backupPath;

    public SettingsBackupServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-settings-xfer-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _service = new SettingsBackupService(_store, "0.1.0-test");
        _backupPath = Path.Combine(Path.GetTempPath(), $"helivms-settings-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
        if (File.Exists(_backupPath)) { File.Delete(_backupPath); }
    }

    private long AddChannel(string name)
    {
        return _store.Query<long>(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, enabled) VALUES (NULL, $n, 'rtsp://a', 'rtsp://b', 1);
            SELECT last_insert_rowid();
            """,
            r => { r.Read(); return r.GetInt64(0); },
            c => c.Parameters.AddWithValue("$n", name));
    }

    [Fact]
    public void 匯出再匯入後設定與頻道含Id原樣保留()
    {
        var channel = AddChannel("大門口");
        _store.Execute(
            "INSERT INTO app_settings (key, value, updated_at) VALUES ('ui.lang', 'zh-Hant', $t);",
            c => c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O")));
        var taskId = new ScheduledTaskRepository(_store).Add(
            "夜間重開機", MaintenanceTaskKind.Reboot, MaintenanceScheduleKind.Daily, timeOfDay: 300);

        _service.ExportTo(_backupPath);

        // 清掉現場，模擬換機／還原。
        _store.Execute("DELETE FROM channels;");
        _store.Execute("DELETE FROM app_settings;");
        _store.Execute("DELETE FROM scheduled_tasks;");

        _service.ImportFrom(_backupPath);

        var channelRow = _store.Query(
            "SELECT id, name FROM channels;",
            r => { r.Read(); return (Id: r.GetInt64(0), Name: r.GetString(1)); });
        Assert.Equal(channel, channelRow.Id);
        Assert.Equal("大門口", channelRow.Name);

        var lang = _store.Query(
            "SELECT value FROM app_settings WHERE key='ui.lang';",
            r => { r.Read(); return r.GetString(0); });
        Assert.Equal("zh-Hant", lang);

        var task = Assert.Single(new ScheduledTaskRepository(_store).List());
        Assert.Equal(taskId, task.Id);
        Assert.Equal(300, task.TimeOfDay);
    }

    [Fact]
    public void 匯入是原子的壞列讓整份回滾()
    {
        AddChannel("原本的頻道");
        _service.ExportTo(_backupPath);

        // 篡改：加一份白名單之外的表＋一份含不存在欄位的列。
        var tampered = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["createdUtc"] = "2026-11-02T00:00:00Z",
            ["appVersion"] = "x",
            ["tables"] = new Dictionary<string, object?>
            {
                ["channels"] = new[] { new Dictionary<string, object?> { ["id"] = 99L, ["name"] = "壞欄位", ["not_a_column"] = 1L } },
                ["users"] = new[] { new Dictionary<string, object?> { ["id"] = 1L } },
            },
        });
        File.WriteAllText(_backupPath, tampered);

        Assert.ThrowsAny<Exception>(() => _service.ImportFrom(_backupPath));

        // 原資料原封不動：交易整份回滾。
        var channels = _store.Query("SELECT name FROM channels;", r => { r.Read(); return r.GetString(0); });
        Assert.Equal("原本的頻道", channels);
    }

    [Fact]
    public void 未來版本的檔案拒絕匯入()
    {
        File.WriteAllText(_backupPath, "{\"version\":999,\"tables\":{}}");
        Assert.Throws<InvalidOperationException>(() => _service.ImportFrom(_backupPath));
    }

    [Fact]
    public void 憑證與授權表不出現在匯出檔()
    {
        // users／auth_providers 是憑證、license 是機器綁定：跟著設定檔走等於把身分搬去別台機器。
        _service.ExportTo(_backupPath);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_backupPath));
        var tables = doc.RootElement.GetProperty("tables");
        foreach (var forbidden in new[] { "users", "auth_providers", "license", "login_sessions", "audit_log", "segments" })
        {
            Assert.False(tables.TryGetProperty(forbidden, out _), $"匯出檔不應含 {forbidden}");
        }
    }

    [Fact]
    public void 摘要回報檔案版本與各表筆數()
    {
        AddChannel("A");
        AddChannel("B");
        _service.ExportTo(_backupPath);

        var summary = _service.ReadSummary(_backupPath);

        Assert.Equal(SettingsBackupService.BackupVersion, summary.Version);
        Assert.Equal("0.1.0-test", summary.AppVersion);
        Assert.Equal(2, summary.Counts["channels"]);
    }

    [Fact]
    public void 壞JSON給出可理解的錯誤()
    {
        File.WriteAllText(_backupPath, "{not json");
        Assert.Throws<InvalidOperationException>(() => _service.ImportFrom(_backupPath));
    }
}
