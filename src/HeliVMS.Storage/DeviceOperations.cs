namespace HeliVMS.Storage;

/// <summary>設備操作種類（<c>device_operations.op_type</c>，§7.4）。</summary>
public static class DeviceOperationTypes
{
    /// <summary>遠端重啟（ONVIF SystemReboot）。</summary>
    public const string Reboot = "reboot";

    /// <summary>韌體遠端升級（ONVIF StartFirmwareUpgrade／Upload）。</summary>
    public const string FirmwareUpgrade = "firmware_upgrade";

    /// <summary>訊號／裝置診斷（ONVIF 資訊與 clock skew）。</summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>音視探測（以 ffprobe 探測主碼流的解析度、編碼與音訊）。</summary>
    public const string AvTest = "av_test";
}

/// <summary>設備操作結果（<c>device_operations.result</c>）。</summary>
public static class DeviceOperationResults
{
    public const string Ok = "ok";
    public const string Failed = "failed";
}

/// <summary>單筆設備操作紀錄（§7.4 遠端操作歷史）。</summary>
/// <param name="Id">紀錄 Id。</param>
/// <param name="DeviceId">操作對象設備。</param>
/// <param name="OpType">操作種類，見 <see cref="DeviceOperationTypes"/>。</param>
/// <param name="Param">操作參數（如韌體檔名）；無則 <see langword="null"/>。</param>
/// <param name="Result">結果，見 <see cref="DeviceOperationResults"/>。</param>
/// <param name="Detail">補充說明（錯誤訊息或摘要）；無則 <see langword="null"/>。</param>
/// <param name="CreatedAt">成立時間（UTC）。</param>
public sealed record DeviceOperation(
    long Id,
    int DeviceId,
    string OpType,
    string? Param,
    string Result,
    string? Detail,
    DateTime CreatedAt);

/// <summary>
/// 設備操作歷史倉儲（v51，§7.4）。每筆命令除了寫入 <c>device_operations</c>，
/// 也寫一則 <c>device.operation</c> 稽核，兩者用途不同：
///
/// <para>
/// <c>device_operations</c> 是「這台設備的歷史」，隨設備 CASCADE 刪除；
/// <c>audit_log</c> 是「誰在何時對什麼做了什麼」，不隨設備刪除，是事後追責的依據。
/// 只留其一都不夠——設備刪掉後仍需回答「上次是誰重啟的」。
/// </para>
/// <para>
/// 操作會直接影響現場攝影機（重啟會中斷畫面、升級可能刷壞），所以成敗一律留下紀錄，
/// 失敗也要寫，否則「按了沒反應」將無從追查。
/// </para>
/// </summary>
public sealed class DeviceOperationRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public DeviceOperationRepository(SqliteStore store, AuditLogRepository audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>
    /// 寫入一筆操作紀錄（並掛稽核 <c>device.operation</c>），回傳新紀錄 Id。
    /// </summary>
    /// <param name="deviceId">設備 Id（外鍵，必須存在）。</param>
    /// <param name="opType">操作種類。</param>
    /// <param name="param">操作參數（可為 <see langword="null"/>）。</param>
    /// <param name="result">結果（<see cref="DeviceOperationResults"/>）。</param>
    /// <param name="detail">補充說明（可為 <see langword="null"/>）。</param>
    /// <param name="actor">操作者；<see langword="null"/> 時由稽核層帶入預設。</param>
    /// <param name="occurredAtUtc">成立時間；<see langword="null"/> 時取當下 UTC。</param>
    public long Record(
        int deviceId,
        string opType,
        string? param,
        string result,
        string? detail = null,
        string? actor = null,
        DateTime? occurredAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opType);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        var at = occurredAtUtc ?? DateTime.UtcNow;
        var id = _store.Query(
            """
            INSERT INTO device_operations (device_id, op_type, param, result, detail, created_at)
            VALUES ($d, $t, $p, $r, $det, $at);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$t", opType);
                cmd.Parameters.AddWithValue("$p", (object?)param ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r", result);
                cmd.Parameters.AddWithValue("$det", (object?)detail ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", SqliteStore.Iso(at));
            });

        // 稽核只記種類與結果，不記 detail（可能含設備原始錯誤訊息）。
        _audit.Record(actor, "device.operation", AuditCategories.Config, "device", deviceId, $"{opType}={result}");
        return id;
    }

    /// <summary>列出指定設備的操作紀錄（最新在前）；<paramref name="limit"/> 為上限。</summary>
    public IReadOnlyList<DeviceOperation> List(int deviceId, int limit = 50)
    {
        if (limit <= 0)
        {
            limit = 1;
        }

        return _store.Query(
            """
            SELECT id, device_id, op_type, param, result, detail, created_at
            FROM device_operations
            WHERE device_id = $d
            ORDER BY created_at DESC, id DESC
            LIMIT $n;
            """,
            Read,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$n", limit);
            });
    }

    /// <summary>列出所有設備的操作紀錄（最新在前）；供全域檢視／除錯。</summary>
    public IReadOnlyList<DeviceOperation> ListAll(int limit = 200)
    {
        if (limit <= 0)
        {
            limit = 1;
        }

        return _store.Query(
            """
            SELECT id, device_id, op_type, param, result, detail, created_at
            FROM device_operations
            ORDER BY created_at DESC, id DESC
            LIMIT $n;
            """,
            Read,
            cmd => cmd.Parameters.AddWithValue("$n", limit));
    }

    private static List<DeviceOperation> Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var list = new List<DeviceOperation>();
        while (r.Read())
        {
            list.Add(new DeviceOperation(
                r.GetInt64(0),
                r.GetInt32(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                SqliteStore.FromIso(r.GetString(6))));
        }

        return list;
    }
}
