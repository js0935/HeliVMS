using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>一次「套用」之後兩個服務各自的狀態文字（設定頁兩條狀態列直接顯示）。</summary>
public sealed record NetworkApplyResult(string Web, string Qctek);

/// <summary>
/// 內嵌 Web 服務與 QCTek 相容服務的<b>連接埠協調者</b>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：QCC 連的是裝置的 Web 埠（它沒有地方可以填 NEMO 埠），而真機的 NEMO
/// 與 HTTP 多工在同一個埠。所以只要相容服務啟用、Web 也啟用，公開埠就要由
/// <see cref="QctekPortMux"/> 擁有，Kestrel 改聽系統指派的臨時埠；<c>qctek.compat.port</c>
/// 填了別的號碼時，另外再開一個獨立 listener 給明確指定該埠的遠端 CMS
/// （見 <see cref="QctekPortPlanner"/>）。
/// </para>
/// <para>
/// 兩個順序都是不可顛倒的：
/// </para>
/// <list type="number">
/// <item><b>要共用時</b>：Kestrel 先讓出公開埠（改聽臨時埠），多工器才綁公開埠。
/// 顛倒的話多工器會先撞上「連接埠已被占用」，設定頁只顯示一句啟動失敗。</item>
/// <item><b>不共用時</b>：多工器先放掉公開埠，Kestrel 才綁得回去。顛倒的話
/// Web 服務會起不來——而操作員只是改了另一個服務的埠。</item>
/// </list>
/// <para>
/// 失敗要能還原：多工器綁不上（例如公開埠被別的程式占用）時，把 Web 服務還原到
/// 公開埠。管理介面至少還在，比「兩個都不見」好。
/// </para>
/// </remarks>
public sealed class NetworkPortCoordinator
{
    private readonly WebHost _web;
    private readonly QctekCompatHost _qctek;

    public NetworkPortCoordinator(WebHost web, QctekCompatHost qctek)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(qctek);
        _web = web;
        _qctek = qctek;

        // 讓直接呼叫 QctekCompatHost.ApplySettings 的舊路徑也走同一套順序，
        // 不會在不知情的情況下嘗試用 NemoServer 綁多工器的埠。
        qctek.AttachCoordinator(this);
    }

    /// <summary>目前是否由多工器擁有公開埠。</summary>
    public bool IsSharingPorts => _web.IsRunning && _web.IsSharing;

    /// <summary>依設定啟停兩個服務，回傳各自的狀態文字。</summary>
    public NetworkApplyResult Apply(SettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var plan = QctekPortPlanner.Plan(
            settings.Get(WebHost.EnabledKey) == "1",
            settings.Get(QctekCompatHost.EnabledKey) == "1",
            ReadPort(settings, WebHost.PortKey, WebHost.DefaultPort),
            ReadPort(settings, QctekCompatHost.PortKey, QctekCompatHost.DefaultPort));

        QctekCompatHost.Diag(
            $"連接埠協調：Web啟用={settings.Get(WebHost.EnabledKey) == "1"} " +
            $"相容啟用={settings.Get(QctekCompatHost.EnabledKey) == "1"} " +
            $"Web埠={ReadPort(settings, WebHost.PortKey, WebHost.DefaultPort)} " +
            $"相容埠={ReadPort(settings, QctekCompatHost.PortKey, QctekCompatHost.DefaultPort)} " +
            $"→ 共用={plan.ShareWebPort} 公開埠={plan.PublicPort} 獨立埠={plan.StandalonePort}");

        if (plan.ShareWebPort)
        {
            // 順序①：Web 先讓出公開埠。
            var web = _web.ApplyLocal(settings, sharePort: true);
            if (!_web.IsRunning)
            {
                // Web 起不來就沒有「公開埠」可以讓給多工器；真正的原因在 helivms-web.log。
                QctekCompatHost.Diag($"Web 沒有起來，公開埠無法讓給多工器：{_web.LastError ?? "未知"}");
            }

            if (_web.IsRunning)
            {
                var qctek = _qctek.ApplyLocal(settings, plan, httpPort: _web.InternalPort);
                if (_qctek.IsMuxRunning)
                {
                    QctekCompatHost.Diag(
                        $"連接埠協調完成：Web 內部埠 {_web.InternalPort}，多工器 {_qctek.Port}");
                    return new NetworkApplyResult(web, qctek);
                }

                // 多工器失敗：把 Web 還原到公開埠，至少管理介面還在。
                QctekCompatHost.Diag("多工器沒有起來：把 Web 還原到公開埠。");
                _web.ApplyLocal(settings, sharePort: false);
            }

            // Web 起不來（或已還原）時，相容服務退回獨立埠；能不能綁上看公開埠是否已被占。
            var fallback = _qctek.ApplyLocal(
                settings,
                QctekPortPlanner.Standalone(
                    compatibilityEnabled: true,
                    ReadPort(settings, QctekCompatHost.PortKey, QctekCompatHost.DefaultPort)),
                httpPort: 0);
            return new NetworkApplyResult(
                _web.IsRunning
                    ? string.Format(Localizer.T("Settings.WebRunning"), _web.Port)
                    : string.Format(Localizer.T("Settings.WebStartFailed"), _web.LastError ?? string.Empty),
                fallback);
        }

        // 順序②：多工器先放掉公開埠，Web 才綁得回去。
        _qctek.StopMux();
        var webStatus = _web.ApplyLocal(settings, sharePort: false);
        var qctekStatus = _qctek.ApplyLocal(settings, plan, httpPort: 0);
        return new NetworkApplyResult(webStatus, qctekStatus);
    }

    private static int ReadPort(SettingsRepository settings, string key, int fallback)
        => int.TryParse(settings.Get(key), out var port) && port is > 0 and <= 65535 ? port : fallback;
}
