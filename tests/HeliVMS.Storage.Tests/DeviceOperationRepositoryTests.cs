using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 設備操作歷史的不變式（v51，§7.4）。
///
/// <para>
/// 這裡守兩件在現場才看得出後果的事。第一是刪設備時的操作紀錄：它跟設備綁定，
/// 用 <c>CASCADE</c> 隨設備消失（不像群組用 SET NULL），因為沒有設備的歷史只會指向
/// 不存在的對象；真正的追責依據在 <c>audit_log</c>（不隨設備刪除）。第二是失敗也要留紀錄：
/// 重啟／升級是按了會直接影響現場畫面的命令，若只記成功，「按了沒反應」將無從追查。
/// </para>
/// </summary>
public sealed class DeviceOperationRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly DeviceOperationRepository _ops;
    private readonly DeviceRepository _devices;
    private readonly AuditLogRepository _audit;

    public DeviceOperationRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-dops-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
        _ops = new DeviceOperationRepository(_store, _audit);
        _devices = new DeviceRepository(_store, _audit);
    }

    public void Dispose()
    {
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
    public void 寫入操作紀錄後可依時間倒序讀回()
    {
        var d = AddDevice("大門", "10.0.0.1");
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        _ops.Record(d, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok, occurredAtUtc: t0);
        _ops.Record(d, DeviceOperationTypes.Diagnostics, null, DeviceOperationResults.Ok, occurredAtUtc: t0.AddMinutes(5));

        var list = _ops.List(d);

        Assert.Equal(2, list.Count);
        // 最新在前，操作者打開視窗先看到的是剛剛做的事。
        Assert.Equal(DeviceOperationTypes.Diagnostics, list[0].OpType);
        Assert.Equal(DeviceOperationTypes.Reboot, list[1].OpType);
        Assert.Equal(t0, list[1].CreatedAt);
    }

    [Fact]
    public void 空的種類或結果會被拒絕()
    {
        var d = AddDevice("大門", "10.0.0.1");

        Assert.Throws<ArgumentException>(
            () => _ops.Record(d, "  ", null, DeviceOperationResults.Ok));
        Assert.Throws<ArgumentException>(
            () => _ops.Record(d, DeviceOperationTypes.Reboot, null, "  "));
    }

    [Fact]
    public void 參數與說明可為空並原樣讀回()
    {
        var d = AddDevice("大門", "10.0.0.1");

        _ops.Record(d, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok);

        var got = Assert.Single(_ops.List(d));
        Assert.Null(got.Param);
        Assert.Null(got.Detail);
    }

    [Fact]
    public void 失敗的操作也會留下紀錄()
    {
        // 這是本表存在的理由：命令失敗時若只記成功，畫面「按了沒反應」將無從追查。
        var d = AddDevice("大門", "10.0.0.1");

        _ops.Record(d, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Failed, "設備拒絕認證");

        var got = Assert.Single(_ops.List(d));
        Assert.Equal(DeviceOperationResults.Failed, got.Result);
        Assert.Equal("設備拒絕認證", got.Detail);
    }

    [Fact]
    public void 韌體升級的檔名會存為參數()
    {
        var d = AddDevice("大門", "10.0.0.1");

        _ops.Record(d, DeviceOperationTypes.FirmwareUpgrade, "fw-2.3.bin", DeviceOperationResults.Ok);

        var got = Assert.Single(_ops.List(d));
        Assert.Equal("fw-2.3.bin", got.Param);
    }

    [Fact]
    public void 清單只回指定設備的紀錄()
    {
        var a = AddDevice("大門", "10.0.0.1");
        var b = AddDevice("後門", "10.0.0.2");
        _ops.Record(a, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok);
        _ops.Record(b, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok);

        Assert.Single(_ops.List(a));
        Assert.Single(_ops.List(b));
        Assert.Equal(2, _ops.ListAll().Count);
    }

    [Fact]
    public void 每筆操作都會寫入稽核()
    {
        var d = AddDevice("大門", "10.0.0.1");

        _ops.Record(d, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok, actor: "ops1");

        var entries = _audit.List(new AuditLogQuery { Action = "device.operation" });
        var entry = Assert.Single(entries);
        Assert.Equal("ops1", entry.Actor);
        Assert.Equal("device", entry.TargetType);
        Assert.Equal(d, entry.TargetId);
    }

    [Fact]
    public void 刪除設備會連帶清除其操作歷史()
    {
        // 這張表刻意用 CASCADE（不同於群組的 SET NULL）：沒有設備的歷史沒有意義。
        // 追責軌跡改由不隨設備刪除的 audit_log 承擔。
        var d = AddDevice("大門", "10.0.0.1");
        _ops.Record(d, DeviceOperationTypes.Reboot, null, DeviceOperationResults.Ok);

        _devices.Delete(d);

        Assert.Empty(_ops.List(d));
        Assert.Single(_audit.List(new AuditLogQuery { Action = "device.operation" }));
    }

    [Fact]
    public void 上限可限制回傳筆數()
    {
        var d = AddDevice("大門", "10.0.0.1");
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            _ops.Record(d, DeviceOperationTypes.Diagnostics, null, DeviceOperationResults.Ok, occurredAtUtc: t0.AddMinutes(i));
        }

        Assert.Equal(3, _ops.List(d, limit: 3).Count);
    }

    private int AddDevice(string name, string ip)
        => _devices.Add(name, ip, 80, "admin", "pw", "manual");
}
