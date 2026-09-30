namespace HeliVMS.Storage;

/// <summary>授權狀態（§19.7 <c>license.status</c>）。</summary>
public static class LicenseStatuses
{
    /// <summary>有效。</summary>
    public const string Active = "active";

    /// <summary>已到期（停止新增錄影，既有錄影仍可回放，§19.4）。</summary>
    public const string Expired = "expired";

    /// <summary>偵測到時鐘被改回（§19.4 回流時鐘防護），已停用待校時。</summary>
    public const string TimeRollback = "time_rollback";

    /// <summary>已作廢。</summary>
    public const string Revoked = "revoked";

    /// <summary>
    /// 已失效：啟動時重新驗證發現授權碼簽章或機器綁定不符（M213）。
    /// </summary>
    /// <remarks>
    /// 必須有這個狀態：<c>license</c> 列的 <c>features</c>／<c>max_cameras</c> 是
    /// <c>Evaluate()</c> 唯一的判斷依據，若只是記一筆稽核就放著不動，使用者手改資料庫
    /// 就能把 4 路基本版改成 1024 路客製版。標記失效後錄影閘門一律拒絕，重新匯入合法授權才恢復。
    /// </remarks>
    public const string Invalid = "invalid";
}

/// <summary>已匯入之授權（§19.7 <c>license</c> 表，v43）。</summary>
public sealed record LicenseRecord(
    long Id,
    string KeyText,
    string DeviceCode,
    string? LicenseId,
    string? Tier,
    int MaxCameras,
    IReadOnlyList<string> Features,
    string? Issuer,
    DateTime? ExpiresUtc,
    DateTime FirstSeenUtc,
    DateTime? LastVerifiedUtc,
    string Status,
    string? CreatedBy)
{
    /// <summary>是否仍可錄影（到期後僅停新增，既有檔可回放）。</summary>
    public bool IsActive => Status == LicenseStatuses.Active;
}

/// <summary>
/// 授權存取（§19.7）。同一 <c>device_code</c> 只保留一列：換發（改版）即覆寫授權碼與快取欄並保留
/// <c>first_seen</c>，<c>last_verified</c> 每次成功驗證更新。
/// <para>每次寫入皆掛稽核（<see cref="AuditCategories.License"/>），滿足 §19.8「授權啟用/到期/改版
/// 事件完整寫入稽核日誌」。</para>
/// </summary>
public sealed class LicenseRepository
{
    private const string Columns =
        "id, key_text, device_code, license_id, tier, max_cameras, features, issuer, " +
        "expired_at, first_seen, last_verified, status, created_by";

    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public LicenseRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    /// <summary>
    /// 匯入／換發授權（upsert by device_code）。回傳列 ID。
    /// <paramref name="status"/> 決定該列現況（有效／到期／時鐘回流／作廢）。
    /// </summary>
    public long Upsert(
        string keyText,
        string deviceCode,
        string? licenseId,
        string? tier,
        int maxCameras,
        IReadOnlyList<string> features,
        string? issuer,
        DateTime? expiresUtc,
        string status,
        string? createdBy,
        DateTime nowUtc,
        bool markVerified = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyText);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCameras);

        var now = SqliteStore.Iso(nowUtc);
        var expires = expiresUtc is { } e ? SqliteStore.Iso(e) : null;
        var featureText = string.Join(',', features);
        var code = deviceCode.ToUpperInvariant();
        var replaced = GetByDeviceCode(deviceCode);

        var id = _store.Query(
            """
            INSERT INTO license
                (key_text, device_code, license_id, tier, max_cameras, features, issuer,
                 expired_at, first_seen, last_verified, status, created_by)
            VALUES
                ($key, $code, $lic, $tier, $max, $feat, $issuer, $exp, $now, $ver, $st, $by)
            ON CONFLICT(device_code) DO UPDATE SET
                key_text      = $key,
                license_id    = $lic,
                tier          = $tier,
                max_cameras   = $max,
                features      = $feat,
                issuer        = $issuer,
                expired_at    = $exp,
                last_verified = COALESCE($ver, last_verified),
                status        = $st,
                created_by    = $by;
            SELECT id FROM license WHERE device_code = $code;
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$key", keyText);
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$lic", (object?)licenseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tier", (object?)tier ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$max", maxCameras);
                cmd.Parameters.AddWithValue("$feat", featureText);
                cmd.Parameters.AddWithValue("$issuer", (object?)issuer ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$exp", (object?)expires ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$ver", markVerified ? now : DBNull.Value);
                cmd.Parameters.AddWithValue("$st", status);
                cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
            });

        AuditUpsert(id, replaced, keyText, code, licenseId, tier, maxCameras, expiresUtc, status, createdBy, nowUtc);
        return id;
    }

    /// <summary>成功驗證後更新 <c>last_verified</c>（不改變 status）。</summary>
    public void RecordVerified(long id, DateTime nowUtc)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "授權 ID 須為正整數。");
        }

        _store.Execute(
            "UPDATE license SET last_verified = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(nowUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>更新 status（如到期、時鐘回流停用）並留稽核。</summary>
    public void SetStatus(long id, string status, string actor, DateTime nowUtc, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "授權 ID 須為正整數。");
        }

        _store.Execute(
            "UPDATE license SET status = $s WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$id", id);
            });

        _audit.Record(
            actor,
            "license.status",
            AuditCategories.License,
            targetType: "license",
            targetId: id,
            detail: detail is null ? status : $"{status} {detail}",
            occurredAtUtc: nowUtc);
    }

    /// <summary>作廢授權（保留列與稽核，非刪除）。</summary>
    public bool Revoke(long id, string actor, string reason, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "授權 ID 須為正整數。");
        }

        var changed = _store.Query(
            """
            UPDATE license SET status = $s WHERE id = $id;
            SELECT changes();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", LicenseStatuses.Revoked);
                cmd.Parameters.AddWithValue("$id", id);
            });
        if (changed == 0)
        {
            return false;
        }

        _audit.Record(
            actor,
            "license.revoke",
            AuditCategories.License,
            targetType: "license",
            targetId: id,
            detail: reason,
            occurredAtUtc: nowUtc);
        return true;
    }

    /// <summary>依綁定設備碼讀取（大小寫不敏感，簽發端可能以小寫記錄）。</summary>
    public LicenseRecord? GetByDeviceCode(string deviceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        return _store.Query(
            $"SELECT {Columns} FROM license WHERE device_code = $c LIMIT 1;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$c", deviceCode.ToUpperInvariant()));
    }

    public LicenseRecord? Get(long id)
        => _store.Query(
            $"SELECT {Columns} FROM license WHERE id = $id;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));

    /// <summary>全部授權，最後驗證者優先。</summary>
    public IReadOnlyList<LicenseRecord> List()
        => _store.Query(
            $"SELECT {Columns} FROM license ORDER BY COALESCE(last_verified, first_seen) DESC;",
            static r =>
            {
                var list = new List<LicenseRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            });

    /// <summary>有效狀態之授權（供啟動時取用）。</summary>
    public IReadOnlyList<LicenseRecord> ListActive()
        => _store.Query(
            $"SELECT {Columns} FROM license WHERE status = $s ORDER BY id;",
            static r =>
            {
                var list = new List<LicenseRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$s", LicenseStatuses.Active));

    /// <summary>刪除（僅測試／重置用；正式作廢走 <see cref="Revoke"/>）。</summary>
    public int DeleteAll()
        => _store.Query(
            """
            DELETE FROM license;
            SELECT changes();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });

    private void AuditUpsert(
        long id,
        LicenseRecord? replaced,
        string keyText,
        string deviceCode,
        string? licenseId,
        string? tier,
        int maxCameras,
        DateTime? expiresUtc,
        string status,
        string? createdBy,
        DateTime nowUtc)
    {
        var actor = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy;
        var detail = $"device={deviceCode} tier={tier ?? "自訂"} max={maxCameras} status={status}";

        if (replaced is null)
        {
            _audit.Record(
                actor,
                "license.activate",
                AuditCategories.License,
                targetType: "license",
                targetId: id,
                detail: licenseId is null ? detail : $"{detail} id={licenseId}",
                occurredAtUtc: nowUtc);
            return;
        }

        // 換發：同一台機器換了一張授權（改版／續期），逐項比對留下差異供追查。
        var changes = new List<string>();
        if (!string.Equals(replaced.KeyText, keyText, StringComparison.Ordinal))
        {
            changes.Add("金鑰已更換");
        }

        if (replaced.MaxCameras != maxCameras)
        {
            changes.Add($"通道 {replaced.MaxCameras}→{maxCameras}");
        }

        if (!string.Equals(replaced.Tier, tier, StringComparison.Ordinal))
        {
            changes.Add($"等級 {replaced.Tier ?? "自訂"}→{tier ?? "自訂"}");
        }

        if (!string.Equals(replaced.Status, status, StringComparison.Ordinal))
        {
            changes.Add($"狀態 {replaced.Status}→{status}");
        }

        // 續期／延長到期日也要留痕；重新驗證本身不記（每次啟動都會發生，記錄會淹沒
        // 真正的啟用／到期／改版事件，§19.8 要的是後者）。last_verified 已反映驗證時點。
        var expires = expiresUtc is { } e ? SqliteStore.Iso(e) : null;
        var prevExpires = replaced.ExpiresUtc is { } pe ? SqliteStore.Iso(pe) : null;
        if (!string.Equals(prevExpires, expires, StringComparison.Ordinal))
        {
            changes.Add($"到期 {prevExpires ?? "永久"}→{expires ?? "永久"}");
        }

        if (changes.Count == 0)
        {
            return;
        }

        _audit.Record(
            actor,
            changes.Contains("金鑰已更換") ? "license.upgrade" : "license.update",
            AuditCategories.License,
            targetType: "license",
            targetId: id,
            detail: licenseId is null
                ? string.Join('；', changes)
                : $"{licenseId} {string.Join('；', changes)}",
            occurredAtUtc: nowUtc);
    }

    private static LicenseRecord Read(Microsoft.Data.Sqlite.SqliteDataReader r)
        => new(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetInt32(5),
            r.IsDBNull(6) || r.GetString(6).Length == 0
                ? []
                : r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : SqliteStore.FromIso(r.GetString(8)),
            SqliteStore.FromIso(r.GetString(9)),
            r.IsDBNull(10) ? null : SqliteStore.FromIso(r.GetString(10)),
            r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12));
}
