namespace HeliVMS.Qctek.Live;

/// <summary>
/// FRAM 時戳欄位的換算口徑（<c>+0x14</c> 微秒、<c>+0x18</c> Unix 秒）。
/// </summary>
/// <remarks>
/// <para>
/// <b>微秒欄位是「秒內偏移」（0–999999），不是「毫秒內偏移」。</b>它與
/// <c>+0x18</c> 的整秒配成一組 <c>qc_timeval</c>，QCC 的 <c>QC_StreamDec::DecProc</c>
/// 拿兩者相加來對牆鐘調速。以 <see cref="DateTime.Microsecond"/>（只取毫秒之內的那
/// 0–999）填這個欄位，等於每秒都少了 <c>Millisecond × 1000</c> 微秒——最多差近一秒，
/// 而超出容差的非關鍵幀會被客戶端整包丟棄：畫面看起來「有點頓」，兩邊日誌乾淨。
/// </para>
/// <para>
/// 即時影像（牆鐘）與遠端回放（錄影時刻）都走這一份口徑，不要各自寫一次乘法。
/// </para>
/// </remarks>
public static class QctekTimestamps
{
    /// <summary>某個時刻在它所屬那一秒之內的微秒數（0–999999）。</summary>
    public static int MicrosecondOfSecond(DateTime value)
        => (value.Millisecond * 1000) + value.Microsecond;

    /// <summary>同上，但先換成 UTC 的牆鐘值（<c>DateTimeOffset</c> 的位移不進 FRAM）。</summary>
    public static int MicrosecondOfSecond(DateTimeOffset value)
        => MicrosecondOfSecond(value.UtcDateTime);
}
