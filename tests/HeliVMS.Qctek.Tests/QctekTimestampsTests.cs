using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// FRAM 時戳欄位的換算（<c>+0x14</c> 微秒是「秒內偏移」）。
/// </summary>
/// <remarks>
/// 這裡釘的是即時影像與遠端回放<b>共用</b>的那一份口徑。填成
/// <see cref="DateTime.Microsecond"/>（只取毫秒之內）時每秒少掉 <c>Millisecond × 1000</c>
/// 微秒，QCC 的 <c>QC_StreamDec::DecProc</c> 對牆鐘的容差判定會把非關鍵幀整包丟掉——
/// 畫面只是「有點頓」，兩邊日誌乾淨。
/// </remarks>
public sealed class QctekTimestampsTests
{
    [Fact]
    public void 微秒欄位是秒內偏移而不是毫秒內偏移()
    {
        var instant = new DateTime(2026, 11, 19, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560);

        // .123456 秒 → 123456 微秒。舊寫法（只取 Microsecond）會給 456。
        Assert.Equal(123456, QctekTimestamps.MicrosecondOfSecond(instant));

        // 已是整秒的時刻是 0，不是 -1 或上一秒的殘值。
        Assert.Equal(0, QctekTimestamps.MicrosecondOfSecond(instant.AddTicks(-4560).AddMilliseconds(-123)));
    }

    [Fact]
    public void 帶位移的時刻以UTC換算微秒()
    {
        // QCC 的時戳是 UTC 的 <c>qc_timeval</c>；本地時間的位移不該進到線路欄位，
        // 否則同一段錄影在本機（UTC+8）與 UTC 機器上會差 8 小時。
        var local = new DateTimeOffset(2026, 11, 19, 20, 0, 0, TimeSpan.FromHours(8)).AddMilliseconds(789);

        Assert.Equal(789000, QctekTimestamps.MicrosecondOfSecond(local));
    }
}
