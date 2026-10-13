using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>中央節點清單存取（central_nodes 表；§14.1 #10，M295）。</summary>
public sealed class CentralNodeRepository
{
    private const string Columns =
        "id, name, base_url, api_key, enabled, node_id, version, channels, online, last_seen, last_error";

    private readonly SqliteStore _store;

    public CentralNodeRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增一個要監控的子節點，回傳新 ID。</summary>
    public long Add(string name, string baseUrl, string apiKey, bool enabled = true)
    {
        _store.Execute(
            """
            INSERT INTO central_nodes (name, base_url, api_key, enabled, created_at)
            VALUES ($n, $u, $k, $e, $c);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$u", baseUrl);
                cmd.Parameters.AddWithValue("$k", apiKey);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$c", SqliteStore.Iso(DateTime.UtcNow));
            });

        return _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>更新設定值；未提供的欄位保持原值。</summary>
    public void Update(long id, string? name = null, string? baseUrl = null, string? apiKey = null, bool? enabled = null)
    {
        _store.Execute(
            """
            UPDATE central_nodes
            SET name = COALESCE($n, name),
                base_url = COALESCE($u, base_url),
                api_key = COALESCE($k, api_key),
                enabled = COALESCE($e, enabled)
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", (object?)baseUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$k", (object?)apiKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", (object?)(enabled.HasValue ? (enabled.Value ? 1 : 0) : (int?)null) ?? DBNull.Value);
            });
    }

    /// <summary>刪除一筆節點。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM central_nodes WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>
    /// 記錄一次探測結果。
    /// 探測失敗時刻意保留既有的 <c>node_id</c>／<c>version</c>／<c>channels</c>：子節點只是暫時連不上，
    /// 清空會讓 UI 突然顯示成「未知機型」，反而比顯示上次已知狀態更容易誤導。
    /// </summary>
    public void RecordProbe(long id, bool online, DateTime atUtc, NodeStatus? result, string? error)
    {
        _store.Execute(
            """
            UPDATE central_nodes
            SET online = $o,
                last_seen = $t,
                node_id = COALESCE($id, node_id),
                version = COALESCE($v, version),
                channels = COALESCE($c, channels),
                last_error = $e
            WHERE id = $rowid;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$rowid", id);
                cmd.Parameters.AddWithValue("$o", online ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(atUtc));
                cmd.Parameters.AddWithValue("$id", (object?)result?.NodeId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$v", (object?)result?.Version ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)result?.Channels ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", (object?)(online ? null : error) ?? DBNull.Value);
            });
    }

    /// <summary>全部節點。</summary>
    public IReadOnlyList<CentralNode> List()
        => Query(null);

    /// <summary>啟用中的節點（實際會被探測的對象）。</summary>
    public IReadOnlyList<CentralNode> ListEnabled()
        => Query("WHERE enabled = 1");

    private IReadOnlyList<CentralNode> Query(string? where)
    {
        var sql = where is null
            ? $"SELECT {Columns} FROM central_nodes ORDER BY id;"
            : $"SELECT {Columns} FROM central_nodes {where} ORDER BY id;";

        return _store.Query(
            sql,
            static r =>
            {
                var list = new List<CentralNode>();
                while (r.Read())
                {
                    list.Add(new CentralNode(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetString(2),
                        r.GetString(3),
                        r.GetInt32(4) != 0,
                        r.IsDBNull(5) ? null : r.GetString(5),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(7) ? null : r.GetInt32(7),
                        r.IsDBNull(8) ? null : r.GetInt32(8) != 0,
                        r.IsDBNull(9) ? null : SqliteStore.FromIso(r.GetString(9)),
                        r.IsDBNull(10) ? null : r.GetString(10)));
                }

                return list;
            });
    }
}
