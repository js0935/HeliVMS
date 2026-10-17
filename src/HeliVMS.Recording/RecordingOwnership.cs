using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影單一擁有者仲裁（M321，§11.1 進程分離）。
///
/// <para>
/// 桌面 App 與 Headless Windows 服務可以同時連上同一個資料庫與錄影根目錄。在此機制之前，兩者會
/// 各自判定「這個頻道該錄」，各開一個 ffmpeg、寫出重複的區段檔——磁碟用量翻倍、回放時間軸出現
/// 交錯重複的段落，而 log 乾淨得像什麼事都沒有。本協調器以共用資料庫上的單列租約仲裁「此刻誰在錄」：
/// 只有租約持有者開錄影機，其餘行程退為待命；持有者若當掉，租約逾時後待命者自動接管（呼應 §11.1
/// 「崩潰自動重啟、重啟後恢復全部通道錄影」）。
/// </para>
///
/// <para>
/// <b>優先序</b>讓「錄影核心＝背景服務、WPF 為客戶端」得以成立：服務（priority 2）可在租約仍有效時
/// 搶下桌面（priority 1）持有的租約，於是先開桌面的站台在服務起來後仍會收斂到由服務錄影；
/// 反之桌面永遠搶不走服務的租約。這不是「誰先開誰佔住」。
/// </para>
///
/// <para>
/// <b>不依賴 SQLite</b>：仲裁只看 <see cref="IRecordingLeaseStore"/>，測試以 in-memory 假時鐘演練
/// 逾時接管、同級不搶、高優先搶佔。SQLite／WAL 對單列 upsert 的序列化即為仲裁點（與 M87 failover
/// 租約相同語意）。
/// </para>
/// </summary>
public sealed class RecordingOwnershipCoordinator
{
    /// <summary>預設租約長度：調和週期 30 秒，取 60 秒＝容許連續一次週期沒續約再逾時。</summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(60);

    private readonly IRecordingLeaseStore _store;
    private readonly string _ownerId;
    private readonly int _priority;
    private readonly bool _preemptSamePriority;
    private readonly TimeSpan _leaseDuration;

    /// <param name="store">租約儲存。</param>
    /// <param name="ownerId">本行程身分（建議含角色前綴與 <c>pid</c>，讓同一角色的兩份實例互不續約）。</param>
    /// <param name="priority">優先序；較高者可在他人租約仍有效時搶下。</param>
    /// <param name="preemptSamePriority">
    /// 是否可搶下「同優先序」的租約。僅服務角色可為 true——服務由 SCM 保證單一實例，其同級租約必定是
    /// 自己上一次崩潰留下的殘骸，搶回可免去重啟後的空窗；桌面可能同時多開，同級搶會導致兩視窗互搶而重複錄影。
    /// </param>
    /// <param name="leaseDuration">租約長度，預設 <see cref="DefaultLeaseDuration"/>。</param>
    public RecordingOwnershipCoordinator(
        IRecordingLeaseStore store,
        string ownerId,
        int priority,
        bool preemptSamePriority = false,
        TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (priority < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), "優先序不可為負。");
        }

        var duration = leaseDuration ?? DefaultLeaseDuration;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租約期間須為正。");
        }

        _store = store;
        _ownerId = ownerId;
        _priority = priority;
        _preemptSamePriority = preemptSamePriority;
        _leaseDuration = duration;
    }

    /// <summary>本行程身分。</summary>
    public string OwnerId => _ownerId;

    /// <summary>本行程優先序。</summary>
    public int Priority => _priority;

    /// <summary>目前租約（供診斷）。</summary>
    public RecordingLease? Current => _store.Get();

    /// <summary>
    /// 嘗試取得或續約租約。可取得（無租約、自身租約、他人租約已過期、優先序較高、或可搶同級）時
    /// 寫入租約並回 <c>true</c>（本行程應錄影）；他人有效租約且無權搶佔時回 <c>false</c>（退為待命）。
    /// </summary>
    public bool AcquireOrRenew(DateTime utcNow)
    {
        var current = _store.Get();
        var canTake = current is null
            || current.IsExpiredAt(utcNow)
            || current.OwnerId == _ownerId
            || current.Priority < _priority
            || (_preemptSamePriority && current.Priority == _priority);

        if (!canTake)
        {
            return false;
        }

        _store.Upsert(new RecordingLease(_ownerId, _priority, utcNow + _leaseDuration));
        return true;
    }

    /// <summary>
    /// 自願讓出（僅當租約仍屬本行程時有效）。用於行程正常關閉，讓下一次啟動能立即取回而不必等逾時；
    /// 非自身租約時回 <c>false</c> 且不改寫他人租約。
    /// </summary>
    public bool Release(DateTime utcNow)
    {
        var current = _store.Get();
        if (current is null || current.OwnerId != _ownerId)
        {
            return false;
        }

        _store.Delete();
        return true;
    }
}

/// <summary>
/// 錄影擁有者的角色定義與建立（M321）。桌面與服務用不同身分／優先序接入同一個
/// <see cref="RecordingOwnershipCoordinator"/>；WebApi 與其他不錄影的行程不參與。
/// </summary>
public static class RecordingOwnership
{
    /// <summary>服務角色優先序（錄影核心）。</summary>
    public const int ServicePriority = 2;

    /// <summary>桌面角色優先序（WPF 客戶端；服務缺席時才錄）。</summary>
    public const int DesktopPriority = 1;

    /// <summary>服務角色的身分（含 pid，讓同一角色的前後兩份實例互不續約）。</summary>
    public static string ServiceOwnerId() => $"service:{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>桌面角色的身分（含 pid）。</summary>
    public static string DesktopOwnerId() => $"desktop:{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>建立服務角色的仲裁器。</summary>
    public static RecordingOwnershipCoordinator CreateService(SqliteStore store)
        => new(new RecordingLeaseRepository(store), ServiceOwnerId(), ServicePriority, preemptSamePriority: true);

    /// <summary>建立桌面角色的仲裁器。</summary>
    public static RecordingOwnershipCoordinator CreateDesktop(SqliteStore store)
        => new(new RecordingLeaseRepository(store), DesktopOwnerId(), DesktopPriority);
}
