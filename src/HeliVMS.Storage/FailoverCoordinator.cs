namespace HeliVMS.Storage;

/// <summary>容錯角色（M87，§14.7 #9）：Leader＝持有有效租約之主伺服器；Standby＝有效租約屬他人；
/// None＝無租約或租約已過期。</summary>
public enum FailoverRole
{
    /// <summary>無租約或租約已過期。</summary>
    None,

    /// <summary>持有有效租約之主伺服器。</summary>
    Leader,

    /// <summary>他人持有有效租約，等待接管。</summary>
    Standby,
}

/// <summary>failover_state 單列租約（M87）。</summary>
public sealed record FailoverLease(string ServerId, DateTime ExpiresUtc)
{
    /// <summary>租約是否已過期（含恰好等於到期時刻）。</summary>
    public bool IsExpiredAt(DateTime utcNow) => ExpiresUtc <= utcNow;
}

/// <summary>容錯狀態快照（M87）：供 UI／診斷顯示相對自身的角色與剩餘時間。</summary>
public sealed record FailoverStatus(
    FailoverRole Role,
    string? LeaderId,
    string ServerId,
    TimeSpan LeaseRemaining,
    DateTime? LeaseExpiresUtc);

/// <summary>
/// 租約儲存介面（M87，§14.7 #9）：L0 引擎不依賴 SQLite，測試以 in-memory 實作假時鐘演練；
/// 正式以 <see cref="FailoverRepository"/> 接 SqliteStore 單列狀態表。
/// </summary>
public interface IFailoverLeaseStore
{
    FailoverLease? GetLease();

    void Upsert(FailoverLease lease);

    void Delete();
}

/// <summary>
/// Failover 容錯 L0（M87，§14.7 #9，純 BCL、時鐘注入）：
/// 以「共用儲存上的租約」作為主備仲裁——空庫即成為 Leader；租約有效且屬他人→Standby；
/// 租約過期→接管成為新 Leader；同 server 重試→續約延後到期。多節點共用同一儲存時，
/// SQLite/WAL 序列化單一 upsert 即為原子仲裁（與 Milestone/Milestone Failover 之 lease 語意一致）。
/// </summary>
public sealed class FailoverCoordinator
{
    private readonly IFailoverLeaseStore _store;
    private readonly string _serverId;

    public FailoverCoordinator(IFailoverLeaseStore store, string serverId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("伺服器識別不可為空。", nameof(serverId));
        }

        _serverId = serverId;
    }

    /// <summary>
    /// 嘗試取得／續約 Leader 租約。成功（空庫、同 server、或他人租約已過期）回 Leader；
    /// 他人有效租約存在回 Standby。
    /// </summary>
    public FailoverRole AcquireOrRenew(TimeSpan leaseDuration, DateTime utcNow)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租約期間須為正。");
        }

        var current = _store.GetLease();
        if (current is null || current.IsExpiredAt(utcNow) || current.ServerId == _serverId)
        {
            _store.Upsert(new FailoverLease(_serverId, utcNow + leaseDuration));
            return FailoverRole.Leader;
        }

        return FailoverRole.Standby;
    }

    /// <summary>目前角色與租約狀態（相對本機 serverId）。</summary>
    public FailoverStatus GetStatus(DateTime utcNow)
    {
        var lease = _store.GetLease();
        if (lease is null)
        {
            return new FailoverStatus(FailoverRole.None, null, _serverId, TimeSpan.Zero, null);
        }

        var expired = lease.IsExpiredAt(utcNow);
        var role = expired
            ? FailoverRole.None
            : lease.ServerId == _serverId
                ? FailoverRole.Leader
                : FailoverRole.Standby;
        var remaining = expired ? TimeSpan.Zero : lease.ExpiresUtc - utcNow;
        return new FailoverStatus(role, lease.ServerId, _serverId, remaining, lease.ExpiresUtc);
    }

    /// <summary>
    /// 自願讓出（僅現任 Leader 有效租約可清除）。已是 Standby/無租約→false 且不改寫他人租約。
    /// </summary>
    public bool Release(DateTime utcNow)
    {
        var current = _store.GetLease();
        if (current is null || current.IsExpiredAt(utcNow) || current.ServerId != _serverId)
        {
            return false;
        }

        _store.Delete();
        return true;
    }
}