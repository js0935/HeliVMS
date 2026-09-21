namespace HeliVMS.Storage;

/// <summary>
/// failover_state 單列租約存取（M87，§14.7 #9）。共享儲存上的單列代表全域 leader，
/// 以 id=1 固定單列（INSERT OR REPLACE）維持「一筆租約」語意；時間以 <see cref="SqliteStore.Iso"/>
/// UTC ISO8601 往返。
/// </summary>
public sealed class FailoverRepository : IFailoverLeaseStore
{
    private readonly SqliteStore _store;

    public FailoverRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>讀取目前租約；無任何租約→null。</summary>
    public FailoverLease? GetLease()
    {
        return _store.Query(
            "SELECT server_id, lease_expires_utc FROM failover_state WHERE id = 1;",
            static r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                return new FailoverLease(
                    r.GetString(0),
                    SqliteStore.FromIso(r.GetString(1)));
            });
    }

    /// <summary>寫入／覆寫租約（單列語意，id 固定 1）。</summary>
    public void Upsert(FailoverLease lease)
    {
        _store.Execute(
            """
            INSERT OR REPLACE INTO failover_state (id, server_id, lease_expires_utc)
            VALUES (1, $s, $e);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", lease.ServerId);
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(lease.ExpiresUtc));
            });
    }

    /// <summary>清除租約（釋出 leader）。</summary>
    public void Delete()
    {
        _store.Execute("DELETE FROM failover_state WHERE id = 1;");
    }
}