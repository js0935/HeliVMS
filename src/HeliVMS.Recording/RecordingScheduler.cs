using System.Collections.Concurrent;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影排程服務（M10）：依 <see cref="RecordingIntent"/> 的判斷在背景對未監看中的頻道
/// 啟動／停止錄影。每 <see cref="ReconcileInterval"/> 檢查一次（預設 30 秒）；
/// 已由即時監看畫面（cell）錄製之頻道不重複開錄，人工錄影不受影響。
/// 錄影參照 <see cref="SegmentRecorder"/>（fMP4＋SHA-256 分段）。
///
/// 「該不該錄」的規則不在本類別，而在 <see cref="RecordingIntent"/>——本類別只執行它。
/// </summary>
public sealed class RecordingScheduler : IDisposable
{
    private readonly SqliteStore _store;
    private readonly RecordingScheduleRepository _repo;
    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly LicenseService _license;
    private readonly LicenseLimitNotifier _limitNotifier;
    private readonly AuditLogRepository _audit;
    private readonly string _recordingsRoot;
    private readonly Func<int, bool>? _isCellRecording;
    private readonly Func<ISegmentRecorder> _recorderFactory;
    private readonly SegmentRepository _segments;
    private readonly Func<int, long> _observedBps;
    private readonly Func<bool>? _diskFull;

    /// <summary>
    /// 本行程此刻是否持有「錄影擁有者」租約（M321，§11.1 進程分離）。null＝不仲裁（一律視為擁有者，
    /// 維持單一行程的既有行為）。由桌面與服務各自注入租約續約函式；未持有者不開新錄影，且會收掉
    /// 手上既有的錄影機，把錄影交還給持有者，避免兩份行程對同一台攝影機各開一個 ffmpeg。
    /// </summary>
    private readonly Func<bool>? _ownsRecording;


    /// <summary>
    /// 錄影心跳要寫到哪裡（資料根目錄）。null 表示不寫（例如測試或非監控用途）。
    /// 每輪調和寫一次，讓看門狗能分辨「行程還活著」與「行程卡住／已死」（M320）。
    /// </summary>
    private readonly string? _heartbeatRoot;

    private readonly ConcurrentDictionary<int, ISegmentRecorder> _active = new();

    /// <summary>目前在錄的各頻道所使用的串流名稱（<c>main</c>／<c>sub</c>）。</summary>
    private readonly ConcurrentDictionary<int, string> _activeStreams = new();

    private readonly ConcurrentDictionary<int, RecordingBlockReason> _blockReasons = new();
    private readonly System.Threading.Timer _timer;

    /// <summary>
    /// 調和序列化閘。System.Threading.Timer 不會等上一輪回呼結束，調和若被資料庫鎖拖過間隔，
    /// 就會有兩輪同時改 <c>_active</c>、同時決定要搬的資料夾。公開的 <see cref="ReconcileAsync"/>
    /// 排隊等鎖；計時器回呼搶不到鎖就跳過這一輪。
    /// </summary>
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private bool _disposed;

    /// <summary>本次啟動是否已跑過錄影資料夾命名搬移（每次行程只需要一次）。</summary>
    private bool _foldersMigrated;

    /// <summary>目前是否因磁碟可用空間過低而暫停開新錄影（磁碟滿策略第三階段去重狀態）。</summary>
    private bool _diskBlocked;

    /// <summary>
    /// 目前是否因「非錄影擁有者」而停錄（M321 去重狀態）。與 <see cref="_diskBlocked"/> 不同：磁碟滿
    /// 只停「開新」、既有錄影收尾；失去擁有權要連既有錄影一併收掉（交還給持有者），否則兩份行程會同時錄。
    /// </summary>
    private bool _ownershipBlocked;

    /// <summary>推估主碼流實測碼率時納入的近期區段區間。</summary>
    private static readonly TimeSpan QualityWindow = TimeSpan.FromMinutes(30);

    public RecordingScheduler(
        SqliteStore store,
        string recordingsRoot,
        Func<int, bool>? isCellRecording = null,
        TimeSpan? reconcileInterval = null,
        LicenseService? license = null,
        LicenseLimitNotifier? limitNotifier = null,
        Func<ISegmentRecorder>? recorderFactory = null,
        Func<int, long>? observedBps = null,
        Func<bool>? diskFull = null,
        string? heartbeatRoot = null,
        Func<bool>? ownsRecording = null)
    {
        _store = store;
        _recordingsRoot = recordingsRoot;
        _isCellRecording = isCellRecording;
        _repo = new RecordingScheduleRepository(store);
        _channels = new ChannelRepository(store);
        _license = license ?? new LicenseService(store);
        _limitNotifier = limitNotifier ?? new LicenseLimitNotifier(store);
        _audit = new AuditLogRepository(store);
        _devices = new DeviceRepository(store, _audit);
        _recorderFactory = recorderFactory ?? DefaultRecorderFactory;
        _segments = new SegmentRepository(store);
        _observedBps = observedBps ?? DefaultObservedBps;
        _diskFull = diskFull;
        _heartbeatRoot = heartbeatRoot;
        _ownsRecording = ownsRecording;
        ReconcileInterval = reconcileInterval ?? TimeSpan.FromSeconds(30);
        _timer = new System.Threading.Timer(OnTick, null, ReconcileInterval, ReconcileInterval);
    }

    /// <summary>
    /// 預設的錄影機工廠。生產環境走這裡；測試注入假工廠以驗證啟停決策，
    /// 不必真的啟動 ffmpeg（見 <c>HeliVMS.Recording.Tests/RecordingSchedulerTests</c>）。
    /// </summary>
    private ISegmentRecorder DefaultRecorderFactory()
        => new SegmentRecorder(
            new SegmentRepository(_store),
            rootSelector: RecordingStorage.CreateRootSelector(_store, _recordingsRoot),
            // 錄影資料夾以頻道名稱命名（M311）。與資料夾搬移共用同一套命名規則，
            // 否則搬好的目錄名稱會和錄影端寫入的名稱對不上。
            folderNameFor: id => RecordingFolderNaming.ResolveFor(_store, id));

    /// <summary>
    /// 主碼流實測碼率的預設來源：近 <see cref="QualityWindow"/> 內已完成的區段。
    /// 用磁碟上實際寫出的檔案推估，與磁碟容量告警同一個數字；測試注入假函式避免碰磁碟。
    /// </summary>
    private long DefaultObservedBps(int channelId)
    {
        var now = DateTime.UtcNow;
        // 只認 final（ListPlayableByRange）：ListByRange 會把 missing／corrupt／tmp 也回傳，
        // 而 ObservedBitrateEstimator 只憑大小與長度取樣、不看狀態，被刪除的段會被算成實際流量，
        // 讓頻寬治理依一個磁碟上不存在的碼率去分配——最該省頻寬時反而分錯。
        var segments = _segments.ListPlayableByRange(channelId, "main", now - QualityWindow, now);
        return ObservedBitrateEstimator.ForChannel(channelId, segments).ObservedBps;
    }

    /// <summary>
    /// 依 <see cref="RecordingQualityCoordinator.BudgetSettingKey"/> 算出跨設備品質計畫。
    /// 未設預算（0＝不限制）時回 null，排程維持原本「一律主碼流」的行為。
    /// </summary>
    private QualityPlan? BudgetPlan(IEnumerable<ChannelInfo> wanted)
    {
        var budget = RecordingQualityCoordinator.ReadBudget(_store);
        if (budget <= 0)
        {
            return null;
        }

        var channels = wanted
            .Select(ch => new QualityChannel(
                ch.Id,
                _observedBps(ch.Id),
                RecordingQualityCoordinator.DefaultPriority,
                // 事件狀態尚無來源可查（排程不持有事件倉儲），先一律視為非事件。
                // 事件加權的分配本身已在協調器層被測過，等有來源再接上即可。
                InEvent: false,
                HasSubStream: HasSubStream(ch)))
            .ToList();

        return RecordingQualityCoordinator.Coordinate(budget, channels);
    }

    private static bool HasSubStream(ChannelInfo channel)
        => !string.IsNullOrWhiteSpace(channel.SubStreamUrl);

    private static string StreamName(RecordingStream stream)
        => stream == RecordingStream.Sub ? "sub" : "main";

    /// <summary>
    /// 解析實際用來啟動錄影的 RTSP 位址——把資料庫中未含帳密的 <c>channels.main_rtsp</c>
    /// 補上對應 <c>devices</c> 的憑證。
    ///
    /// 排程錄影以前直接使用 <c>ch.MainStreamUrl</c>，相機要求驗證時就一直回 401，
    /// 連續三段寫不出檔而發出停滯事件；即時監看畫面卻正常，因為它早就透過
    /// <c>ChannelManager</c> 走同一條解析路徑。這裡補上相同處理，消除這個落差。
    ///
    /// <paramref name="stream"/> 為 <see cref="RecordingStream.Sub"/> 時改解析子碼流位址；
    /// 頻道沒設子碼流時退回主碼流，避免降級決策意外把錄影指向空位址。
    /// </summary>
    private string ResolveRecordingUrl(ChannelInfo channel, RecordingStream stream = RecordingStream.Main)
    {
        var raw = stream == RecordingStream.Sub ? channel.SubStreamUrl : channel.MainStreamUrl;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = channel.MainStreamUrl;
        }

        return RtspStreamResolver.Resolve(
            raw,
            channel.DeviceId,
            host => _devices.FindByIp(host)?.Id,
            _devices.GetRtspCredentials);
    }

    public TimeSpan ReconcileInterval { get; }

    /// <summary>目前由排程啟動錄影之頻道 ID。</summary>
    public IReadOnlyCollection<int> ActiveChannelIds => _active.Keys.ToList();

    /// <summary>
    /// 該頻道目前「不該錄」的原因；正在錄或沒有阻擋時回 null。
    /// </summary>
    /// <remarks>
    /// 這裡不重算決策，而是記錄上次 <see cref="ReconcileAsync"/> 算出的結果。
    /// 讓 UI 直接問「為什麼沒在錄」，是為了取代一種很糟的診斷方式：
    /// 操作員看到磁碟沒檔案，只能把所有可能的原因逐一試過去。
    /// </remarks>
    public RecordingBlockReason? BlockReasonFor(int channelId)
        => _blockReasons.TryGetValue(channelId, out var reason) ? reason : null;

    /// <summary>最近一次調和錯誤（null 表示無誤）。</summary>
    public string? LastError { get; private set; }

    /// <summary>排程開始／停止錄影之診斷訊息。</summary>
    public event EventHandler<string>? Activity;

    /// <summary>錄影連續失敗（已寫入稽核；此事件另供 UI 即時顯示）。</summary>
    public event EventHandler<RecordingStallInfo>? RecordingStalled;

    /// <summary>先前停滯的頻道已成功錄回檔；參數為頻道 ID。</summary>
    public event EventHandler<int>? RecordingResumed;

    private async void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        // 搶不到鎖代表上一輪還在跑：跳過這次，寧可晚一輪也不要與它重疊。
        if (!_reconcileGate.Wait(0))
        {
            return;
        }

        try
        {
            await ReconcileAsyncCore();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// 一次（每次啟動）把既有 <c>ch{id}</c> 錄影資料夾改成以頻道名稱命名，並回寫索引（M311）。
    /// <para>
    /// 放在 <see cref="ReconcileAsync"/> 最前面，是因為這裡是排程錄影與監看牆錄影開寫之前必然經過的
    /// 入口：不先搬，舊目錄與新目錄會同時存在，同一段錄影散在兩處。兩種錄影路徑（本機 App 與
    /// Agent 共用這個 scheduler）都因此不用各自接一段。
    /// </para>
    /// <para>
    /// 全程盡力而為——檔案被佔用等情況由搬移器逐筆跳過，不應讓調和失敗，所以例外只記一行活動，
    /// 並保留這次嘗試讓下一個調和週期再試。
    /// </para>
    /// </summary>
    private void MigrateRecordingFoldersOnce()
    {
        if (_foldersMigrated)
        {
            return;
        }

        try
        {
            var result = RecordingFolderMigrator.Run(_store);
            if (result.Changed)
            {
                Activity?.Invoke(
                    this,
                    $"錄影資料夾以頻道名稱命名：搬移 {result.MovedFolders}、合併 {result.MergedFolders}、回寫 {result.UpdatedSegments} 筆路徑");
            }

            _foldersMigrated = true;
        }
        catch (Exception ex)
        {
            Activity?.Invoke(this, $"錄影資料夾搬移失敗：{ex.Message}");
        }
    }

    /// <summary>調和一次：讓「該錄的」有錄影機、「不該錄的」沒有。同一時間只允許一輪，重複呼叫會排隊。</summary>
    public async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReconcileAsyncCore().ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task ReconcileAsyncCore()
    {
        MigrateRecordingFoldersOnce();

        var now = DateTime.Now;
        var schedules = _repo.List();
        var channelById = _channels.List().ToDictionary(c => c.Id);

        // 單一趟迴圈決定該錄的頻道清單，啟動與停止共用同一份判斷。
        // 舊的寫法是「走排程清單啟動、走排程清單停止」，兩份依據各自算出來；
        // 改成走 recording_mode 之後這個分歧會更明顯，所以改成先收斂成 wanted。
        var wanted = channelById.Values
            .Where(ch => RecordingIntent.ShouldRecord(ch, schedules, now))
            .ToDictionary(ch => ch.Id);

        // 記下每個「不該錄」的頻道為什麼不該錄，讓 UI 能直接回答「為什麼沒錄」。
        // 少了這一步，操作員看到磁碟沒檔案時只能逐一猜原因。
        var reasons = channelById.Values.ToDictionary(
            ch => ch.Id,
            ch => RecordingIntent.BlockReasonFor(ch, schedules, now));
        _blockReasons.Clear();
        foreach (var (channelId, reason) in reasons)
        {
            if (reason != RecordingBlockReason.None)
            {
                _blockReasons[channelId] = reason;
            }
        }

        // 磁碟滿策略第三階段（§11.1）：可用空間低於寫入門檻時「停新錄、保全證物」。
        // 等級只在改變時寫一筆稽核與活動訊息；既有錄影不因此中斷，只是這一輪不再開新的錄影機，
        // 空間恢復後下一輪自然又把它們帶回來。讀取磁碟狀態失敗時 fail-open（不擋錄影）——
        // 取不到容量資訊與真的沒空間是兩回事，不該讓前者停掉全部錄影。
        var diskFull = false;
        try
        {
            diskFull = _diskFull?.Invoke() == true;
        }
        catch (Exception)
        {
            diskFull = false;
        }

        if (diskFull != _diskBlocked)
        {
            _diskBlocked = diskFull;
            _audit.Record(
                "system",
                diskFull ? "recording.disk_full" : "recording.disk_full_cleared",
                AuditCategories.Recording,
                detail: diskFull
                    ? "磁碟可用空間低於寫入門檻，暫停開新錄影（既有錄影保留收尾）"
                    : "磁碟可用空間已恢復，恢復開新錄影");
            Activity?.Invoke(this, diskFull ? "磁碟空間不足：暫停開新錄影" : "磁碟空間已恢復：恢復開新錄影");
        }

        // 錄影擁有者仲裁（M321，§11.1 進程分離）：桌面與服務同連一個資料庫時，只有租約持有者錄影。
        // 失去擁有權者除了不開新錄影，還要收掉既有錄影（下面的停止迴圈會處理），否則兩份行程同時錄、
        // 寫出重複區段而 log 乾淨。讀取失敗時 fail-open（視為擁有者繼續錄）——仲裁只是防重複，
        // 取不到狀態不該讓錄影中斷而漏掉畫面。狀態改變時寫一筆稽核與活動訊息。
        var ownsRecording = true;
        try
        {
            ownsRecording = _ownsRecording?.Invoke() ?? true;
        }
        catch (Exception)
        {
            ownsRecording = true;
        }

        if (!ownsRecording != _ownershipBlocked)
        {
            _ownershipBlocked = !ownsRecording;
            _audit.Record(
                "system",
                ownsRecording ? "recording.ownership_acquired" : "recording.ownership_lost",
                AuditCategories.Recording,
                detail: ownsRecording
                    ? "取得錄影擁有者租約，恢復錄影"
                    : "錄影擁有者租約由另一行程持有，停止本行程錄影（交還）");
            Activity?.Invoke(this, ownsRecording ? "取得錄影擁有權：恢復錄影" : "錄影由另一行程接手：本行程停止錄影");
        }

        // 跨設備品質協調（M285，§14.7 #12）。未設總頻寬上限時 plan 為 null，
        // 以下所有分支都會退回「一律主碼流」的既有行為。
        var plan = BudgetPlan(wanted.Values);

        // 分段長度（M312）每個調和週期讀一次即可：它是純設定，不像品質計畫會隨負載變動。
        // 在啟動迴圈外讀，避免每個頻道各讀一次設定表。
        var segmentSeconds = RecordingSegmentPolicy.Read(_store);

        // 已經在錄、但新計畫要求換串流的頻道：先收掉，讓下面的啟動迴圈用新串流重開。
        // 只在設了預算時才會走到這裡（plan 非 null），所以未設定的站台不會有這個中斷。
        if (plan is not null && !_disposed)
        {
            foreach (var ch in wanted.Values)
            {
                var wantedStream = StreamName(RecordingQualityCoordinator.StreamFor(plan, ch.Id));
                if (!_activeStreams.TryGetValue(ch.Id, out var current) || current == wantedStream)
                {
                    continue;
                }

                if (_active.TryRemove(ch.Id, out var stale))
                {
                    stale.RecordingStalled -= OnRecordingStalled;
                    await stale.StopAsync();
                    await stale.DisposeAsync();
                    _activeStreams.TryRemove(ch.Id, out _);
                    Activity?.Invoke(this, $"錄影品質調整：ch{ch.Id:000} {current}→{wantedStream}");
                }
            }
        }

        foreach (var ch in wanted.Values)
        {
            if (_disposed)
            {
                return;
            }

            if (_active.ContainsKey(ch.Id))
            {
                continue;
            }

            // 磁碟滿：不再開新的錄影機（第三階段）。既有錄影在上面的品質切換與此處都不受影響。
            // 失去錄影擁有權：同樣不開新的（既有錄影由下方停止迴圈一併收掉）。
            if (_diskBlocked || _ownershipBlocked)
            {
                continue;
            }

            // 已由即時監看畫面（cell）錄製之頻道不重複開錄：同一台攝影機兩個 ffmpeg
            // 會互相搶區段檔，磁碟用量翻倍而 log 乾淨。
            if (_isCellRecording?.Invoke(ch.Id) == true)
            {
                continue;
            }

            // 授權閘門（§19.4）。排程與全時都是遠端可寫入的路徑
            //（POST /api/recording/schedules），閘門放在此同時封住
            //「遠端繞過本地端授權狀態」這條路（§18.4）。
            var gateSource = RecordingIntent.GateSourceFor(ch.RecordingMode);
            var gate = _license.CheckRecording(ch.Id, DateTime.UtcNow, gateSource);
            if (!gate.Allowed)
            {
                // 超限（額度用完）另寫一筆事件中心，讓排程被擋也留得下痕跡；
                // 非超限（未匯入／到期／作廢）由各閘門與 UI 浮條回饋，不重複灌事件。
                _limitNotifier.Report(gate, DateTime.UtcNow);
                continue;
            }

            _limitNotifier.Clear(ch.Id);

            var recorder = _recorderFactory();
            // 訂閱要在 StartAsync 之前：錄影機一開就可能失敗，訂閱晚一步就漏掉第一次停滯。
            recorder.RecordingStalled += OnRecordingStalled;
        recorder.RecordingResumed += (_, channelId) => RecordingResumed?.Invoke(this, channelId);
            var stream = RecordingQualityCoordinator.StreamFor(plan, ch.Id);
            var streamName = StreamName(stream);
            await recorder.StartAsync(ch.Id, ResolveRecordingUrl(ch, stream), _recordingsRoot, streamName, segmentSeconds: segmentSeconds);
            if (_active.TryAdd(ch.Id, recorder))
            {
                _activeStreams[ch.Id] = streamName;
                Activity?.Invoke(this, $"錄影開始：ch{ch.Id:000} {ch.Name}");
                _audit.Record(
                    "system",
                    "recording.start",
                    AuditCategories.Recording,
                    targetType: "channel",
                    targetId: ch.Id,
                    // 位址必須遮蔽帳密：稽核記錄會顯示在稽核視窗、也能經 API 讀取與匯出，
                // 明文密碼進去之後就等於把憑證散播到每一份稽核備份裡。
                detail: $"source={gateSource} mode={ch.RecordingMode} stream={streamName} url={RtspUri.Redact(stream == RecordingStream.Sub ? ch.SubStreamUrl : ch.MainStreamUrl)}");
            }
            else
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
            }
        }

        foreach (var id in _active.Keys.ToList())
        {
            if (_disposed)
            {
                continue;
            }

            // 失去擁有權時要收掉所有既有錄影（交還給持有者）；否則只收「已不需要」的頻道。
            if (!_ownershipBlocked && wanted.ContainsKey(id))
            {
                continue;
            }

            if (_active.TryRemove(id, out var recorder))
            {
                recorder.RecordingStalled -= OnRecordingStalled;
                await recorder.StopAsync();
                await recorder.DisposeAsync();
                _activeStreams.TryRemove(id, out _);
                Activity?.Invoke(this, $"錄影結束：ch{id:000}");
                // 頻道可能已被刪除，所以來源標不下就老實記 unknown，
                // 不要因為拿不到就跳過稽核——「發生了卻沒留下紀錄」正是最難查的那種。
                var mode = channelById.TryGetValue(id, out var ch) ? ch.RecordingMode : (RecordingMode?)null;
                _audit.Record(
                    "system",
                    "recording.stop",
                    AuditCategories.Recording,
                    targetType: "channel",
                    targetId: id,
                    detail: mode is null ? "source=unknown" : $"source={RecordingIntent.GateSourceFor(mode.Value)} mode={mode}");
            }
        }

        // 心跳（M320）：走到這裡代表這一輪調和真的完成了。看門狗看不到新心跳就代表行程卡住或已死。
        // 只在整輪成功後才寫，讓「每輪都擲例外」不會被誤判成健康。
        // 僅在持有錄影擁有權時寫（M321）：待命行程若也寫心跳，擁有者當掉時看門狗會誤以為錄影還在。
        if (!_ownershipBlocked)
        {
            WriteHeartbeat();
        }
    }

    /// <summary>
    /// 把「還活著、現在錄幾路」寫進資料根目錄的心跳檔（M320）。未設定心跳根目錄時不做任何事。
    /// 寫入失敗已在 <see cref="RecordingHeartbeat.Write"/> 吞掉。
    /// </summary>
    private void WriteHeartbeat()
    {
        if (_heartbeatRoot is null)
        {
            return;
        }

        RecordingHeartbeat.Write(_heartbeatRoot, DateTime.UtcNow, _active.Count, Environment.ProcessId);
    }

    /// <summary>
    /// 錄影機連續錄不成時留下紀錄。
    ///
    /// 這不是可選的額外記錄，而是唯一能讓「攝影機離線」變成可見事件的管道：
    /// ffmpeg 連不上時錄影迴圈仍在跑、稽核也已經寫過 recording.start，
    /// 沒有這裡的話系統狀態燈全綠而磁碟上沒有任何檔案。
    ///
    /// 節錄 ffmpeg 輸出是因為它幾乎一定包含那句帶帳密的輸入網址回顯
    /// （<c>Input #0, rtsp, from 'rtsp://user:pass@host'</c>），而稽核會被匯出。
    /// </summary>
    private void OnRecordingStalled(object? sender, RecordingStallInfo stall)
    {
        RecordingStalled?.Invoke(this, stall);
        const int MaxErrorChars = 400;
        var detail = $"failures={stall.ConsecutiveFailures} url={RtspUri.Redact(_channels.Get(stall.ChannelId)?.MainStreamUrl ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(stall.FfError))
        {
            // 必須先遮蔽再截斷：拿遮蔽前的長度去切遮蔽後的字串，會因為遮蔽讓字串變短
            // 而擲出 ArgumentOutOfRangeException——也就是說，正好在有憑證要遮的時候爆掉。
            var redacted = RtspUri.RedactText(stall.FfError);
            detail += $" ffmpeg={redacted[..Math.Min(MaxErrorChars, redacted.Length)]}";
        }

        if (!string.IsNullOrWhiteSpace(stall.Failure))
        {
            // Failure 是 ex.ToString()，而啟動 ffmpeg 失敗時 例外訊息裡常常帶著完整的
            // 命令列參數——其中就有含帳密的 RTSP URL。這裡若直接寫進稽核，等於把剛剛
            // 在 recording.start 遮掉的憑證又漏出來，所以遮罩與截斷都不能省。
            var failure = RtspUri.RedactText(stall.Failure);
            detail += $" error={failure[..Math.Min(MaxErrorChars, failure.Length)]}";
        }

        _audit.Record(
            "system",
            "recording.stalled",
            AuditCategories.Recording,
            targetType: "channel",
            targetId: stall.ChannelId,
            detail: detail);
        Activity?.Invoke(this, $"錄影失敗：ch{stall.ChannelId:000} 連續 {stall.ConsecutiveFailures} 段未成功");
    }

    public async Task StopAllAsync()
    {
        var recorders = new List<ISegmentRecorder>();
        foreach (var id in _active.Keys.ToList())
        {
            if (_active.TryRemove(id, out var recorder))
            {
                recorders.Add(recorder);
            }
        }

        _activeStreams.Clear();

        if (recorders.Count == 0)
        {
            return;
        }

        // 先對每一台送出取消，再一起等收尾。
        //
        // 逐一「取消→等」的話，每台 ffmpeg 收尾要幾百毫秒到 8 秒（見 SegmentRecorder.StopAsync），
        // 16 台就變成兩分鐘以上的關機等待，遠超過使用者願意等的時間——而關機等待太久，
        // 就是「收尾沒做完、行程被硬砍、留下 seg-*.mp4.tmp」的成因。並行之後總時間只等於
        // 最慢的那一台。
        await Task.WhenAll(recorders.Select(StopQuietlyAsync));

        foreach (var recorder in recorders)
        {
            await recorder.DisposeAsync();
        }
    }

    /// <summary>
    /// 停一台錄影機；單台失敗不得讓其他台（或整個關機流程）停下來。
    /// 失敗的證據已經在 <see cref="SegmentRecorder"/> 自己的 LastFailure／稽核裡。
    /// </summary>
    private static async Task StopQuietlyAsync(ISegmentRecorder recorder)
    {
        try
        {
            await recorder.StopAsync();
        }
        catch (Exception)
        {
            // 收尾失敗不該阻擋其他頻道收尾，也不該讓關機流程爆掉。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
