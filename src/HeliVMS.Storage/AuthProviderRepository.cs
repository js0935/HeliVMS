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

    public AuthProviderRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增提供者（name 唯一；重複拋 SqliteException）。回傳新 ID。</summary>
    public int Add(string name, string kind, string configJson, bool enabled = true)
    {
        ValidateKind(kind);
        return _store.Query(
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

    public void SetEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE auth_providers SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    public void Delete(int id)
    {
        _store.Execute(
            "DELETE FROM auth_providers WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
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
