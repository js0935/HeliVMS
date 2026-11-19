namespace HeliVMS.Qctek.Live;

/// <summary>
/// 一路即時影像的 H.264 來源：Annex-B 基本流。
/// </summary>
/// <remarks>
/// <para>
/// 由 App 端實作（ffmpeg 行程自 RTSP 拉流、<c>-f h264</c> 輸出）；Qctek 專案不引用
/// Media/Storage/Recording——「每頻道一個行程」是 App 的資源決策（便宜攝影機的 RTSP
/// 工作階段有限），相容層只消費位元組。
/// </para>
/// <para>
/// 實作必須保證：<see cref="Output"/> 在物件回傳前即可讀（行程已在跑）；
/// <see cref="IAsyncDisposable.DisposeAsync"/> 停止行程並關閉管道。來源消失
/// （攝影機離線）會讓 <see cref="Output"/> 讀到 0——live 迴圈對「有來源但沒影格」
/// 的頻道每秒回狀態幀 0x8001。
/// </para>
/// </remarks>
public interface IQctekLiveSource : IAsyncDisposable
{
    /// <summary>Annex-B 的 H.264 基本流（ffmpeg <c>-f h264</c> 的 stdout）。</summary>
    Stream Output { get; }

    /// <summary>
    /// 這一路來源的音訊（G.711 µ-law 位元組流）；沒掛音訊輸出時是 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 預設實作回 <c>null</c>：只有即時影像會掛音訊（<c>channels.listen_enabled</c>），
    /// 遠端回放與測試用的來源不需要為此多寫一個成員。回 <c>null</c> 時 live 迴圈
    /// 只在開流時送一包近靜音幀，與加入音訊前的行為完全相同。
    /// </remarks>
    IQctekLiveAudio? Audio => null;
}

/// <summary>
/// 一路來源的音訊位元組流：<b>非阻塞</b>讀取，讀不到就回 0。
/// </summary>
/// <remarks>
/// 為什麼不是 <see cref="Stream"/>：即時影像的輪詢執行緒每 33ms 走一圈，阻塞讀會讓
/// keepalive 與影像 tick 一起停擺（QCC 的狀態機以 33ms 為週期）；而聲音本來就可以
/// 遲到或丟失，讀不到不是錯誤。
/// </remarks>
public interface IQctekLiveAudio
{
    /// <summary>把目前已到達的位元組讀進 <paramref name="destination"/>；沒有資料回 0。</summary>
    int Read(Span<byte> destination);
}
