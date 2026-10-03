using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 設備管理（§7：專業 IP 攝影機；§4 devices 表，密碼以 DPAPI 加密儲存）。
/// </summary>
public sealed class DeviceRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public DeviceRepository(SqliteStore store, AuditLogRepository audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>列出全部設備。</summary>
    public IReadOnlyList<DeviceRecord> List()
    {
        return _store.Query(
            """
            SELECT id, name, ip, port, vendor, enabled, created_at, sd_url
            FROM devices ORDER BY id;
            """,
            static r =>
            {
                var list = new List<DeviceRecord>();
                while (r.Read())
                {
                    list.Add(new DeviceRecord
                    {
                        Id = r.GetInt32(0),
                        Name = r.GetString(1),
                        Ip = r.GetString(2),
                        Port = r.GetInt32(3),
                        Vendor = r.GetString(4),
                        Enabled = r.GetInt32(5) != 0,
                        CreatedAt = SqliteStore.FromIso(r.GetString(6)),
                        SdUrl = r.IsDBNull(7) ? null : r.GetString(7),
                    });
                }

                return list;
            });
    }

    /// <summary>依 ID 取得單一設備（含帳密欄位，供 ONVIF 直連）；無此設備回傳 null。</summary>
    public DeviceRecord? Get(int id)
    {
        return _store.Query(
            """
            SELECT id, name, ip, port, username, password_encrypted, vendor, enabled, created_at, sd_url
            FROM devices WHERE id = $id;
            """,
            static r => r.Read() ? ReadFull(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>
    /// 依 IP 取得單一設備（含帳密欄位）。<c>devices.ip</c> 為 UNIQUE，
    /// 故加入頻道時可先以此查找，避免重複加入同一台攝影機而觸發唯一性限制。
    /// </summary>
    public DeviceRecord? FindByIp(string ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        return _store.Query(
            """
            SELECT id, name, ip, port, username, password_encrypted, vendor, enabled, created_at, sd_url
            FROM devices WHERE ip = $ip;
            """,
            static r => r.Read() ? ReadFull(r) : null,
            cmd => cmd.Parameters.AddWithValue("$ip", ip));
    }

    /// <summary>取得設備的 RTSP 帳密（密碼已 DPAPI 解密）；未綁定、無帳號或解密失敗時回傳空字串。</summary>
    public (string Username, string Password) GetRtspCredentials(int id)
    {
        var device = Get(id);
        if (device is null || string.IsNullOrWhiteSpace(device.Username))
        {
            return (string.Empty, string.Empty);
        }

        return string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? (device.Username, string.Empty)
            : (device.Username, SafeUnprotect(device.PasswordEncrypted));
    }

    /// <summary>
    /// DPAPI 解密；失敗（跨機器／舊明文）時原樣回傳，不讓拉流中斷。非 Windows 原樣回傳。
    /// 供需要單獨解密的地方（如 PTZ 視窗）使用，避免直接呼叫 <see cref="Unprotect"/> 因例外而中斷。
    /// </summary>
    public static string SafeUnprotect(string stored)
    {
        if (!OperatingSystem.IsWindows())
        {
            return stored;
        }

        try
        {
            return Unprotect(stored);
        }
        catch (Exception)
        {
            return stored;
        }
    }

    private static DeviceRecord ReadFull(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new()
        {
            Id = r.GetInt32(0),
            Name = r.GetString(1),
            Ip = r.GetString(2),
            Port = r.GetInt32(3),
            Username = r.IsDBNull(4) ? null : r.GetString(4),
            PasswordEncrypted = r.IsDBNull(5) ? null : r.GetString(5),
            Vendor = r.GetString(6),
            Enabled = r.GetInt32(7) != 0,
            CreatedAt = SqliteStore.FromIso(r.GetString(8)),
            SdUrl = r.IsDBNull(9) ? null : r.GetString(9),
        };

    /// <summary>新增設備（M152 稽核掛載：device.add 寫 audit_log），回傳新 ID。密碼以 DPAPI（目前使用者）加密後儲存。</summary>
    public int Add(string name, string ip, int port, string username, string password, string vendor, string actor = "system")
    {
        var id = _store.Query(
            """
            INSERT INTO devices (name, ip, port, username, password_encrypted, vendor)
            VALUES ($n, $i, $p, $u, $pw, $v);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$i", ip);
                cmd.Parameters.AddWithValue("$p", port);
                cmd.Parameters.AddWithValue("$u", (object?)username ?? DBNull.Value);
                cmd.Parameters.AddWithValue(
                    "$pw",
                    string.IsNullOrEmpty(password)
                        ? DBNull.Value
                        : OperatingSystem.IsWindows() ? Protect(password) : password);
                cmd.Parameters.AddWithValue("$v", vendor);
            });

        _audit.Record(actor, "device.add", AuditCategories.Config, "device", id, $"{name}@{ip}:{port}");
        return id;
    }

    /// <summary>
    /// 更新設備的 RTSP 憑證。既有記錄可能因手誤或舊版缺漏而存有錯誤／空白密碼，
    /// 重新以精靈加入同一台攝影機時需能就地修正，否則會沿用壞掉的憑證而無法出畫面。
    /// 密碼以 DPAPI（目前使用者）加密後儲存；回傳是否命中。
    /// </summary>
    public bool SetRtspCredentials(int id, string username, string password, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            UPDATE devices SET username = $u, password_encrypted = $pw
            WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$u", (object?)username ?? DBNull.Value);
                cmd.Parameters.AddWithValue(
                    "$pw",
                    string.IsNullOrEmpty(password)
                        ? DBNull.Value
                        : OperatingSystem.IsWindows() ? Protect(password) : password);
            });

        if (affected > 0)
        {
            // 稽核只記帳號，不記密碼。
            _audit.Record(actor, "device.credentials", AuditCategories.Config, "device", id, $"user={username}");
        }

        return affected > 0;
    }

    /// <summary>刪除設備（其通道連帶刪除，ON DELETE CASCADE；M152 稽核掛載：device.delete 寫 audit_log）。</summary>
    public void Delete(int id, string actor = "system")
    {
        var name = Get(id)?.Name ?? $"id:{id}";
        _store.Execute("DELETE FROM devices WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        _audit.Record(actor, "device.delete", AuditCategories.Config, "device", id, name);
    }

    /// <summary>更新設備基本欄位（M152：原無 Update）；回傳是否命中。稽核掛載：device.update 寫 audit_log。</summary>
    public bool Update(int id, string name, string ip, int port, string vendor, bool enabled, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            UPDATE devices SET name = $n, ip = $i, port = $p, vendor = $v, enabled = $en
            WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$i", ip);
                cmd.Parameters.AddWithValue("$p", port);
                cmd.Parameters.AddWithValue("$v", vendor);
                cmd.Parameters.AddWithValue("$en", enabled ? 1 : 0);
            });

        if (affected > 0)
        {
            _audit.Record(actor, "device.update", AuditCategories.Config, "device", id, $"{name}@{ip}:{port} enabled={enabled}");
        }

        return affected > 0;
    }

    /// <summary>
    /// 設定設備的 SD 側錄／回放串流位址（M94 邊緣補抓）；空白視為清除。稽核掛載：device.sd_url。
    /// </summary>
    public bool SetSdUrl(int id, string? sdUrl, string actor = "system")
    {
        var cleared = string.IsNullOrWhiteSpace(sdUrl);
        var affected = _store.Query<int>(
            """
            UPDATE devices SET sd_url = $u WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$u", cleared ? DBNull.Value : sdUrl);
            });

        if (affected > 0)
        {
            _audit.Record(actor, "device.sd_url", AuditCategories.Config, "device", id, cleared ? "(cleared)" : "set");
        }

        return affected > 0;
    }

    /// <summary>
    /// 以 DPAPI（目前使用者，Windows）加密秘密。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string Protect(string secret) =>
        Convert.ToBase64String(
            System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(secret),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

    /// <summary>解開 DPAPI 加密（僅 Windows）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string Unprotect(string cipherBase64) =>
        System.Text.Encoding.UTF8.GetString(
            System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(cipherBase64),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
}