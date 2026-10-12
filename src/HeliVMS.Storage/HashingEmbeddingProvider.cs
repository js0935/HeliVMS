namespace HeliVMS.Storage;

/// <summary>
/// 決定性字元 n-gram 雜湊嵌入器（§14.1 #8 之後備實作）：無模型、無外部相依，把文字的單字與
/// 相鄰字元對以 FNV-1a 雜湊到固定維度並 L2 正規化，使共享字元較多的字串在餘弦相似度上更靠近。
/// </summary>
/// <remarks>
/// 這是「外部模型縫」的刻意後備：真機可換成 CLIP/ONNX 實作而不動儲存層；此實作讓「模型尚未接上」
/// 時語意搜尋仍能端到端運作、並以純函式閉環測試。中文以相鄰字元對（bigram）近似詞彙，英文則近似
/// 子字串，兩者共用同一條管線。
/// <para>
/// 不可改用 <see cref="object.GetHashCode()"/> 或 <c>string.GetHashCode()</c>：.NET 的字串雜湊
/// 每個行程隨機加鹽，跨行程（重啟後檢索已索引向量）與跨機器都會得到不同向量，索引等於作廢。
/// FNV-1a 對固定輸入恆得同值，才是可持久化的嵌入。
/// </para>
/// </remarks>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>預設維度：足以降低雜湊碰撞，又不必像 CLIP 般數百維。</summary>
    public const int DefaultDimension = 256;

    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>無狀態之共用實例（維度為預設值），供 DI 與測試使用。</summary>
    public static HashingEmbeddingProvider Default { get; } = new();

    /// <param name="dimension">輸出向量維度，必須為正。</param>
    public HashingEmbeddingProvider(int dimension = DefaultDimension)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "向量維度必須為正。");
        }

        Dimension = dimension;
    }

    /// <inheritdoc />
    public string ModelId => $"hashing-chargram-v1-dim{Dimension}";

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public IReadOnlyList<float> Embed(string text)
    {
        var vector = new float[Dimension];
        if (string.IsNullOrEmpty(text))
        {
            return vector;
        }

        // 去掉空白後保留字元序列：中文沒有分隔，空白跳過不影響 bigram 的相鄰性。
        var chars = new List<char>(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            chars.Add(char.ToLowerInvariant(ch));
        }

        if (chars.Count == 0)
        {
            return vector;
        }

        for (var i = 0; i < chars.Count; i++)
        {
            // 單字權重 1，相鄰字元對權重 2：bigram 較能區辨詞彙，但單字讓短查詢也有訊號。
            Accumulate(vector, Hash(chars[i]), 1f);
            if (i + 1 < chars.Count)
            {
                Accumulate(vector, Hash(chars[i], chars[i + 1]), 2f);
            }
        }

        Normalize(vector);
        return vector;
    }

    private static void Accumulate(float[] vector, uint hash, float weight)
    {
        var index = (int)(hash % (uint)vector.Length);
        var sign = (hash & 0x80000000u) == 0 ? 1f : -1f;
        vector[index] += sign * weight;
    }

    private static uint Hash(char a)
    {
        var h = FnvOffsetBasis;
        return (h ^ a) * FnvPrime;
    }

    private static uint Hash(char a, char b)
    {
        var h = (FnvOffsetBasis ^ a) * FnvPrime;
        return (h ^ b) * FnvPrime;
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        foreach (var v in vector)
        {
            sum += (double)v * v;
        }

        if (sum <= 0)
        {
            return;
        }

        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inv;
        }
    }
}
