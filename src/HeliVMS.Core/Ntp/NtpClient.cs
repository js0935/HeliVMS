using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Core.Ntp;

/// <summary>一次成功的 NTP 查詢樣本（RFC 5905，M68）。</summary>
public sealed record NtpQuery(
    TimeSpan Offset,
    TimeSpan RoundTrip,
    byte Stratum,
    byte LeapIndicator,
    byte Version);

/// <summary>
/// SNTP 用戶端（M68，RFC 5905）——向伺服器送 MODE3 請求並由 4 時間戳計算時鐘偏移與往返延遲。
/// 純 BCL（UdpClient），零外部套件。失敗/逾時/無效樣本一律回傳 null（不拋例外）。
/// </summary>
public sealed class NtpClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>NTP-1900 與 Unix-1970 epoch 秒差。</summary>
    public const long NtpEpochToUnixSeconds = 2_208_988_800L;

    public async Task<NtpQuery?> QueryAsync(
        string server,
        int port = 123,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var udp = new UdpClient(server, port);
        var t1 = DateTime.UtcNow;
        var request = NtpFrames.BuildRequestUtc(t1);
        await udp.SendAsync(request.AsMemory(), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        byte[] response;
        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            response = result.Buffer;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return null;
        }

        var t4 = DateTime.UtcNow;
        return NtpFrames.TryParseResponse(response, t1, t4, out var query) ? query : null;
    }

    /// <summary>把 1900-epoch 的 NTP 秒（含分數）轉為 DateTime（UTC）。</summary>
    public static DateTime NtpSecondsToDateTime(uint seconds, uint fraction)
    {
        var whole = (long)seconds - NtpEpochToUnixSeconds;
        var ticks = whole * TimeSpan.TicksPerSecond
            + (long)(fraction / 4_294_967_296.0 * TimeSpan.TicksPerSecond);
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
    }

    /// <summary>DateTime（UTC）轉為 NTP 秒＋分數（RFC 5905 時間戳場）。</summary>
    public static (uint Seconds, uint Fraction) DateTimeToNtp(DateTime utc)
    {
        var ticks = utc.Ticks;
        var whole = (long)DateTimeToUnixSeconds(utc) + NtpEpochToUnixSeconds;
        var fracTicks = ticks % TimeSpan.TicksPerSecond;
        var fraction = (uint)(fracTicks / (double)TimeSpan.TicksPerSecond * 4_294_967_296.0);
        return ((uint)whole, fraction);
    }

    private static long DateTimeToUnixSeconds(DateTime utc)
        => utc.Ticks / TimeSpan.TicksPerSecond - 62135596800L;
}