using System.IO;
using System.Net;
using HeliVMS.Qctek.Alarm;
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
/// 綁定所有介面（CMS 在別的機器上），憑證政策<b>跟隨 HeliVMS 自己的登入驗證</b>：
/// <c>auth.enabled=1</c> 時走 <see cref="AuthService.Authenticate"/>（含鎖定與稽核）；
/// 未啟用驗證時不檢查密碼——與「本機未啟用驗證」的政策一致，但這代表同網段的人
/// 可以查頻道數並訂閱警報（目前相容層只實作這些命令，沒有串流），設定頁的說明
/// 會提醒先啟用登入驗證。HeliVMS 走的是自己的 WHEP 串流，QCC 端目前拿不到畫面。
/// </para>
/// <para>
/// 生命週期照 <see cref="WebHost"/>：MainWindow 建立後 <see cref="ApplySettings"/>，
/// 設定頁按「套用」再即時套用一次；收尾由 MainWindow 的收尾序列呼叫
/// <see cref="Dispose"/>（在執行緒池上，不碰 UI）。
/// </para>
/// </remarks>
public sealed class QctekCompatHost : IDisposable
{
    /// <summary>預設埠：與 web.port（5080）錯開，避免與同機服務互撞。</summary>
    public const int DefaultPort = 5170;

    public const string EnabledKey = "qctek.compat.enabled";
    public const string PortKey = "qctek.compat.port";

    /// <summary>警報回報（M3b）：啟用、CMS 主機、CMS Alarm Service 埠。</summary>
    public const string AlarmEnabledKey = "qctek.alarm.enabled";

    public const string AlarmHostKey = "qctek.alarm.cms_host";
    public const string AlarmPortKey = "qctek.alarm.cms_port";

    private readonly DeviceContext _context;
    private NemoServer? _server;
    private QctekAlarmClient? _alarmClient;
    private bool _disposed;

    public QctekCompatHost(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _context = new DeviceContext(store);
    }

    public bool IsRunning => _server is not null;

    public int Port { get; private set; }

    /// <summary>警報回報是否執行中（M3b）。</summary>
    public bool IsAlarmReporting => _alarmClient is not null;

    /// <summary>警報回報的目標（<c>host:port</c>）；未執行時為 <c>null</c>。</summary>
    public string? AlarmTarget { get; private set; }

    /// <summary>最近一次啟動失敗的訊息；成功時為 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 依設定啟用或停用相容服務（NEMO 伺服器）與警報回報，回傳 NEMO 狀態訊息
    /// （設定頁直接顯示；警報狀態由 <see cref="IsAlarmReporting"/>／
    /// <see cref="AlarmTarget"/> 讀現況）。
    /// </summary>
    public string ApplySettings(SettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 警報回報有獨立開關（M3b）：NEMO 伺服器的啟停不影響它，反之亦然。
        ApplyAlarmSettings(settings);

        var enabled = settings.Get(EnabledKey) == "1";
        var port = ReadPort(settings);

        if (!enabled)
        {
            Stop();
            return Localizer.T("Settings.QctekStopped");
        }

        LastError = null;
        try
        {
            Start(port);
            return string.Format(Localizer.T("Settings.QctekRunning"), port);
        }
        catch (Exception ex)
        {
            // 埠被占用等啟動失敗：確保不留半啟動的伺服器（否則下次套用會洩漏一條 listener）。
            LastError = ex.Message;
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
        if (_server is not null)
        {
            return;
        }

        var server = new NemoServer(_context, IPAddress.Any, port, log: Diag);
        server.Start();
        _server = server;
        Port = port;
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
    private static void Diag(string message)
        => BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-qctek.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
            maxBytes: 512 * 1024);

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
        Stop();
        StopAlarm();
    }

    /// <summary>NEMO 命令所需的裝置資訊與憑證驗證。</summary>
    private sealed class DeviceContext : IQctekDeviceContext
    {
        private readonly AuthService _auth;
        private readonly ChannelRepository _channels;

        public DeviceContext(SqliteStore store)
        {
            _auth = new AuthService(store);
            _channels = new ChannelRepository(store);
        }

        /// <summary>
        /// 對 CMS 宣告 <c>QCV8001</c>：QCC 以學到的產品字串決定後續路徑
        /// （<c>NV7.1</c>／<c>QCV8001</c>…），宣告自家產品名最相容。
        /// </summary>
        public string ServerVersion => "QCV8001";

        /// <summary>頻道數即時查（CMS 只在連線時問一次，成本可忽略；頻道增減會立刻反映）。</summary>
        public int ChannelCount => _channels.List().Count;

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
    }
}
