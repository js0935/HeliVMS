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
}
