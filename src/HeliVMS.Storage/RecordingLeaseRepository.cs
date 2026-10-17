namespace HeliVMS.Storage;

/// <summary>
/// 錄影擁有者租約（M321，§11.1 進程分離）。單列（<c>id=1</c>）代表「目前誰在錄影」。
/// <see cref="Priority"/> 較高者可在租約仍有效時搶下（服務 &gt; 桌面），
/// 讓「錄影核心＝背景服務、WPF 為客戶端」得以成立，而不是誰先開誰佔住。
/// </summary>
public sealed record RecordingLease(string OwnerId, int Priority, DateTime ExpiresUtc)
{
    /// <summary>租約是否已過期（含恰好等於到期時刻）。</summary>
    public bool IsExpiredAt(DateTime utcNow) => ExpiresUtc <= utcNow;
}

/// <summary>
/// 錄影擁有者租約儲存介面（M321）。仲裁引擎（<c>RecordingOwnershipCoordinator</c>）不依賴 SQLite，
/// 測試以 in-memory 假時鐘演練；正式以 <see cref="RecordingLeaseRepository"/> 接 SqliteStore 單列狀態表。
/// </summary>
public interface IRecordingLeaseStore
{
    RecordingLease? Get();

    void Upsert(RecordingLease lease);

    void Delete();
}

/// <summary>
/// recording_lease 單列租約存取（M321）。時間以 <see cref="SqliteStore.Iso"/> UTC ISO8601 往返；
/// 以 <c>INSERT OR REPLACE</c> 維持「一筆租約」語意（比照 <see cref="FailoverRepository"/>）。
/// </summary>
public sealed class RecordingLeaseRepository : IRecordingLeaseStore
{
    private readonly SqliteStore _store;

    public RecordingLeaseRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>讀取目前租約；無任何租約→null。</summary>
    public RecordingLease? Get()
    {
        return _store.Query(
            "SELECT owner_id, priority, lease_expires_utc FROM recording_lease WHERE id = 1;",
            static r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                return new RecordingLease(
                    r.GetString(0),
                    r.GetInt32(1),
                    SqliteStore.FromIso(r.GetString(2)));
            });
    }

    /// <summary>寫入／覆寫租約（單列語意，id 固定 1）。</summary>
    public void Upsert(RecordingLease lease)
    {
        _store.Execute(
            """
            INSERT OR REPLACE INTO recording_lease (id, owner_id, priority, lease_expires_utc)
            VALUES (1, $o, $p, $e);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$o", lease.OwnerId);
                cmd.Parameters.AddWithValue("$p", lease.Priority);
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(lease.ExpiresUtc));
            });
    }

    /// <summary>清除租約（自願讓出）。</summary>
    public void Delete()
    {
        _store.Execute("DELETE FROM recording_lease WHERE id = 1;");
    }
}
