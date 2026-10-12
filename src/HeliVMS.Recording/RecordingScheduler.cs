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
    private readonly ConcurrentDictionary<int, ISegmentRecorder> _active = new();
    private readonly ConcurrentDictionary<int, RecordingBlockReason> _blockReasons = new();
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public RecordingScheduler(
        SqliteStore store,
        string recordingsRoot,
        Func<int, bool>? isCellRecording = null,
        TimeSpan? reconcileInterval = null,
        LicenseService? license = null,
        LicenseLimitNotifier? limitNotifier = null,
        Func<ISegmentRecorder>? recorderFactory = null)
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
        ReconcileInterval = reconcileInterval ?? TimeSpan.FromSeconds(30);
        _timer = new System.Threading.Timer(OnTick, null, ReconcileInterval, ReconcileInterval);
    }

    /// <summary>
    /// 預設的錄影機工廠。生產環境走這裡；測試注入假工廠以驗證啟停決策，
    /// 不必真的啟動 ffmpeg（見 <c>HeliVMS.Recording.Tests/RecordingSchedulerTests</c>）。
    /// </summary>
    private ISegmentRecorder DefaultRecorderFactory()
        => new SegmentRecorder(new SegmentRepository(_store));

    /// <summary>
    /// 解析實際用來啟動錄影的 RTSP 位址——把資料庫中未含帳密的 <c>channels.main_rtsp</c>
    /// 補上對應 <c>devices</c> 的憑證。
    ///
    /// 排程錄影以前直接使用 <c>ch.MainStreamUrl</c>，相機要求驗證時就一直回 401，
    /// 連續三段寫不出檔而發出停滯事件；即時監看畫面卻正常，因為它早就透過
    /// <c>ChannelManager</c> 走同一條解析路徑。這裡補上相同處理，消除這個落差。
    /// </summary>
    private string ResolveRecordingUrl(ChannelInfo channel)
        => RtspStreamResolver.Resolve(
            channel.MainStreamUrl,
            channel.DeviceId,
            host => _devices.FindByIp(host)?.Id,
            _devices.GetRtspCredentials);

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

        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>調和一次：讓「該錄的」有錄影機、「不該錄的」沒有。</summary>
    public async Task ReconcileAsync()
    {
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
            await recorder.StartAsync(ch.Id, ResolveRecordingUrl(ch), _recordingsRoot, "main", segmentSeconds: 15);
            if (_active.TryAdd(ch.Id, recorder))
            {
                Activity?.Invoke(this, $"錄影開始：ch{ch.Id:000} {ch.Name}");
                _audit.Record(
                    "system",
                    "recording.start",
                    AuditCategories.Recording,
                    targetType: "channel",
                    targetId: ch.Id,
                    // 位址必須遮蔽帳密：稽核記錄會顯示在稽核視窗、也能經 API 讀取與匯出，
                // 明文密碼進去之後就等於把憑證散播到每一份稽核備份裡。
                detail: $"source={gateSource} mode={ch.RecordingMode} url={RtspUri.Redact(ch.MainStreamUrl)}");
            }
            else
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
            }
        }

        foreach (var id in _active.Keys.ToList())
        {
            if (wanted.ContainsKey(id) || _disposed)
            {
                continue;
            }

            if (_active.TryRemove(id, out var recorder))
            {
                recorder.RecordingStalled -= OnRecordingStalled;
                await recorder.StopAsync();
                await recorder.DisposeAsync();
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
        foreach (var id in _active.Keys.ToList())
        {
            if (_active.TryRemove(id, out var recorder))
            {
                await recorder.StopAsync();
                await recorder.DisposeAsync();
            }
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