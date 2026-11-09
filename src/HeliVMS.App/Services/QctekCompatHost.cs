using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using HeliVMS.Qctek.Alarm;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// QCTek CMS 相容宿主（M2c）：以設定控制 NEMO 伺服器的啟停，讓 QCTek QCC（CMS）
/// 能把 HeliVMS 當成一台 NVR 管理（上線、版本／頻道數查詢、警報訂閱）。
/// </summary>
/// <remarks>
/// <para>
/// 兩種 listener：Web 埠上的多工器（<see cref="QctekPortMux"/>，只綁 loopback——QCC 與管理
/// 介面同機）與獨立埠的 <see cref="NemoServer"/>（綁所有介面，給在 CMS 端明確填了埠的遠端
/// 站台；兩者可以並存，見 <see cref="QctekPortPlanner"/>）。憑證政策<b>跟隨 HeliVMS 自己的
/// 登入驗證</b>：
/// <c>auth.enabled=1</c> 時走 <see cref="AuthService.Authenticate"/>（含鎖定與稽核）；
/// 未啟用驗證時不檢查密碼——與「本機未啟用驗證」的政策一致，但這代表同網段的人
/// 可以查頻道數、訂閱警報、看即時影像，設定頁的說明會提醒先啟用登入驗證。
/// 即時影像由 <see cref="QctekLiveStreamSource"/> 提供（每頻道一個 ffmpeg 行程，
/// CMS 要哪幾路才開哪幾路）。
/// </para>
/// <para>
/// 生命週期照 <see cref="WebHost"/>：MainWindow 建立後 <see cref="ApplySettings"/>，
/// 設定頁按「套用」再即時套用一次；收尾由 MainWindow 的收尾序列呼叫
/// <see cref="Dispose"/>（在執行緒池上，不碰 UI）。
/// </para>
/// </remarks>
public sealed class QctekCompatHost : IDisposable
{
    /// <summary>
    /// 預設的獨立埠：與 web.port（5080）錯開，避免與同機服務互撞。QCC 走的不是這個埠
    /// （它固定連 Web 埠），這裡只用於「CMS 端明確填了這個埠」的遠端站台。
    /// </summary>
    public const int DefaultPort = 5170;

    public const string EnabledKey = "qctek.compat.enabled";
    public const string PortKey = "qctek.compat.port";

    /// <summary>警報回報（M3b）：啟用、CMS 主機、CMS Alarm Service 埠。</summary>
    public const string AlarmEnabledKey = "qctek.alarm.enabled";

    public const string AlarmHostKey = "qctek.alarm.cms_host";
    public const string AlarmPortKey = "qctek.alarm.cms_port";

    private readonly DeviceContext _context;
    private NemoServer? _server;
    private QctekPortMux? _mux;
    private QctekAlarmClient? _alarmClient;
    private NetworkPortCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>
    /// 診斷日誌的背景泵：NEMO 的回應時效是協定行為的一部分（QCC 的狀態機以 33ms
    /// 為週期，回應跨過一個週期它就在授權成功後立刻關線），而一條命令要寫三行日誌、
    /// 每行都是同步的開檔＋寫＋關（過防毒掃描，實測每次 5–30ms）——同步寫會把回應
    /// 拖過這個週期。改為有界佇列＋專用寫入執行緒：關鍵路徑只做入隊（滿了丟掉），
    /// 磁碟 I/O 全在背景。崩潰時最後幾行可能丟失，可接受（診斷日誌不是稽核）。
    /// </summary>
    private static readonly BlockingCollection<string> LogQueue = new(boundedCapacity: 1024);

    static QctekCompatHost()
    {
        var path = Path.Combine(Path.GetTempPath(), "helivms-qctek.log");
        var writer = new Thread(
            () =>
            {
                foreach (var line in LogQueue.GetConsumingEnumerable())
                {
                    BoundedFileLog.Append(path, line, maxBytes: 512 * 1024);
                }
            })
        {
            IsBackground = true,
            Name = "qctek-log",
        };
        writer.Start();
    }

    public QctekCompatHost(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _context = new DeviceContext(store);
    }

    /// <summary>
    /// NEMO 應答是否執行中：獨立埠（<see cref="NemoServer"/>）或 Web 埠前置多工器
    /// （<see cref="QctekPortMux"/>）任一在跑都算。
    /// </summary>
    public bool IsRunning => _server is not null || _mux is not null;

    /// <summary>Web 埠多工器是否執行中（QCC 走這條路）。</summary>
    public bool IsMuxRunning => _mux is not null;

    /// <summary>
    /// 對外（CMS 連線）的埠：與 Web 共用時就是 Web 埠（QCC 走這一個），否則才是
    /// <see cref="PortKey"/> 指定的獨立埠。
    /// </summary>
    public int Port { get; private set; }

    /// <summary>警報回報是否執行中（M3b）。</summary>
    public bool IsAlarmReporting => _alarmClient is not null;

    /// <summary>警報回報的目標（<c>host:port</c>）；未執行時為 <c>null</c>。</summary>
    public string? AlarmTarget { get; private set; }

    /// <summary>最近一次啟動失敗的訊息；成功時為 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 由 <see cref="NetworkPortCoordinator"/> 接手啟停順序：Web 與相容服務搶同一個埠時，
    /// 誰先綁、誰先放會決定成敗。單獨使用本宿主（沒有協調者）時仍可運作，只是
    /// 「共用埠」不會成立。
    /// </summary>
    internal void AttachCoordinator(NetworkPortCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>
    /// 依設定啟用或停用相容服務（NEMO 伺服器）與警報回報，回傳 NEMO 狀態訊息
    /// （設定頁直接顯示；警報狀態由 <see cref="IsAlarmReporting"/>／
    /// <see cref="AlarmTarget"/> 讀現況）。
    /// </summary>
    public string ApplySettings(SettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 有協調者時一律走它：只套用相容服務會在多工器佔著公開埠時嘗試用
        // NemoServer 綁同一個埠（失敗），或反過來把 Web 鎖在臨時埠上。
        return _coordinator is not null
            ? _coordinator.Apply(settings).Qctek
            : ApplyLocal(
                settings,
                QctekPortPlanner.Standalone(settings.Get(EnabledKey) == "1", ReadPort(settings)),
                httpPort: 0);
    }

    /// <summary>
    /// 套用設定，並依呼叫端指定的順序決定 listener 的組態。
    /// </summary>
    /// <param name="settings">共用設定。</param>
    /// <param name="plan">
    /// 由 <see cref="QctekPortPlanner"/> 算出的計畫；共用時呼叫前 Web 服務必須已改聽
    /// <paramref name="httpPort"/>。
    /// </param>
    /// <param name="httpPort">內部 Kestrel 的實際埠（HTTP 轉發目標）。</param>
    public string ApplyLocal(SettingsRepository settings, QctekPortPlan plan, int httpPort)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 這一行是「QCC 說連線中斷」時的第一個分流點：看得到計畫與多工器狀態，
        // 才分得清「沒開多工器」「多工器綁不上」「Web 沒讓出公開埠」三種完全不同的成因。
        Diag(
            $"套用相容服務：共用Web埠={plan.ShareWebPort} 公開埠={plan.PublicPort} " +
            $"獨立埠={plan.StandalonePort} 內部HTTP={httpPort} 目前多工器={_mux?.Port.ToString(CultureInfo.InvariantCulture) ?? "無"}");

        // 警報回報有獨立開關（M3b）：NEMO 伺服器的啟停不影響它，反之亦然。
        ApplyAlarmSettings(settings);

        if (!plan.Anything)
        {
            StopMux();
            Stop();
            return Localizer.T("Settings.QctekStopped");
        }

        LastError = null;
        try
        {
            if (plan.ShareWebPort)
            {
                // 公開埠要讓給多工器（Web 已在臨時埠上）；同一個埠上的 NemoServer 要收掉。
                StartMux(plan.PublicPort, httpPort);
            }
            else
            {
                // 不共用：多工器必須先放掉公開埠，NemoServer 才綁得上。
                StopMux();
            }

            if (plan.StandalonePort > 0)
            {
                // 相容埠與 Web 埠不同：那是給「在 CMS 端明確填了這個埠」的遠端站台用的。
                // 兩個 listener 並存才不會「換了埠就讓另一邊斷線」。
                Start(plan.StandalonePort);
            }
            else if (plan.ShareWebPort)
            {
                Stop();
            }

            Port = plan.ShareWebPort ? plan.PublicPort : plan.StandalonePort;
            return string.Format(Localizer.T("Settings.QctekRunning"), Port);
        }
        catch (Exception ex)
        {
            // 埠被占用等啟動失敗：確保不留半啟動的伺服器（否則下次套用會洩漏一條 listener）。
            // 一定要寫進日誌：QCC 端只會說「連線中斷」，沒有這一行的話這裡是唯一的線索。
            Diag($"相容服務啟動失敗：{ex.GetType().Name} {ex.Message}");
            LastError = ex.Message;
            StopMux();
            Stop();
            return string.Format(Localizer.T("Settings.QctekStartFailed"), ex.Message);
        }
    }

    private static int ReadPort(SettingsRepository settings)
    {
        var raw = settings.Get(PortKey);
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : DefaultPort;
    }

    /// <summary>依設定啟停警報回報（M3b）。未啟用或未填妥目標時保持停用。</summary>
    private void ApplyAlarmSettings(SettingsRepository settings)
    {
        var enabled = settings.Get(AlarmEnabledKey) == "1";
        var host = settings.Get(AlarmHostKey)?.Trim() ?? string.Empty;
        var portRaw = settings.Get(AlarmPortKey);
        var port = int.TryParse(portRaw, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 0;

        if (!enabled || host.Length == 0 || port == 0)
        {
            StopAlarm();
            return;
        }

        StartAlarm(host, port);
    }

    private void StartAlarm(string host, int port)
    {
        StopAlarm();
        var client = new QctekAlarmClient(new QctekAlarmOptions
        {
            CmsHost = host,
            CmsPort = port,
            // 連線識別由客戶端自行遞增（QCV8001 的 %d 是每條連線的計數）；
            // =%s 是裝置資訊字串——宣告 QCV8001 讓 CMS 走相容路徑（與 NEMO 伺服器一致）。
            Info = "QCV8001",
            // 回報側與 NEMO 側共用同一份診斷日誌：實測時「HELLO 有沒有送出去」
            // 與「QCC 有沒有連進來」都要在同一條時間線上對照。
            Log = Diag,
        });
        client.Start();
        _alarmClient = client;
        AlarmTarget = $"{host}:{port}";
    }

    /// <summary>
    /// 回報一筆事件到 CMS（M3c）。任何執行緒可呼叫；未啟用回報或未連線時
    /// 事件進佇列等待重連（佇列有上限，滿了丟最舊）。
    /// </summary>
    /// <param name="channelId">頻道 id（送作相機號——穩定且唯一）。</param>
    /// <param name="channelName">頻道名（送作事件名稱）。</param>
    /// <param name="eventType">事件類型（motion/offline/…；送作類型字串）。</param>
    /// <param name="startUtc">事件時刻（UTC；送出前轉本地時間）。</param>
    /// <param name="detail">事件詳細資料（送作描述；可空）。</param>
    public void ReportEvent(int channelId, string channelName, string eventType, DateTime startUtc, string? detail)
    {
        _alarmClient?.Report(new QctekAlarmEvent(
            CameraNumber: channelId,
            LocalTime: startUtc.ToLocalTime(),
            Name: channelName,
            Describe: detail ?? string.Empty,
            Priority: 0,
            EventType: eventType));
    }

    private void StopAlarm()
    {
        var client = _alarmClient;
        _alarmClient = null;
        AlarmTarget = null;
        if (client is null)
        {
            return;
        }

        try
        {
            client.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 停止失敗不讓設定套用或關閉流程卡住。
        }
    }

    private void Start(int port)
    {
        // 同一個埠就沿用；換埠一定要重開，否則設定頁顯示新埠、listener 還停在舊埠，
        // CMS 永遠連不上（而且沒有任何錯誤）。
        if (_server is { } running && running.Port == port)
        {
            return;
        }

        Stop();
        var server = new NemoServer(_context, IPAddress.Any, port, log: Diag);
        server.Start();
        _server = server;
        Port = port;
    }

    /// <summary>
    /// 啟動 Web 埠多工器（公開埠由它擁有，HTTP 轉給 <paramref name="httpPort"/>）。
    /// 參數沒變就不重開——重開會斷掉正在連線的 QCC，也會在公開埠上留下一小段空窗。
    /// </summary>
    private void StartMux(int publicPort, int httpPort)
    {
        if (_mux is { } running && running.Port == publicPort && running.InternalHttpPort == httpPort)
        {
            return;
        }

        StopMux();
        var mux = new QctekPortMux(_context, httpPort, IPAddress.Loopback, publicPort, log: Diag);
        mux.Start();
        _mux = mux;
        Port = publicPort;
    }

    /// <summary>
    /// 停掉多工器並放掉公開埠。必須在「Web 要綁回公開埠」之前完成，
    /// 否則 Kestrel 會因連接埠被占用而啟動失敗。
    /// </summary>
    public void StopMux()
    {
        var mux = _mux;
        _mux = null;
        if (mux is null)
        {
            return;
        }

        Diag($"多工器停止：公開埠 {mux.Port}");
        try
        {
            mux.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 停止失敗（連線收尾中斷等）不讓設定套用或關閉流程卡住。
        }
    }

    /// <summary>
    /// QCTek 相容層的診斷日誌（<c>%TEMP%\helivms-qctek.log</c>，上限 512 KB）。
    ///
    /// <para>
    /// QCC 實測失敗時（連不上、命令不認識、密碼錯，畫面上長得一樣）這份日誌是
    /// 唯一的線索；命令摘要在 <see cref="QctekDiagnostics.Summarize"/> 遮蔽密碼段
    /// ——日誌會被複製到郵件與工單裡。
    /// </para>
    /// </summary>
    internal static void Diag(string message)
    {
        try
        {
            LogQueue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 佇列已滿（磁碟寫入暫時卡住）：丟掉這一行，不讓診斷拖垮協定時效。
        }
    }

    public void Stop()
    {
        var server = _server;
        _server = null;
        if (server is null)
        {
            return;
        }

        try
        {
            server.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 停止失敗（連線收尾中斷等）不讓關閉流程卡住；listener 已先被 StopAsync 關掉。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMux();
        Stop();
        StopAlarm();
    }

    /// <summary>NEMO 命令所需的裝置資訊與憑證驗證。</summary>
    private sealed class DeviceContext : IQctekDeviceContext
    {
        private readonly AuthService _auth;
        private readonly ChannelRepository _channels;
        private readonly UserRepository _users;

        public DeviceContext(SqliteStore store)
        {
            _auth = new AuthService(store);
            _channels = new ChannelRepository(store);
            _users = new UserRepository(store);
        }

        /// <summary>
        /// 對 CMS 宣告 <c>QCV8001</c>：QCC 以學到的產品字串決定後續路徑
        /// （<c>NV7.1</c>／<c>QCV8001</c>…），宣告自家產品名最相容。
        /// </summary>
        public string ServerVersion => "QCV8001";

        /// <summary>頻道數即時查（CMS 只在連線時問一次，成本可忽略；頻道增減會立刻反映）。</summary>
        public int ChannelCount => _channels.List().Count;

        /// <summary>
        /// 頻道名稱即時查（<c>GET_CONFIG_QCTEK</c> 的每筆記錄）。依 <see cref="ChannelRepository.List"/>
        /// 的排序，與 <see cref="ChannelCount"/> 同一個來源，兩者不會漂移。
        /// </summary>
        public IReadOnlyList<string> ChannelNames
            => _channels.List().Select(static channel => channel.Name).ToList();

        /// <summary>QCV8001 慣例值；HeliVMS 每個頻道有主流＋子流。</summary>
        public int MaxStreams => 2;

        /// <summary>HeliVMS 不提供 VNC——寧可回 FAIL，也不讓 CMS 去連一個不存在的埠。</summary>
        public bool HasVnc => false;

        /// <summary>
        /// 憑證政策跟隨本機登入驗證：啟用時走 <see cref="AuthService.Authenticate"/>
        /// （失敗會計入鎖定與稽核——CMS 設定錯密碼時會鎖掉該帳號，這是有意的：
        /// 與人工登入同一套保護）；未啟用時不驗密碼。
        /// </summary>
        public bool TryAuthenticate(string user, string password)
        {
            if (!_auth.IsAuthEnabled)
            {
                return true;
            }

            return _auth.Authenticate(user, password).Succeeded;
        }

        /// <summary>
        /// <c>QCV8001_AUTH_SENDCODE</c> 之後唯一能做的檢查：使用者存在且啟用。
        /// <para>
        /// 不能做密碼驗證——QCTek 的 digest 需要一份 HeliVMS 沒有的專有轉換密碼
        /// （見 <see cref="QctekSession"/>）。未啟用本機登入驗證時一律放行，與
        /// <see cref="TryAuthenticate"/> 的政策一致；啟用時至少要求帳號真的存在，
        /// 不能任意使用者名都通過。
        /// </para>
        /// </summary>
        public bool IsKnownUser(string user)
        {
            if (!_auth.IsAuthEnabled)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(user) &&
                   _users.GetByUsername(user) is { Enabled: true };
        }

        /// <summary>
        /// 開一路即時影像來源。頻道即時查（<see cref="ChannelRepository.List"/>，與
        /// <see cref="ChannelCount"/> 同一個來源）：CMS 的位圖 bit i 對「第 i 個頻道」，
        /// 停用的頻道與不存在的頻道一樣回 <c>null</c>（狀態幀 0x8002）——不存在的頻道
        /// 不該看起來像「連上了但沒畫面」。
        /// </summary>
        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream)
        {
            var channels = _channels.List();
            if (channelIndex < 0 || channelIndex >= channels.Count)
            {
                return null;
            }

            var channel = channels[channelIndex];
            if (!channel.Enabled)
            {
                return null;
            }

            // 子碼流為空時退回主碼流：多分割用子流省頻寬，但沒有子流可用的頻道
            // 寧可給主流（那格至少有畫面），不要回「無來源」。
            var url = subStream == 1 && !string.IsNullOrWhiteSpace(channel.SubStreamUrl)
                ? channel.SubStreamUrl
                : channel.MainStreamUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return QctekLiveStreamSource.TryOpen(url, QctekCompatHost.Diag);
        }
    }
}
