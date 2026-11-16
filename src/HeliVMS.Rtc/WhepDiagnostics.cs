using HeliVMS.Shared;

namespace HeliVMS.Rtc;

/// <summary>
/// 即時監看的生命週期日誌（<c>%TEMP%\helivms-whep.log</c>，上限 256 KB）。
/// </summary>
/// <remarks>
/// <para>
/// 「畫面突然沒了」有兩種處置完全相反的原因：觀眾自己離開了（送出 DELETE，或瀏覽器消失後被判定
/// 斷線而由伺服器收掉），或是<b>一個健康的瀏覽器被誤判成斷線</b>。少了這條記錄，現場只看得到
/// 「人數少一個」——而兩者要查的方向不同（前者沒事，後者要去調判定門檻）。
/// </para>
/// <para>
/// 只寫事件，不寫每一包 RTCP：10 路監看每秒就是十幾行，會把真正的事件淹掉。檔案有上限，
/// 所以可以常駐。
/// </para>
/// </remarks>
internal static class WhepDiagnostics
{
    /// <summary>檔名（放在 <c>%TEMP%</c>，與 <c>helivms-live.log</c>／<c>helivms-playback.log</c> 同一慣例）。</summary>
    internal const string FileName = "helivms-whep.log";

    /// <summary>檔案大小上限；超過只留尾巴。</summary>
    internal const long MaxBytes = 256 * 1024;

    /// <summary>附加一行（UTC 時間戳 + 內容）。</summary>
    internal static void Write(string line)
        => BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), FileName),
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}",
            MaxBytes);
}
