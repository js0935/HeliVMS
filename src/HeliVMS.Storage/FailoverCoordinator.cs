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

/// <summary>接管事件模式（M98，§14.7 #9 L1）：當選／接管／交還，落 failover_events 軌跡。</summary>
public enum FailoverEventMode
{
    /// <summary>無前手租約下獲選為 Leader（首次或釋出後重選）。</summary>
    Leader,

    /// <summary>由 Standby 於租約逾時後接管成為 Leader。</summary>
    Takeover,

    /// <summary>Leader 名義旁落（他人持有)或自願交還，退回 Standby。</summary>
    Relinquish,
}

/// <summary>failover 接管事件（M98）：軌跡列（附表 failover_events v35）。</summary>
public sealed record FailoverEvent(long Id, string ServerId, FailoverEventMode Mode, string Detail, DateTime AtUtc);

/// <summary>
/// 實體接管控制器（M98）：租約升遷/旁落時對本機錄影上下文執行的動作——測試注入 fake 記錄調用；
/// 正式整合（WPF）以啟用/停用錄影與串流實作。
/// </summary>
public interface IFailoverRoleController
{
    /// <summary>本機開始作為 Leader 運作（接管錄影/串流上下文）。</summary>
    void TakeOver(DateTime utcNow);

    /// <summary>本機停止 Leader 運作（交還上下文）。</summary>
    void Relinquish(DateTime utcNow);
}

/// <summary>接管事件軌跡寫入（M98）。</summary>
public interface IFailoverEventLog
{
    /// <summary>追加一筆事件並回傳 id。</summary>
    long Append(string serverId, FailoverEventMode mode, string detail, DateTime atUtc);
}

/// <summary>不做事之角色控制器（預設，供協調器主體以外使用）。</summary>
public sealed class NoopFailoverRoleController : IFailoverRoleController
{
    public void TakeOver(DateTime utcNow) { }

    public void Relinquish(DateTime utcNow) { }
}

/// <summary>不做事之事件軌跡（預設）。</summary>
public sealed class NoopFailoverEventLog : IFailoverEventLog
{
    public long Append(string serverId, FailoverEventMode mode, string detail, DateTime atUtc) => 0;
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
    private FailoverRole _lastRole = FailoverRole.None;

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
            _lastRole = FailoverRole.Leader;
            return FailoverRole.Leader;
        }

        _lastRole = FailoverRole.Standby;
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
        _lastRole = FailoverRole.None;
        return true;
    }

    /// <summary>
    /// 接管協調（M98，§14.7 #9 L1）：以心跳式呼叫驅動實體接管狀態機——
    /// 每次呼叫依租約現況決定目標角色，僅在「角色轉變」時執行動作與寫軌跡：
    /// 空庫/自身租約→當選或續約（Leader）；他人有效租約→退避（Standby、不再動作）；
    /// 他人租約逾時且逾窗 ≥<paramref name="missingWindow"/>→接管（TakeOver）；未逾窗→仍 Standby（寬限等待）。
    /// 「旁落」偵測：本機上輪為 Leader 而租約已易主/逾窗→Relinquish 交還上下文。
    /// </summary>
    /// <remarks>
    /// 動作與軌跡只在轉變時觸發：Steady 續約不重複 TakeOver、不濫寫事件。租約寫入（Upsert）本身
    /// 即原子競選點（與 M87 相同、SQLite/WAL 序列化），控制器動作在競選成功後才執行。
    /// </remarks>
    public FailoverRole Reconcile(
        TimeSpan leaseDuration,
        TimeSpan missingWindow,
        DateTime utcNow,
        IFailoverRoleController? controller = null,
        IFailoverEventLog? events = null)
    {
        controller ??= new NoopFailoverRoleController();
        events ??= new NoopFailoverEventLog();

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租約期間須為正。");
        }

        if (missingWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(missingWindow), "缺拍寬限窗不可為負。");
        }

        var lease = _store.GetLease();
        var target = Decide(lease, leaseDuration, missingWindow, utcNow, controller, events);
        _lastRole = target;
        return target;
    }

    private FailoverRole Decide(
        FailoverLease? lease,
        TimeSpan leaseDuration,
        TimeSpan missingWindow,
        DateTime utcNow,
        IFailoverRoleController controller,
        IFailoverEventLog events)
    {
        if (lease is null)
        {
            return Elect(leaseDuration, utcNow, controller, events);
        }

        if (lease.ServerId == _serverId)
        {
            // 自身租約：續約；轉變自非-Leader 才當選/接管
            _store.Upsert(new FailoverLease(_serverId, utcNow + leaseDuration));
            return Elect(leaseDuration, utcNow, controller, events);
        }

        if (!lease.IsExpiredAt(utcNow))
        {
            // 他人有效租約：退避；上輪是 Leader→旁落
            CedeIfWasLeader(lease, utcNow, controller, events);
            return FailoverRole.Standby;
        }

        var expiredFor = utcNow - lease.ExpiresUtc;
        if (expiredFor >= missingWindow)
        {
            // 他人租約逾窗→接管機護窗已過，競選
            return Elect(leaseDuration, utcNow, controller, events);
        }

        // 逾時但仍在寬限窗內：等待（不競選）；上輪 Leader 則交還
        CedeIfWasLeader(lease, utcNow, controller, events);
        return FailoverRole.Standby;
    }

    private FailoverRole Elect(
        TimeSpan leaseDuration,
        DateTime utcNow,
        IFailoverRoleController controller,
        IFailoverEventLog events)
    {
        _store.Upsert(new FailoverLease(_serverId, utcNow + leaseDuration));

        if (_lastRole == FailoverRole.Leader)
        {
            return FailoverRole.Leader; // 自身續約（含自身租約過期的自我回復）
        }

        controller.TakeOver(utcNow);
        var mode = _lastRole == FailoverRole.Standby
            ? FailoverEventMode.Takeover
            : FailoverEventMode.Leader;
        events.Append(_serverId, mode, $"acquired leader lease expire={SqliteStore.Iso(utcNow + leaseDuration)}", utcNow);
        return FailoverRole.Leader;
    }

    private void CedeIfWasLeader(
        FailoverLease lease,
        DateTime utcNow,
        IFailoverRoleController controller,
        IFailoverEventLog events)
    {
        if (_lastRole != FailoverRole.Leader)
        {
            return;
        }

        controller.Relinquish(utcNow);
        events.Append(_serverId, FailoverEventMode.Relinquish, $"ceded leader to {lease.ServerId}", utcNow);
    }
}