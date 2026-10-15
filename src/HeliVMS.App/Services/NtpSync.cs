using System.Globalization;
using HeliVMS.Core.Ntp;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>一次 NTP 對時的結果。</summary>
/// <param name="Ok">是否取得伺服器時間（false＝逾時／網路失敗／伺服器未同步）。</param>
/// <param name="ServerUtc">依伺服器推算出的 UTC 時間。</param>
/// <param name="Offset">本地時鐘相對伺服器的偏移（正值＝本地慢、要往前調）。</param>
/// <param name="RoundTrip">往返延遲。</param>
/// <param name="Stratum">伺服器階層。</param>
/// <param name="ClockSet">系統時鐘是否真的被調整（無權限時為 false）。</param>
/// <param name="Error">系統時鐘調整失敗的原因；成功或未嘗試時為 null。</param>
public sealed record NtpSyncOutcome(
    bool Ok,
    DateTime ServerUtc,
    TimeSpan Offset,
    TimeSpan RoundTrip,
    byte Stratum,
    bool ClockSet,
    string? Error);

/// <summary>
/// 設定中心「網路」頁的 NTP 對時（M305；校時引擎為 M68 的 <see cref="NtpClient"/>）。
///
/// <para>
/// 把「查詢 → 記錄 → 盡力調整系統時鐘」收在一處，讓手動「立即對時」與主視窗的定期自動對時
/// 走同一條路徑；否則兩邊各寫一份，日後只會有一邊被修好。
/// </para>
/// </summary>
public static class NtpSync
{
    public const string EnabledKey = "ntp.enabled";
    public const string ServerKey = "ntp.server";
    public const string LastSyncUtcKey = "ntp.last_sync_utc";
    public const string LastOffsetMsKey = "ntp.last_offset_ms";

    /// <summary>預設伺服器。留空時使用；刻意選 Windows 內建的公用時間來源。</summary>
    public const string DefaultServer = "time.windows.com";

    public static string ReadServer(SettingsRepository settings)
    {
        var configured = settings.Get(ServerKey);
        return string.IsNullOrWhiteSpace(configured) ? DefaultServer : configured.Trim();
    }

    public static bool IsEnabled(SettingsRepository settings) => settings.Get(EnabledKey) == "1";

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
            return new NtpSyncOutcome(false, default, default, default, 0, false, null);
        }

        var serverUtc = DateTime.UtcNow + query.Offset;
        settings.Set(LastSyncUtcKey, serverUtc.ToString("o", CultureInfo.InvariantCulture));
        settings.Set(LastOffsetMsKey, ((long)query.Offset.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

        var clockSet = SystemClock.TrySetUtcNow(serverUtc, out var error);
        return new NtpSyncOutcome(true, serverUtc, query.Offset, query.RoundTrip, query.Stratum, clockSet, error);
    }

    /// <summary>讀回上次對時的時間與偏移；從未對時時回傳 null。</summary>
    public static (DateTime ServerUtc, long OffsetMs)? ReadLast(SettingsRepository settings)
    {
        var raw = settings.Get(LastSyncUtcKey);
        if (string.IsNullOrWhiteSpace(raw)
            || !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
        {
            return null;
        }

        var offset = long.TryParse(settings.Get(LastOffsetMsKey), out var ms) ? ms : 0;
        return (when, offset);
    }
}
