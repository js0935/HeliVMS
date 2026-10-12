using System.Collections.Concurrent;
using System.IO;
using HeliVMS.Alarms;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using StreamKind = HeliVMS.Storage.StreamKind;

namespace HeliVMS.App.Services;

/// <summary>
/// 頻道工作階段管理器：維護多路監看，每 1 秒健康檢查（畫面包裹逾時即自動重連），
/// 並以事件對外發布聚合狀態（footer 統計）。
/// </summary>
public sealed class ChannelManager : IDisposable
{
    private const int StaleSeconds = 10;
    private readonly ConcurrentDictionary<int, ChannelSession> _sessions = new();

    /// <summary>
    /// channelId → cell 的權威對應。
    /// </summary>
    /// <remarks>
    /// 原本 <c>GetCell</c> 是把 <c>_sessions.Keys</c> 排序後取索引，這在頻道清單未按 id 遞增時
    /// 會回傳<em>錯誤</em>的格位（例如 channels=[9,3,5] → cell 0,1,2，但排序後 9 落在索引 2），
    /// 於是健康重連事件指向錯的格子、操作員看到錯誤提示。此表在建立/拆除工作階段時維護。
    /// </remarks>
    private readonly ConcurrentDictionary<int, int> _cellByChannel = new();
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly System.Threading.Timer _health;
    private readonly OfflineEventTracker _offline;
    private readonly StreamPreferenceRepository _streamPrefs;
    private IDetectionEngine? _detection;
    private bool _disposed;

    public ChannelManager(SqliteStore store, string recordingsRoot, string snapshotsRoot, string? modelPath)
    {
        _store = store;
        _channels = new ChannelRepository(store);
        _devices = new DeviceRepository(store, new AuditLogRepository(store));
        _recordingsRoot = recordingsRoot;
        _snapshotsRoot = snapshotsRoot;
        _offline = new OfflineEventTracker(new AlarmEventRepository(store));
        _offline.CloseOpenAtStartup();
        _streamPrefs = new StreamPreferenceRepository(store);

        // 以資料庫恢復每頻道目前碼流與上次切換時間，否則重開後一律顯示 Main、維持期歸零。
        foreach (var ch in _channels.List())
        {
            Switcher.Prime(ch.Id, _streamPrefs.GetKind(ch.Id), _streamPrefs.GetLastSwitchUtc(ch.Id));
        }
        if (!string.IsNullOrEmpty(modelPath))
        {
            try
            {
                _detection = new OnnxRuntimeCpuEngine(modelPath);
            }
            catch (Exception)
            {
                _detection = null;   // 模型壞檔或架構不符 → 關閉 AI 層
            }
        }

        _health = new System.Threading.Timer(OnHealthTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>已載入元件模型路徑（null 表示未部署）。</summary>
    public string? ModelPath => _detection is OnnxRuntimeCpuEngine e ? e.ModelPath : null;

    /// <summary>單一頻道畫面抵達。</summary>
    public event EventHandler<(int Cell, VideoFrame Frame)>? FrameArrived;

    /// <summary>單一頻道狀態轉換。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel, RtspState State)>? StateChanged;

    /// <summary>健康檢查要求重連。</summary>
    public event EventHandler<(int Cell, ChannelInfo Channel)>? HealthRestart;

    /// <summary>單一頻道完整 AI 偵測（cell 對應監看格；一併帶 ChannelId 讓訂閱者不必回頭查 cell→channel 對應）。</summary>
    public event EventHandler<(int Cell, int ChannelId, DetectionsFrame Frame)>? AiDetections;

    /// <summary>單一頻道事件已寫入 alarm_events（通知中心訂閱用）。</summary>
    public event EventHandler<(int Cell, AlarmEventRecord Record)>? AlarmEvent;

    /// <summary>單一頻道偵測到運動（rising edge）；在解碼執行緒引發，訂閱者須自行 marshal。</summary>
    public event EventHandler<(int Cell, double Ratio)>? MotionSignal;

    /// <summary>某格的「顯示的是哪個頻道」已確定或改變。</summary>
    /// <remarks>
    /// 為什麼需要這個事件：改派 cell 時本來就有在跑的 <c>ChannelSession</c> 不會再發
    /// <c>StateChanged</c>，而 UI 是靠該事件填 <c>_cellChannel</c>。少了這個事件，
    /// 使用者按 F11 回多格之後會看到「畫面是 A 頻道、徽章卻寫 B 頻道」，
    /// 而且幀率／延遲會被記到錯誤的頻道帳上。狀態與影格都正常，所以沒有任何報錯可指認。
    /// </remarks>
    public event EventHandler<(int Cell, int ChannelId)>? CellMappingChanged;

    /// <summary>
    /// 本地警報推播中樞：事件寫入 alarm_events 後在此發布，事件中心／警報管理器即時刷新，
    /// 不必等 15 秒輪詢。與 WebApi 的 <c>/api/alerts/ws</c> 共用同一個型別。
    /// </summary>
    public AlertBroadcastHub Alerts { get; } = new();

    /// <summary>
    /// 每頻道目前碼流與切換史的權威來源（M76）。由管理器持有（而非各視窗自建），
    /// 狀態才不會每開一次切流視窗就被當成全新的、可以立刻再切一次。
    /// </summary>
    public StreamSwitcher Switcher { get; } = new();

    public bool HasActiveSessions { get; private set; }

    /// <summary>依頻道清單連線到前 <paramref name="count"/> 路（自 <paramref name="startIndex"/> 起循環）。</summary>
    public async Task ConnectAsync(IReadOnlyList<ChannelInfo> channels, int startIndex, int count)
    {
        // 重新連線即重建全部工作階段，保持 cell 對應一致
        await DisconnectAllAsync();

        if (channels.Count == 0)
        {
            return;
        }

        // 迴圈上限同時受格數與頻道數限制：格數多於頻道數時不得再以 % 循環回填，
        // 否則單一頻道會被建立多個工作階段、同時畫進多格（1 台攝影機填滿整個多分割）。
        for (var i = 0; i < count && i < channels.Count; i++)
        {
            var ch = channels[(startIndex + i) % channels.Count];
            var session = CreateSession(ch, i);
            _sessions[ch.Id] = session;
            _cellByChannel[ch.Id] = i;
            CellMappingChanged?.Invoke(this, (i, ch.Id));

            await session.StartMonitoringAsync();
        }

        await ApplyDisplayPolicyAsync(count);
        HasActiveSessions = _sessions.Values.Any(s => s.IsMonitoring);
        if (HasActiveSessions)
        {
            _ = _health.Change(1_000, 1_000);
        }
        else
        {
            _ = _health.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 建立單一頻道工作階段並接好事件（cell 於建立時固定）。連線與後續切流共用，
    /// 避免兩條路徑各自接線而漏掉事件或 cell 對應。
    /// </summary>
    private ChannelSession CreateSession(ChannelInfo ch, int cell)
    {
        var session = new ChannelSession(
            ch.Id,
            ch.Name,
            ResolveRecordingUrl(ch),                 // 主流：顯示升級與錄影共用同一條（固定主流）
            ResolveStreamUrl(ch, StreamKind.Sub),    // 次流：常駐顯示
            _store,
            _recordingsRoot,
            _snapshotsRoot,
            ch.MotionEnabled,
            _detection,
            IsTamperEnabled());
        session.Cell = cell;

        // 一律讀 session.Cell 而非閉包捕捉的 cell：佈局切換改派格位時，
        // 事件必須跟著新格位走，否則畫面會畫在錯的格子上。
        session.FrameArrived += (_, f) => FrameArrived?.Invoke(this, (session.Cell, f));
        session.StateChanged += (_, st) =>
        {
            if (st == RtspState.Reconnecting)
            {
                _offline.MarkOffline(ch.Id);
            }
            else if (st == RtspState.Streaming)
            {
                _offline.MarkOnline(ch.Id);
            }

            StateChanged?.Invoke(this, (session.Cell, ch, st));
        };
        session.AiDetections += (_, d) => AiDetections?.Invoke(this, (session.Cell, ch.Id, d));
        session.EventInserted += (_, r) =>
        {
            AlarmEvent?.Invoke(this, (session.Cell, r));
            Alerts.Publish(new AlertUpdate("alarm.created", r.Id));
        };
        session.MotionSignal += (_, ratio) => MotionSignal?.Invoke(this, (session.Cell, ratio));
        return session;
    }

    /// <summary>
    /// 依目前佈局套用顯示碼流政策：單畫面（1 格）只有第 0 格的頻道升級主流以取得最佳畫質，
    /// 其餘一律用次流；多分割（2 格以上）全部用次流以降低解碼與頻寬負載。
    /// </summary>
    /// <remarks>
    /// 次流永遠常駐，升級主流或退回次流都不會中斷次流連線，故切換佈局不會出現「連線中」或黑畫面。
    /// 錄影永遠走主流（<see cref="ResolveRecordingUrl"/>），不隨顯示切流改變。
    /// 手動「切流」可臨時覆蓋單一頻道，但下一次佈局變更或重新連線會再以本政策為準。
    /// 只對「碼流真的需要改變」的頻道動作，避免每次切佈局都重建連線。
    /// </remarks>
    private async Task ApplyDisplayPolicyAsync(int cellCount)
    {
        var now = DateTime.UtcNow;

        foreach (var (channelId, session) in _sessions)
        {
            var target = cellCount <= 1 && session.Cell == 0 ? StreamKind.Main : StreamKind.Sub;
            if (Switcher.GetCurrent(channelId) != target)
            {
                Switcher.ApplySwitch(channelId, target, now);
                _streamPrefs.Save(channelId, target, now);
            }

            await session.SwitchDisplayStreamAsync(target);
        }
    }

    /// <summary>目前採用的顯示碼流（供切流視窗顯示）。</summary>
    public StreamKind GetStreamKind(long channelId) => Switcher.GetCurrent(channelId);

    /// <summary>
    /// 採納切流決策：更新引擎狀態並持久化，若該頻道正在監看就即時切換顯示碼流。
    /// 錄影位址不受影響（見 <see cref="ChannelSession.SwitchDisplayStreamAsync"/>）。
    /// </summary>
    public async Task ApplyStreamKindAsync(int channelId, StreamKind kind)
    {
        var ch = _channels.Get(channelId);
        if (ch is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        Switcher.ApplySwitch(channelId, kind, now);
        _streamPrefs.Save(channelId, kind, now);

        if (_sessions.TryGetValue(channelId, out var session))
        {
            await session.SwitchDisplayStreamAsync(kind);
        }
    }

    /// <summary>
    /// 取得頻道實際要拉流的位址。ONVIF GetStreamUri 回傳的位址不含帳密，故依綁定的設備憑證
    /// 於「連線時」才嵌入帳密：devices.password_encrypted 為 DPAPI 加密，channels.main_rtsp
    /// 維持裸位址，避免明文密碼落到 sqlite、設定頁與連線提示文字。
    /// 未綁定設備或無帳號者原樣使用儲存的位址（相容手動輸入的 RTSP 網址）。
    /// </summary>
    private string ResolveStreamUrl(ChannelInfo channel, StreamKind kind)
        => ResolveUrl(StreamRouting.DisplayUrl(channel.MainStreamUrl, channel.SubStreamUrl, kind), channel);

    /// <summary>錄影固定用主流，不隨顯示切流改變。</summary>
    private string ResolveRecordingUrl(ChannelInfo channel)
        => ResolveUrl(StreamRouting.RecordingUrl(channel.MainStreamUrl), channel);

    private string ResolveUrl(string rawUrl, ChannelInfo channel)
    {
        return RtspStreamResolver.Resolve(
            rawUrl,
            channel.DeviceId,
            host => _devices.FindByIp(host)?.Id,
            _devices.GetRtspCredentials);
    }

    /// <summary>M39：是否啟用遮蔽偵測（app_settings `detect.tamper.enabled`；重新連線時讀取）。</summary>
    private bool IsTamperEnabled()
        => string.Equals(
            new SettingsRepository(_store).Get("detect.tamper.enabled"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>中斷所有監看。</summary>
    public async Task DisconnectAllAsync()
    {
        foreach (var kv in _sessions)
        {
            await TeardownAsync(kv.Value);
        }

        HasActiveSessions = false;
        _health.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>開啟／關閉指定頻道錄影；被授權閘門拒絕時回傳阻擋結果。</summary>
    public async Task<RecordingGateResult?> SetRecordingAsync(int channelId, bool recording)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            return await session.SetRecordingAsync(recording);
        }

        return null;
    }

    /// <summary>是否有任何頻道正在錄影。</summary>
    public bool AnyRecording() => _sessions.Values.Any(s => s.IsRecording);

    /// <summary>指定頻道是否正在錄影。</summary>
    public bool IsRecording(int channelId) => _sessions.TryGetValue(channelId, out var s) && s.IsRecording;

    /// <summary>M14：套用單一頻道 AI 策略（enabled＝是否推理；intervalMs＝取樣間隔）。</summary>
    public void SetAiPolicy(int channelId, bool enabled, int intervalMs)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            session.ConfigureAi(enabled, intervalMs);
        }
    }

    /// <summary>播放狀態（連線中／流媒體中）。</summary>
    public int CountStreaming()
    {
        var connected = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.IsMonitoring && (s.Client.State == RtspState.Streaming || s.Client.State == RtspState.Connecting))
            {
                connected++;
            }
        }

        return connected;
    }

    private async void OnHealthTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var kv in _sessions)
        {
            var session = kv.Value;
            if (!session.IsMonitoring)
            {
                continue;
            }

            if (session.Client.State == RtspState.Streaming &&
                (DateTime.UtcNow - session.LastFrameUtc).TotalSeconds > StaleSeconds)
            {
                var info = _channels.Get(kv.Key);
                var cell = GetCell(kv.Key);
                _offline.MarkOffline(kv.Key);
                var restarted = await RestartAsync(session);
                if (restarted)
                {
                    _offline.MarkOnline(kv.Key);
                }

                if (info is not null)
                {
                    HealthRestart?.Invoke(this, (cell, info));
                }
            }
        }
    }

    private async Task<bool> RestartAsync(ChannelSession session)
    {
        await session.Client.StopAsync();
        try
        {
            await session.Client.StartAsync();
            session.ResetDetection();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int GetCell(int channelId)
        => _cellByChannel.TryGetValue(channelId, out var cell) ? cell : -1;

    /// <summary>
    /// 該頻道目前的連線診斷（ffmpeg 最近輸出，已遮蔽帳密）；沒有工作階段時回傳空字串。
    /// </summary>
    /// <remarks>
    /// 操作員問「為什麼這台連不上」時，唯一可信的答案是 ffmpeg 自己說了什麼。
    /// 這條路徑不經事件，是因為診斷隨時在更新，而診斷視窗是按需開啟的。
    /// </remarks>
    public string Diagnostic(int channelId)
        => _sessions.TryGetValue(channelId, out var session) ? session.LastDiagnostic : string.Empty;

    /// <summary>該頻道最近一次重連的原因；沒有工作階段或尚未重連過時回傳 null。</summary>
    public string? ReconnectReason(int channelId)
        => _sessions.TryGetValue(channelId, out var session) ? session.ReconnectReason : null;

    /// <summary>取該頻道的工作階段（診斷顯示需要位址與最後畫面時間）；沒有時回傳 null。</summary>
    public ChannelSession? ChannelSessionFor(int channelId)
        => _sessions.TryGetValue(channelId, out var session) ? session : null;

    private async Task TeardownAsync(ChannelSession session)
    {
        await session.StopAsync();
        _sessions.TryRemove(session.ChannelId, out _);
        _cellByChannel.TryRemove(session.ChannelId, out _);
        session.Dispose();
    }

    /// <summary>
    /// 讓畫面上的格位與既有工作階段對齊：改派仍在顯示的、補上尚未連線的。
    /// </summary>
    /// <remarks>
    /// <b>刻意不拆除</b>不再顯示的頻道。看似該關省資源，但 <c>ChannelSession</c> 同時
    /// 持有該頻道的手動錄影行程，拆掉等於使用者切個版面就默默被斷錄；
    /// 對錄影系統來說這比多留幾條閒置串流嚴重得多。狀態仍會由健康檢查維持。
    /// </remarks>
    /// <returns>本次新建立的頻道數量。</returns>
    public async Task<int> ReconcileAsync(IReadOnlyList<ChannelInfo> channels, int startIndex, int count)
    {
        if (channels.Count == 0)
        {
            return 0;
        }

        var created = 0;
        var assigned = new HashSet<int>();
        // 與 ConnectAsync 同理：格數多於頻道數時不得以 % 循環回填，否則同一頻道會被
        // 指派到多個格位（1 台攝影機佔滿整個多分割）。
        for (var i = 0; i < count && i < channels.Count; i++)
        {
            var ch = channels[(startIndex + i) % channels.Count];
            assigned.Add(ch.Id);
            if (_sessions.TryGetValue(ch.Id, out var existing))
            {
                // 仍在顯示 → 只改格位，不重連。必須同時通知 UI：
                // 已在串流的 session 不會再發 StateChanged，UI 的 cell 對應會停在舊格。
                existing.Cell = i;
                _cellByChannel[ch.Id] = i;
                CellMappingChanged?.Invoke(this, (i, ch.Id));
                continue;
            }

            var session = CreateSession(ch, i);
            _sessions[ch.Id] = session;
            _cellByChannel[ch.Id] = i;
            CellMappingChanged?.Invoke(this, (i, ch.Id));
            created++;
            await session.StartMonitoringAsync();
        }

        // 未被本次佈局指派的既有工作階段，移出畫面格位（cell = -1）：單格展開時它可能仍
        // 佔著第 0 格，而該格已被新頻道接管，兩條串流會在同一格互相閃爍。刻意不拆除工作階段，
        // 錄影行程要繼續；只是不再往畫面上疊圖。
        foreach (var kv in _sessions)
        {
            if (!assigned.Contains(kv.Key) && kv.Value.Cell >= 0 && kv.Value.Cell < count)
            {
                kv.Value.Cell = -1;
                _cellByChannel.TryRemove(kv.Key, out _);
            }
        }

        await ApplyDisplayPolicyAsync(count);
        HasActiveSessions = _sessions.Values.Any(s => s.IsMonitoring);
        var period = HasActiveSessions ? 1_000 : Timeout.Infinite;
        _health.Change(period, period);
        return created;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _health.Dispose();
        _detection?.Dispose();
        foreach (var kv in _sessions)
        {
            kv.Value.Dispose();
        }

        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}