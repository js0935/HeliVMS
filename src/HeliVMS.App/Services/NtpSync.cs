using HeliVMS.Core.Ntp;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>一次 NTP 對時的結果。</summary>
/// <param name="Ok">是否取得伺服器時間（false＝逾時／網路失敗／伺服器未同步）。</param>
/// <param name="ServerUtc">依伺服器推算出的 UTC 時間。</param>
/// <param name="Offset">本地時鐘相對伺服器的偏移（正值＝本地慢、要往前調）。</param>
/// <param name="RoundTrip">往返延遲。</param>
/// <param name="Stratum">伺服器階層。</param>
/// <param name="ClockSet">系統時鐘是否由本行程直接校正（需要校時權限）。</param>
/// <param name="Error">本行程直接校正失敗的原因；成功或未嘗試時為 null。</param>
/// <param name="ServiceRequested">是否已改請 <c>HeliVMS.Agent</c> 服務代為校時（本行程無權限時）。</param>
public sealed record NtpSyncOutcome(
    bool Ok,
    DateTime ServerUtc,
    TimeSpan Offset,
    TimeSpan RoundTrip,
    byte Stratum,
    bool ClockSet,
    string? Error,
    bool ServiceRequested);

/// <summary>
/// 設定中心「網路」頁的 NTP 對時（M305；校時引擎為 M68 的 <see cref="NtpClient"/>）。
///
/// <para>
/// 把「查詢 → 記錄 → 校正系統時鐘」收在一處，讓手動「立即對時」與主視窗的定期自動對時走同一條路徑。
/// 校正 Windows 系統時鐘需要 <c>SeSystemtimePrivilege</c>，一般（未提高權限的）桌面行程沒有，於此情境
/// 改為在設定庫留下請求、由以 LocalSystem 執行且預設持有該權限的 <c>HeliVMS.Agent</c> 服務代勞
/// （見 <see cref="NtpSettings"/> 與 Agent 的 <c>NtpSyncWorker</c>）。
/// </para>
/// </summary>
public static class NtpSync
{
    public const string EnabledKey = NtpSettings.Enabled;
    public const string ServerKey = NtpSettings.Server;
    public const string LastSyncUtcKey = NtpSettings.LastSyncUtc;
    public const string LastOffsetMsKey = NtpSettings.LastOffsetMs;

    /// <summary>預設伺服器。留空時使用；刻意選 Windows 內建的公用時間來源。</summary>
    public const string DefaultServer = NtpSettings.DefaultServer;

    public static string ReadServer(SettingsRepository settings) => NtpSettings.ReadServer(settings);

    public static bool IsEnabled(SettingsRepository settings) => NtpSettings.IsEnabled(settings);

    /// <summary>
    /// 對 <paramref name="server"/>（未指定時用設定的伺服器）對時，並盡力調整系統時鐘。
    /// 不論時鐘是否調整成功，只要拿到伺服器時間就會記錄「上次對時時間與偏移」，供頁面重開時顯示。
    /// </summary>
    public static async Task<NtpSyncOutcome> SyncAsync(
        SettingsRepository settings,
        string? server = null,
        CancellationToken ct = default)
    {
        var target = (server ?? ReadServer(settings)).Trim();
        if (target.Length == 0)
        {
            target = DefaultServer;
        }

        var query = await new NtpClient().QueryAsync(target, ct: ct).ConfigureAwait(false);
        if (query is null)
        {
            return new NtpSyncOutcome(false, default, default, default, 0, false, null, false);
        }

        var serverUtc = DateTime.UtcNow + query.Offset;
        settings.Set(NtpSettings.LastSyncUtc, NtpSettings.FormatUtc(serverUtc));
        settings.Set(NtpSettings.LastOffsetMs, NtpSettings.FormatOffsetMs(query.Offset));

        // 先就地校正（本行程有校時權限時，例如以系統管理員身分執行）；失敗則改請 Agent 服務代勞，
        // 讓無人值守的機器也能校時，不必每次都提高權限。
        var clockSet = SystemClock.TrySetUtcNow(serverUtc, out var error);
        var serviceRequested = false;
        if (!clockSet)
        {
            NtpSettings.RequestSync(settings);
            serviceRequested = true;
        }

        return new NtpSyncOutcome(true, serverUtc, query.Offset, query.RoundTrip, query.Stratum, clockSet, error, serviceRequested);
    }

    /// <summary>讀回上次對時的時間與偏移；從未對時時回傳 null。</summary>
    public static (DateTime ServerUtc, long OffsetMs)? ReadLast(SettingsRepository settings) => NtpSettings.ReadLast(settings);
}
