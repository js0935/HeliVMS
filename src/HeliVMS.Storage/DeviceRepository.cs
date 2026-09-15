using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 設備管理（§7：專業 IP 攝影機；§4 devices 表，密碼以 DPAPI 加密儲存）。
/// </summary>
public sealed class DeviceRepository
{
    private readonly SqliteStore _store;

    public DeviceRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>列出全部設備。</summary>
    public IReadOnlyList<DeviceRecord> List()
    {
        return _store.Query(
            """
            SELECT id, name, ip, port, vendor, enabled, created_at
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
            SELECT id, name, ip, port, username, password_encrypted, vendor, enabled, created_at
            FROM devices WHERE id = $id;
            """,
            static r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                return new DeviceRecord
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
                };
            },
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>新增設備，回傳新 ID。密碼以 DPAPI（目前使用者）加密後儲存。</summary>
    public int Add(string name, string ip, int port, string username, string password, string vendor)
    {
        return _store.Query(
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
                cmd.Parameters.AddWithValue("$u", username);
                cmd.Parameters.AddWithValue(
                    "$pw",
                    string.IsNullOrEmpty(password) ? null
                        : OperatingSystem.IsWindows() ? Protect(password) : password);
                cmd.Parameters.AddWithValue("$v", vendor);
            });
    }

    /// <summary>刪除設備（其通道連帶刪除，ON DELETE CASCADE）。</summary>
    public void Delete(int id)
    {
        _store.Execute("DELETE FROM devices WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
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