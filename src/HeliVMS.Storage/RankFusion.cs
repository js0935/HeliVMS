namespace HeliVMS.Storage;

/// <summary>融合輸入項（M154，§14.7 #7：FTS bm25 與 CLIP 向量之單一命中）。</summary>
public sealed record FusionItem(
    string Key,
    double TextScore,
    double? VectorScore,
    double TextWeight = 1.0,
    double VectorWeight = 1.0);

/// <summary>
/// 排名融合（M154，§14.7 #7）：把語法全文（bm25，分數愈低愈相關）與向量語意
/// （餘弦，分數愈高愈相關）兩路命正規化為 [0,1]（愈高愈相關）後加權合併，
/// 回傳穩定排序。純 BCL、不依賴資料庫，可閉環單元測試。
/// </summary>
public static class RankFusion
{
    /// <summary>融合並排序；回傳（Key, 正規化合併分數）清單，降冪。</summary>
    public static IReadOnlyList<FusedResult> Fuse(IEnumerable<FusionItem> items)
    {
        return items
            .Select(i => new FusedResult(i.Key, Score(i)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>雙路（文字早於/向量）正規化內產生 sorted union key（供 SPA 對接同識別）。</summary>
    public static string NormalizeKey(string source, long refId) => $"{source}:{refId}";

    private static double Score(FusionItem i)
    {
        // TextScore=bm25 rank：愈小愈相關 → 1/(1+score) 逐項映射到 (0,1]。
        var textNorm = i.TextScore > 0 ? 1 / (1 + i.TextScore) : 0.0;
        // VectorScore=餘弦：[-1,1] → [0,1]。
        var vectorNorm = i.VectorScore is double v ? Math.Clamp((v + 1) / 2, 0, 1) : 0.0;

        return i.TextScore > 0 && i.VectorScore is null
            ? textNorm
            : i.TextScore <= 0 && i.VectorScore is not null
                ? vectorNorm
                : (textNorm * i.TextWeight + vectorNorm * i.VectorWeight) / (i.TextWeight + i.VectorWeight);
    }
}

/// <summary>融合結果（M154）：Key 與正規化合併分數（0..1，愈高愈相關）。</summary>
public sealed record FusedResult(string Key, double Score);