using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 企業身份提供者（M50，§14.7 #1）。<c>ConfigJson</c> 依 <c>Kind</c> 存
/// <see cref="OidcOptions"/>（oidc）或 <see cref="LdapSettings"/>（ldap）。
/// </summary>
public sealed record AuthProviderRecord(
    int Id,
    string Name,
    string Kind,
    bool Enabled,
    string ConfigJson,
    string CreatedAt);

/// <summary>企業身份提供者存取（M50，§14.7 #1）。</summary>
public sealed class AuthProviderRepository
{
    public const string KindOidc = "oidc";
    public const string KindLdap = "ldap";

    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    /// <summary>
    /// 預設自建 <see cref="AuditLogRepository"/>（同 <c>ChannelRepository</c>）：企業身份來源
    /// 的新增／啟停／刪除會改變誰能登入，屬安全相關管理動作，一律留稽核軌跡（僅記名稱/類型，不記密鑰 JSON）。
    /// </summary>
    public AuthProviderRepository(SqliteStore store, AuditLogRepository? audit = null)
    {
        _store = store;
        _audit = audit ?? new AuditLogRepository(store);
    }

    /// <summary>新增提供者（name 唯一；重複拋 SqliteException）。回傳新 ID。稽核：provider.add。</summary>
    public int Add(string name, string kind, string configJson, bool enabled = true, string? actor = null)
    {
        ValidateKind(kind);
        var id = _store.Query(
            """
            INSERT INTO auth_providers (name, kind, enabled, config_json, created_at)
            VALUES ($n, $k, $e, $c, $t);
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
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$c", configJson);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(DateTime.UtcNow));
            });

        _audit.Record(actor, "provider.add", AuditCategories.Config, "auth_provider", id, $"kind={kind} name={name}");
        return id;
    }

    /// <summary>列出全部提供者（依 name）。</summary>
    public IReadOnlyList<AuthProviderRecord> List()
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, config_json, created_at
            FROM auth_providers ORDER BY name COLLATE NOCASE;
            """,
            static r =>
            {
                var list = new List<AuthProviderRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            });
    }

    /// <summary>列出指定種類且啟用的提供者。</summary>
    public IReadOnlyList<AuthProviderRecord> ListEnabled(string kind)
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, config_json, created_at
            FROM auth_providers WHERE kind = $k AND enabled = 1
            ORDER BY name COLLATE NOCASE;
            """,
            static r =>
            {
                var list = new List<AuthProviderRecord>();
                while (r.Read())
                {
                    list.Add(Read(r));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$k", kind));
    }

    public AuthProviderRecord? Get(int id)
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, config_json, created_at
            FROM auth_providers WHERE id = $id;
            """,
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    public AuthProviderRecord? GetByName(string name)
    {
        return _store.Query(
            """
            SELECT id, name, kind, enabled, config_json, created_at
            FROM auth_providers WHERE name = $n COLLATE NOCASE LIMIT 1;
            """,
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$n", name));
    }

    /// <summary>啟用/停用提供者。稽核：provider.enable／provider.disable。</summary>
    public void SetEnabled(int id, bool enabled, string? actor = null)
    {
        var affected = _store.Query<int>(
            "UPDATE auth_providers SET enabled = $e WHERE id = $id; SELECT changes();",
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });

        if (affected > 0)
        {
            _audit.Record(actor, enabled ? "provider.enable" : "provider.disable",
                AuditCategories.Config, "auth_provider", id, enabled ? "enabled" : "disabled");
        }
    }

    /// <summary>刪除提供者。稽核：provider.delete。</summary>
    public void Delete(int id, string? actor = null)
    {
        var name = Get(id)?.Name ?? $"id:{id}";
        var affected = _store.Query<int>(
            "DELETE FROM auth_providers WHERE id = $id; SELECT changes();",
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$id", id));

        if (affected > 0)
        {
            _audit.Record(actor, "provider.delete", AuditCategories.Config, "auth_provider", id, name);
        }
    }

    private static void ValidateKind(string kind)
    {
        if (!string.Equals(kind, KindOidc, StringComparison.Ordinal) &&
            !string.Equals(kind, KindLdap, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "種類僅允許 oidc/ldap");
        }
    }

    private static AuthProviderRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetInt32(3) != 0,
            r.GetString(4),
            r.GetString(5));
}
