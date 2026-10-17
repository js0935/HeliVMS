using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeliVMS.Devices.Onvif;
using HeliVMS.Devices.Ptz;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// PTZ 巡航的執行主機：把 <see cref="PatrolRepository"/> 裡的排程真的讓攝影機走一遍。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這個類別：巡航的排程、儲存、編輯 UI、以及演算法
/// （<see cref="PatrolController"/>／<see cref="PatrolRunner"/>／<see cref="OnvifPtzExecutor"/>）
/// 全部早就存在而且有單元測試，但沒有任何東西把它們接起來。操作員在
/// <c>PatrolWindow</c> 儲存並勾選「啟用」後，介面還會回報「已儲存巡航（N 個步驟、已啟用）」，
/// 然後永遠不會有任何事發生。這比沒有這個功能更糟：它讓人相信巡邏在跑。</para>
/// <para>
/// 設計上刻意「稀疏」：巡邏點之間的等待由 <see cref="PatrolController"/> 用注入時鐘判斷，
/// 所以迴圈可以固定心跳而不需要精確排程；只有真的要換點的那一次 Tick 才會碰網路。</para>
/// <para>
/// 時段語意由注入的時鐘決定（<see cref="Entry"/> 傳的是<strong>現場本地時間</strong>）：
/// <see cref="PatrolController"/> 只拿時鐘的 TimeOfDay 去比 <c>HH:mm</c>，控制器本身
/// 不知道自己是 UTC 還是本地時間，所以 <c>PatrolControllerTests</c> 用 UTC 假時鐘測試
/// 仍然成立、也不需要改。</para>
/// </remarks>
public sealed class PatrolHostService : IDisposable
{
    private readonly SqliteStore _store;
    private readonly TimeSpan _tickInterval;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private bool _disposed;

    /// <param name="store">資料來源（巡邏排程、頻道、裝置）。</param>
    /// <param name="tickInterval">心跳間隔。只需夠細以跟上最短停留時間。</param>
    public PatrolHostService(SqliteStore store, TimeSpan? tickInterval = null)
    {
        _store = store;
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>排程異動與執行結果（供 UI 顯示）。</summary>
    public event EventHandler<string>? Activity;

    /// <summary>巡邏執行失敗（參數為頻道 ID）；用於把該格標成異常。</summary>
    public event EventHandler<int>? PatrolFailed;

    /// <summary>巡邏恢復正常（參數為頻道 ID）。</summary>
    public event EventHandler<int>? PatrolRecovered;

    /// <summary>開始執行巡邏（冪等）。</summary>
    public void Start()
    {
        if (_disposed || _loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// 某頻道巡邏的可顯示狀態；沒有排程時回傳空字串。
    /// </summary>
    public string Status(int channelId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(channelId, out var e))
            {
                return string.Empty;
            }

            if (e.ConsecutiveFailures > 0)
            {
                return string.Format(Localizer.T("Patrol.StatusFailed"), e.ConsecutiveFailures, e.LastError);
            }

            if (e.Runner is null)
            {
                return e.Plan.Enabled ? Localizer.T("Patrol.StatusStandby") : Localizer.T("Patrol.StatusDisabled");
            }

            return e.Runner.Controller.CurrentIndex < 0
                ? Localizer.T("Patrol.StatusStandby")
                : string.Format(Localizer.T("Patrol.StatusRunning"), e.Runner.Controller.Plan.Steps[e.Runner.Controller.CurrentIndex].PresetName);
        }
    }

    /// <summary>暫停某頻道巡邏（操作員手動轉向時用），期間停留時間凍結。</summary>
    public void Hold(int channelId, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(channelId, out var e) && e.Runner is not null)
            {
                e.Runner.Controller.HoldFor(duration);
            }
        }
    }

    /// <summary>解除暫停並從剩餘停留繼續。</summary>
    public void Release(int channelId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(channelId, out var e) && e.Runner is not null)
            {
                e.Runner.Controller.ReleaseHold();
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAllAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Activity?.Invoke(this, string.Format(Localizer.T("Patrol.HeartbeatFail"), ex.Message));
            }

            try
            {
                await Task.Delay(_tickInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAllAsync(CancellationToken ct)
    {
        Reconcile();

        List<int> due;
        lock (_gate)
        {
            due = _entries
                .Where(kv => kv.Value.Plan.Enabled && DateTime.UtcNow >= kv.Value.RetryAfterUtc)
                .Select(kv => kv.Key)
                .ToList();
        }

        foreach (var channelId in due)
        {
            ct.ThrowIfCancellationRequested();
            await TickChannelAsync(channelId, ct);
        }
    }

    /// <summary>
    /// 以資料庫為準同步執行中的排程：新增、移除、以及使用者改過就重建。
    /// </summary>
    private void Reconcile()
    {
        IReadOnlyList<StoredPatrol> rows;
        try
        {
            rows = new PatrolRepository(_store).ListAll();
        }
        catch (Exception ex)
        {
            Activity?.Invoke(this, string.Format(Localizer.T("Patrol.ScheduleReadFail"), ex.Message));
            return;
        }

        var wanted = rows.Where(r => r.Enabled && r.Steps.Count > 0).ToDictionary(r => r.ChannelId);

        lock (_gate)
        {
            foreach (var id in _entries.Keys.ToList())
            {
                // 排程被刪掉或取消啟用 → 停止執行。已停在某個預設點的攝影機不會被移回 home，
                // 因為那是操作員的畫面；這裡只停止「繼續巡邏」。
                if (!wanted.Remove(id, out _))
                {
                    _entries.Remove(id);
                }
            }

            foreach (var (channelId, row) in wanted)
            {
                if (_entries.TryGetValue(channelId, out var existing))
                {
                    // updated_at 變了就是使用者改了排程，控制器必須重建，否則會照舊路徑走。
                    if (existing.Plan.UpdatedAtUtc != row.UpdatedAtUtc)
                    {
                        _entries[channelId] = new Entry(row);
                    }

                    continue;
                }

                _entries[channelId] = new Entry(row);
                Activity?.Invoke(this, string.Format(Localizer.T("Patrol.Started"), row.Name, channelId, row.Steps.Count));
            }
        }
    }

    private async Task TickChannelAsync(int channelId, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(channelId, out var e) || e.Runner is null)
            {
                return;
            }

            entry = e;
        }

        try
        {
            await EnsureRunnerAsync(entry, ct);
            await entry.Runner!.TickOnceAsync(ct);
            Recover(entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(entry, ex);
        }
    }

    /// <summary>ONVIF 執行者延遲建立：建立本身就要打網路（列 profiles）。</summary>
    private async Task EnsureRunnerAsync(Entry entry, CancellationToken ct)
    {
        if (entry.Runner is not null)
        {
            return;
        }

        var channel = new ChannelRepository(_store).Get(entry.Plan.ChannelId);
        if (channel?.DeviceId is not int deviceId)
        {
            throw new InvalidOperationException(Localizer.T("Patrol.NoOnvif"));
        }

        // 巡邏是排程行為，不是操作員手動操作；audit 記「巡航觸發」而不是每個心跳都記一筆。
        var devices = new DeviceRepository(_store, new AuditLogRepository(_store));
        var device = devices.Get(deviceId)
            ?? throw new InvalidOperationException(string.Format(Localizer.T("Patrol.DeviceMissing"), deviceId));

        // SafeUnprotect 而非 Unprotect：舊資料可能是明文，解密失敗不該讓整條巡邏死掉。
        var password = string.IsNullOrWhiteSpace(device.PasswordEncrypted)
            ? null
            : DeviceRepository.SafeUnprotect(device.PasswordEncrypted);

        var service = new OnvifDeviceService(
            $"http://{device.Ip}:{device.Port}/onvif/device_service",
            device.Username,
            password);

        var profiles = await service.GetProfilesAsync(ct);
        if (profiles.Count == 0)
        {
            throw new InvalidOperationException(Localizer.T("Patrol.NoProfile"));
        }

        entry.Device = service;
            entry.Runner = new PatrolRunner(entry.Controller, new OnvifPtzExecutor(service, profiles[0].Token));
    }

    private void Fail(Entry entry, Exception ex)
    {
        var channelId = entry.Plan.ChannelId;
        var wasFailing = entry.ConsecutiveFailures > 0;

        entry.ConsecutiveFailures++;
        entry.LastError = FirstLine(ex.Message);
        // 離線的攝影機不可能靠更頻繁重試就连上线；沿用錄影迴圈同一套退避，避免把 ONVIF 打爆。
        entry.RetryAfterUtc = DateTime.UtcNow + SegmentRecorder.RetryDelayFor(entry.ConsecutiveFailures);

        Activity?.Invoke(this, string.Format(Localizer.T("Patrol.Failed"), channelId, entry.LastError));
        if (!wasFailing)
        {
            PatrolFailed?.Invoke(this, channelId);
        }
    }

    private void Recover(Entry entry)
    {
        if (entry.ConsecutiveFailures == 0)
        {
            return;
        }

        entry.ConsecutiveFailures = 0;
        entry.LastError = string.Empty;
        entry.RetryAfterUtc = DateTime.MinValue;
        PatrolRecovered?.Invoke(this, entry.Plan.ChannelId);
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Localizer.T("Patrol.UnknownError");
        }

        var trimmed = text.Trim();
        var nl = trimmed.IndexOfAny(['\r', '\n']);
        return nl >= 0 ? trimmed[..nl] : trimmed;
    }

    /// <summary>
    /// 把儲存的 HH:mm 解析成 TimeSpan。
    /// </summary>
    /// <remarks>
    /// 壞值不能各自退回 0：那會讓時段變成 00:00–00:00，等於只在午夜執行，
    /// 比不啟用更難察覺。任一端壞掉就整段視為全天。解析規則本身在
    /// <see cref="PatrolWindowTime"/>，因為它是純邏輯、值得被測。
    /// </remarks>
    private static (TimeSpan Start, TimeSpan End) ParseWindow(StoredPatrol plan)
        => PatrolWindowTime.Parse(plan.WindowStart, plan.WindowEnd);

    private static DateTime LocalClock() => DateTime.Now;

    private sealed class Entry
    {
        public Entry(StoredPatrol plan)
        {
            Plan = plan;
            var steps = plan.Steps
                .Select(s => new PatrolStep(s.PresetName, TimeSpan.FromSeconds(Math.Max(0, s.DwellSeconds))))
                .ToArray();

            var window = ParseWindow(plan);
            Controller = new PatrolController(
                new PatrolPlan(plan.Name, plan.Enabled, window.Start, window.End, steps),
                LocalClock);

            // 操作員輸入的 HH:mm 一定是當地牆上時間（他們看的是牆上的時鐘），
            // 而 PatrolController 拿注入時鐘的 TimeOfDay 去比時段。這裡傳本地時間
            // 而不是 UTC，就讓比對發生在操作員認得的座標系上；DateTime 的 Kind
            // 對控制器不重要，它只看 TimeOfDay 與錨點差值。
            //
            // 早期版本傳 UTC，等於台灣的「09:00 巡邏」實際在 01:00 執行，
            // 而且看起來完全正常——排程有跑，只是跑在錯的時間。
        }

        public StoredPatrol Plan { get; }

        public PatrolController Controller { get; }

        public PatrolRunner? Runner { get; set; }

        public OnvifDeviceService? Device { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTime RetryAfterUtc { get; set; }

        public string LastError { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _entries.Clear();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}