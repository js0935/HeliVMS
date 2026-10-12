using System.Diagnostics;
using HeliVMS.Alarms;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using StreamKind = HeliVMS.Storage.StreamKind;

namespace HeliVMS.App.Services;

/// <summary>單一頻道之生命週期封裝：監看連線（RtspClient）＋選用錄影（SegmentRecorder）＋運動偵測與健康訊息。</summary>
public sealed class ChannelSession : IDisposable
{
    private readonly SqliteStore _store;
    private readonly string _recordingsRoot;
    private readonly LicenseService _license;
    private MotionEventEngine? _motion;
    private AiEventEngine? _ai;
    private TamperEventEngine? _tamper;
    private readonly Stopwatch _motionClock = new();
    private long _lastMotionMs;
    private long _lastTamperMs;
    private SegmentRecorder? _recorder;
    private bool _disposed;

    /// <summary>常駐次流以外的選用主流連線（單畫面才建立）。</summary>
    private RtspClient? _mainClient;

    /// <summary>解析後的主流顯示位址（已嵌設備憑證）。</summary>
    private readonly string _mainUrl;

    /// <summary>主／次流位址是否真的不同；相同時不需為「升級畫質」多拉一條連線。</summary>
    private readonly bool _hasMainStream;

    /// <summary>目前是否要求顯示主流（次流仍常駐）。</summary>
    private bool _wantMain;

    /// <summary>主流是否已出畫；未出畫前顯示來源仍為次流。</summary>
    private bool _mainReady;

    public ChannelSession(int channelId, string name, string mainDisplayUrl, string subDisplayUrl, SqliteStore store, string recordingsRoot, string snapshotsRoot, bool motionEnabled, IDetectionEngine? detection, bool tamperEnabled = false, LicenseService? license = null)
    {
        ChannelId = channelId;
        Name = name;
        _mainUrl = mainDisplayUrl;
        _hasMainStream = !string.Equals(mainDisplayUrl, subDisplayUrl, StringComparison.Ordinal);
        RecordingUrl = mainDisplayUrl;
        _store = store;
        _recordingsRoot = recordingsRoot;
        _license = license ?? new LicenseService(store);

        // 次流常駐：多分割直接取用；切到單畫面時，主流尚未出畫前也先用次流頂著，
        // 故切換佈局不會出現「連線中」，也不會黑畫面。
        Client = new RtspClient(subDisplayUrl, hwAccel: ReadHwAccel()) { MaxFramesPerSecond = 15 };
        AttachSubClient(Client);

        if (motionEnabled)
        {
            _motion = new MotionEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot);
            _motion.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
            _motion.MotionSignal += (_, ratio) => MotionSignal?.Invoke(this, ratio);
            if (detection is not null)
            {
                _ai = new AiEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot, detection);
                _ai.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
                _ai.DetectionsReady += (_, d) =>
                {
                    LastAiUtc = d.SnapshotUtc;
                    AiDetections?.Invoke(this, d);
                };
            }
        }

        if (tamperEnabled)
        {
            _tamper = new TamperEventEngine(channelId, new AlarmEventRepository(_store), snapshotsRoot);
            _tamper.EventInserted += (_, r) => EventInserted?.Invoke(this, r);
        }

        _motionClock.Start();
    }

    public int ChannelId { get; }

    /// <summary>
    /// 此工作階段目前對應的監看格。佈局切換時可原地改派（<see cref="ChannelManager.ReconcileAsync"/>），
    /// 不必拆掉 RTSP 連線與 ffmpeg 行程——這是 F11 展開不再全黑數十秒的關鍵。
    /// </summary>
    public int Cell { get; internal set; }

    public string Name { get; }

    /// <summary>目前實際顯示的 RTSP 位址（主流已接手時為主流，否則為常駐次流）。</summary>
    public string Url => EffectiveClient.RtspUrl;

    /// <summary>錄影用 RTSP 位址（固定主流，不隨顯示切流改變）。</summary>
    public string RecordingUrl { get; } = string.Empty;

    public RtspClient Client { get; private set; }

    public bool IsMonitoring { get; private set; }

    public bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>最後收到畫面之時間（健康監控用）。</summary>
    public DateTime LastFrameUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 最近一次重連的原因（null 表示尚未重連過）。
    /// </summary>
    /// <remarks>
    /// 原本 <see cref="RtspClient.Reconnecting"/> 帶著 <see cref="Exception"/> 事件上來卻被
    /// 直接丟棄，只轉成 <c>Reconnecting</c> 狀態。操作員看到的就是一個不動的「重連中…」，
    /// 無從判斷是 401、逾時還是位址寫錯——而這三種的處置完全不同。診斷字串必須留著。
    /// </remarks>
    public string? ReconnectReason { get; private set; }

    /// <summary>
    /// ffmpeg 最近的輸出（已遮蔽帳密），用來回答「為何這個頻道連不上」。
    /// </summary>
    /// <remarks>
    /// <see cref="RtspClient.LastDiagnostic"/> 的文件註解就寫著「供 UI 診斷顯示」，
    /// 但從來沒有任何 UI 讀過它——資訊已經算好了卻沒人看。
    /// </remarks>
    public string LastDiagnostic => EffectiveClient.LastDiagnostic;

    public event EventHandler<VideoFrame>? FrameArrived;

    public event EventHandler<RtspState>? StateChanged;

    /// <summary>最近一次被授權閘門拒絕的原因（null 表示未被拒絕過）。</summary>
    public string? RecordingBlockedReason { get; private set; }

    /// <summary>該頻道事件已寫入 alarm_events（通知中心訂閱用）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>運動觸發（rising edge），參數為當下變動比例；在解碼執行緒引發。</summary>
    public event EventHandler<double>? MotionSignal;

    /// <summary>新增錄影被授權閘門拒絕（UI 以此提示使用者）。</summary>
    public event EventHandler<RecordingGateResult>? RecordingBlocked;

    /// <summary>該頻道每幀完整 AI 偵測（即時監看疊加用）。</summary>
    public event EventHandler<DetectionsFrame>? AiDetections;

    /// <summary>最近一次 AI 推理結果時間（null 表示尚未有）。</summary>
    public DateTime? LastAiUtc { get; private set; }

    /// <summary>M14：套用 AI 每格策略（啟用與否＋取樣間隔 ms）；停用時清窗捨棄待決幀。</summary>
    public void ConfigureAi(bool enabled, int sampleIntervalMs)
    {
        if (_ai is null)
        {
            return;
        }

        _ai.MinSampleIntervalMs = sampleIntervalMs;
        _ai.Enabled = enabled;
    }

    public async Task StartMonitoringAsync()
    {
        if (IsMonitoring || _disposed)
        {
            return;
        }

        await Client.StartAsync();
        IsMonitoring = true;

        // 若在開始監看前就被指派為單畫面，這裡補上主流連線（次流已在跑）。
        await EnsureMainAsync();
    }

    /// <summary>接上次流連線事件。次流常駐，是唯一對外發布 <see cref="StateChanged"/> 的來源，
    /// 這樣單畫面為升級畫質而建立的主流，其暫時的 Connecting／Reconnecting 就不會讓 UI 閃「連線中」。</summary>
    private void AttachSubClient(RtspClient client)
    {
        client.FrameDecoded += OnSubFrame;
        client.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        client.Reconnecting += (_, ex) =>
        {
            ReconnectReason = Summarize(ex);
            StateChanged?.Invoke(this, RtspState.Reconnecting);
        };
    }

    /// <summary>
    /// 接上主流連線事件。主流只負責在單畫面時升級畫質：尚未出畫或失效時靜默退回次流，
    /// 不發布狀態、不影響 UI；<see cref="_mainReady"/> 是顯示來源是否交付給主流的唯一開關。
    /// </summary>
    private void AttachMainClient(RtspClient client)
    {
        client.FrameDecoded += OnMainFrame;
        client.StateChanged += (_, s) =>
        {
            if (s != RtspState.Streaming)
            {
                _mainReady = false;
            }
        };
        client.Reconnecting += (_, _) => _mainReady = false;
    }

    /// <summary>
    /// 把連線失敗的例外壓成一句可顯示的原因。
    /// </summary>
    /// <remarks>
    /// 直接顯示例外訊息會把 ffmpeg 整段 stderr 丟進格內文字，那會讓版面爆掉；
    /// 但只顯示「重連中」又等於什麼都沒說。取第一行非空訊息並限長，兩者之間取平衡。
    /// </remarks>
    private static string Summarize(Exception? ex)
    {
        if (ex is null)
        {
            return Localizer.T("Main.ConnectionLost");
        }

        var text = ex.Message.Trim();
        if (text.Length == 0)
        {
            text = ex.GetType().Name;
        }

        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            text = text[..newline];
        }

        return text.Length <= 120 ? text : text[..120] + "…";
    }

    /// <summary>
    /// 讀取監看硬體解碼設定（<c>decode.hwaccel</c>，§3.3 D4：opt-in）。每次建立/切換顯示連線時重讀，
    /// 讓操作員改設定後切流即生效、免重啟；未設定時為 <c>null</c>（純軟解，與既有行為一致）。
    /// </summary>
    private string? ReadHwAccel() => new SettingsRepository(_store).Get("decode.hwaccel");

    /// <summary>
    /// 切換顯示碼流（<see cref="StreamKind.Main"/>＝高清單畫面，<see cref="StreamKind.Sub"/>＝多分割）。
    /// 次流永遠常駐，切換只是決定要不要「加開」一條主流：主流連上並出畫前，畫面仍由次流供應，
    /// 故切換佈局不會閃「連線中」或黑畫面；切回次流時關閉主流以省資源。
    /// 錄影（<see cref="RecordingUrl"/>，固定主流）不受影響。
    /// </summary>
    public async Task SwitchDisplayStreamAsync(StreamKind kind)
    {
        var wantMain = kind == StreamKind.Main && _hasMainStream;
        if (_wantMain == wantMain)
        {
            return;
        }

        _wantMain = wantMain;
        if (wantMain)
        {
            await EnsureMainAsync();
        }
        else
        {
            await StopMainAsync();
        }
    }

    /// <summary>目前實際顯示的連線：主流已接手時為主流，否則為常駐次流。</summary>
    private RtspClient EffectiveClient =>
        _wantMain && _mainReady && _mainClient is not null ? _mainClient : Client;

    /// <summary>必要時建立並啟動主流顯示連線（次流已在跑；等首幀抵達才接手顯示）。</summary>
    private async Task EnsureMainAsync()
    {
        if (_disposed || !IsMonitoring || !_wantMain || !_hasMainStream || _mainClient is not null)
        {
            return;
        }

        var main = new RtspClient(_mainUrl, hwAccel: ReadHwAccel()) { MaxFramesPerSecond = 15 };
        AttachMainClient(main);
        _mainClient = main;
        _mainReady = false;
        await main.StartAsync();
    }

    /// <summary>關閉主流顯示連線；顯示來源即刻退回常駐次流（次流從未中斷）。</summary>
    private async Task StopMainAsync()
    {
        var main = _mainClient;
        if (main is null)
        {
            return;
        }

        _mainClient = null;
        _mainReady = false;
        await main.StopAsync();
        await main.DisposeAsync();
    }

    /// <summary>
    /// 開啟／關閉本頻道錄影。
    /// </summary>
    /// <returns>
    /// 被授權閘門拒絕時回傳阻擋結果（UI 據此提示）；其餘情況回傳 null。
    /// 停止錄影與已在錄影中的情況永不回傳阻擋結果——授權失效不中斷既有錄影。
    /// </returns>
    public async Task<RecordingGateResult?> SetRecordingAsync(bool recording)
    {
        if (recording && _recorder is null)
        {
            // 授權閘門（§19.4）：到期／時鐘回流／未匯入授權／超出通道上限皆拒絕新增錄影。
            // 既有錄影不受影響（不停止、不刪除）——不可勒索客戶。
            //
            // 來源先存成變數再開閘門：RecordingGateContractTests 要求每個開錄點
            // 在問閘門之前就把來源備好，這樣「有帶來源」是可以結構上確認的，
            // 而不是靠人工檢查引數裡剛好有沒有字樣。
            var source = RecordingGateSources.Manual;
            var gate = _license.CheckRecording(ChannelId, DateTime.UtcNow, source);
            if (!gate.Allowed)
            {
                RecordingBlockedReason = gate.Reason;
                RecordingBlocked?.Invoke(this, gate);
                return gate;
            }

            RecordingBlockedReason = null;
            var recorder = new SegmentRecorder(new SegmentRepository(_store));
            await recorder.StartAsync(ChannelId, RecordingUrl, _recordingsRoot, "main", segmentSeconds: 15);
            _recorder = recorder;
        }
        else if (!recording && _recorder is not null)
        {
            var recorder = _recorder;
            _recorder = null;
            await recorder.StopAsync();
        }

        return null;
    }

    public async Task StopAsync()
    {
        await SetRecordingAsync(recording: false);
        _motion?.Flush();
        _ai?.Flush();
        _tamper?.Flush();
        await StopMainAsync();
        if (IsMonitoring)
        {
            await Client.StopAsync();
            IsMonitoring = false;
        }
    }

    /// <summary>健康重連後清除偵測窗口，避免殘留假事件。</summary>
    public void ResetDetection()
    {
        _motion?.Reset();
        _ai?.Reset();
        _tamper?.Reset();
    }

    /// <summary>次流影格：只要目前顯示來源仍是次流（含主流尚未出畫的過渡期）就對外輸出。</summary>
    private void OnSubFrame(object? sender, VideoFrame frame)
    {
        if (ReferenceEquals(EffectiveClient, Client))
        {
            EmitFrame(frame);
        }
    }

    /// <summary>主流影格：標記已出畫並接手顯示。主流失效時退回次流（由 <see cref="AttachMainClient"/> 清除旗標）。</summary>
    private void OnMainFrame(object? sender, VideoFrame frame)
    {
        _mainReady = true;
        if (ReferenceEquals(EffectiveClient, _mainClient))
        {
            EmitFrame(frame);
        }
    }

    /// <summary>送出實際顯示的影格，並餵給偵測引擎（只有顯示中的來源會被處理，避免重複事件）。</summary>
    private void EmitFrame(VideoFrame frame)
    {
        LastFrameUtc = DateTime.UtcNow;
        FrameArrived?.Invoke(this, frame);

        // 運動偵測抽樣：約每 250ms 一幀（低解析度網格成本可忽略）
        if (_motion is not null && _motionClock.ElapsedMilliseconds - _lastMotionMs >= 250)
        {
            _lastMotionMs = _motionClock.ElapsedMilliseconds;
            _motion.OnFrame(frame);
        }

        // 遮蔽偵測抽樣：約每 250ms 一幀（引擎內部需連續 N 次才開窗）
        if (_tamper is not null && _motionClock.ElapsedMilliseconds - _lastTamperMs >= 250)
        {
            _lastTamperMs = _motionClock.ElapsedMilliseconds;
            _tamper.OnFrame(frame);
        }

        // AI 取樣（推理佇列，引擎內部節流 400ms）
        _ai?.OnFrame(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Client.FrameDecoded -= OnSubFrame;
        }
        catch (InvalidOperationException)
        {
            // 事件解除失敗時忽略
        }

        if (_mainClient is not null)
        {
            try
            {
                _mainClient.FrameDecoded -= OnMainFrame;
            }
            catch (InvalidOperationException)
            {
                // 事件解除失敗時忽略
            }
        }

        _motion?.Dispose();
        _ai?.Dispose();
        _tamper?.Dispose();
        _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mainClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}