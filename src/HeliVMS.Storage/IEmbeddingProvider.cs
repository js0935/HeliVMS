namespace HeliVMS.Storage;

/// <summary>
/// 嵌入供應商：把一段輸入（文字或影像描述）編碼成固定維度向量，供
/// <see cref="EmbeddingRepository"/> 索引與 <see cref="ForensicSemanticSearch"/> 檢索。
/// </summary>
/// <remarks>
/// 產生 embedding 的模型（CLIP/ONNX 等）屬外部縫（§14.1 #8）：真機需 GPU／模型檔，無法進版控。
/// 這個介面把「產生向量」與「索引／檢索」解耦——儲存層只認 <see cref="IReadOnlyList{T}"/>，
/// 換模型時只替換實作，索引資料以 <see cref="Dimension"/> 判斷是否需重建。內建
/// <see cref="HashingEmbeddingProvider"/> 為無模型、可離線閉環測試之決定性後備。
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>模型識別（含版本與維度），可寫入索引以判定換版重建。</summary>
    string ModelId { get; }

    /// <summary>輸出向量維度。索引與查詢維度不同即不可比（<see cref="EmbeddingRepository.Search"/> 會略過）。</summary>
    int Dimension { get; }

    /// <summary>把文字編碼成長度 <see cref="Dimension"/> 的向量；無法編碼（空字串）時回全零向量。</summary>
    IReadOnlyList<float> Embed(string text);
}
