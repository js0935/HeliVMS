using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// 影片摘要供應商：把一段時間內的事件快照合成一份「摘要」（目前為拼貼 BMP ＋ manifest ＋摘要文字）。
/// </summary>
/// <remarks>
/// <para>
/// 真正做「偵測物件動態幀合成短片」的外部模型（§5.9）屬外部縫（§14.1 #8）：需要模型或 ffmpeg，
/// 無法在沒有硬體的 CI 上跑。此介面把「怎麼產生摘要」抽離 UI，讓呼叫端只依賴抽象，
/// 真機換上模型實作即可，內建 <see cref="CollageVideoSummaryProvider"/> 則讓功能在模型接上前仍能端到端運作。
/// </para>
/// <para>
/// 與 M291 的 <c>IEmbeddingProvider</c> 同一套路：介面＋決定性後備，後備以純函式離線測試，
/// 換模型不必動呼叫端。
/// </para>
/// </remarks>
public interface IVideoSummaryProvider
{
    /// <summary>供應商識別（供記錄／除錯；真機實作可回模型版本）。</summary>
    string ModelId { get; }

    /// <summary>
    /// 依 <paramref name="request"/> 與候選事件產生摘要。無可用畫面時回 <c>null</c> 且不寫任何檔
    /// （與 M63 既有行為一致）。<paramref name="outputDir"/> 由呼叫端決定（通常為資料根下的 <c>synopsis</c>）。
    /// </summary>
    SynopsisResult? Generate(SynopsisRequest request, IReadOnlyList<AlarmEventRecord> events, string outputDir);
}
