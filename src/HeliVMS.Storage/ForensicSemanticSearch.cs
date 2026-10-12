namespace HeliVMS.Storage;

/// <summary>語意融合搜尋命中（§14.7 #7）：FTS bm25 與 CLIP 向量餘弦經 <see cref="RankFusion"/> 合併之單一結果，Score∈[0,1] 愈高愈相關。</summary>
public sealed record SemanticSearchHit(
    ForensicSource Source,
    long SourceId,
    string Text,
    DateTime OccurredAtUtc,
    double Score,
    string? Status = null);

/// <summary>
/// 法證語意融合搜尋（§14.7 #7）：把 FTS5 全文（<see cref="UnifiedEventSearch"/>，bm25）與 CLIP 向量
/// （<see cref="EmbeddingRepository"/>，餘弦）兩路命中以 <see cref="RankFusion"/> 加權合併。
///
/// <para>
/// 向量檢索本身早已可用，但先前只是一個孤立的 <c>/api/clip/*</c> 端點，從未與文字檢索同框排序——
/// 「缺真正的向量檢索」指的就是這個接線。查詢向量由外部模型產生（外部縫），本協調器只負責融合：
/// 向量命中若不在文字命中內，會回查來源表補齊顯示所需的中文片段與時間。
/// </para>
/// </summary>
public sealed class ForensicSemanticSearch
{
    /// <summary>來源字串約定（與 <c>/api/clip/index</c> 索引端一致）。</summary>
    public static string SourceName(ForensicSource source) => source switch
    {
        ForensicSource.Alarm => "alarm",
        ForensicSource.Door => "door",
        ForensicSource.Pos => "pos",
        ForensicSource.EdgeSmart => "edge",
        _ => "unknown",
    };

    /// <summary>字串反解回來源；未知字串回 null（該向量不參與融合）。</summary>
    public static ForensicSource? SourceOf(string sourceType) => sourceType switch
    {
        "alarm" => ForensicSource.Alarm,
        "door" => ForensicSource.Door,
        "pos" => ForensicSource.Pos,
        "edge" => ForensicSource.EdgeSmart,
        _ => null,
    };

    private readonly UnifiedEventSearch _text;
    private readonly EmbeddingRepository _vectors;

    public ForensicSemanticSearch(SqliteStore store)
        : this(new UnifiedEventSearch(store), new EmbeddingRepository(store))
    {
    }

    public ForensicSemanticSearch(UnifiedEventSearch text, EmbeddingRepository vectors)
    {
        _text = text;
        _vectors = vectors;
    }

    /// <summary>
    /// 融合文字（<paramref name="query"/>）與向量（<paramref name="queryVector"/>）兩路命中。
    /// 兩者皆可單獨缺席：只給文字＝退化為全文檢索；只給向量＝純相似度檢索。
    /// </summary>
    public IReadOnlyList<SemanticSearchHit> Search(
        string query,
        IReadOnlyList<float>? queryVector = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        ForensicSource sourceMask = ForensicSource.All,
        int limit = 20)
    {
        if (limit <= 0)
        {
            limit = 20;
        }

        var hasText = !string.IsNullOrWhiteSpace(query);
        var hasVector = queryVector is { Count: > 0 };
        if (!hasText && !hasVector)
        {
            return [];
        }

        var textHits = hasText
            ? _text.Search(query, fromUtc, toUtc, sourceMask, limit)
            : [];

        var vectorHits = hasVector
            ? _vectors.Search(queryVector!, limit)
            : [];

        // 先以文字命中的後設資料建立 key → hit；key 形如 "alarm:12"。
        var meta = new Dictionary<string, ForensicSearchHit>(StringComparer.Ordinal);
        foreach (var h in textHits)
        {
            meta[RankFusion.NormalizeKey(SourceName(h.Source), h.SourceId)] = h;
        }

        // 向量命中若來源可辨、在來源遮罩內、且不在文字命中裡，就回查來源表補齊後設資料。
        var unmapped = vectorHits
            .Where(v => SourceOf(v.SourceType) is { } s && sourceMask.HasFlag(s))
            .Where(v => !meta.ContainsKey(RankFusion.NormalizeKey(v.SourceType, v.RefId)))
            .ToList();

        foreach (var group in unmapped.GroupBy(v => SourceOf(v.SourceType)!.Value))
        {
            foreach (var h in _text.ByIds(group.Key, group.Select(v => v.RefId).Distinct().ToList()))
            {
                if (fromUtc is { } f && h.OccurredAtUtc < f)
                {
                    continue;
                }

                if (toUtc is { } t && h.OccurredAtUtc >= t)
                {
                    continue;
                }

                meta[RankFusion.NormalizeKey(SourceName(h.Source), h.SourceId)] = h;
            }
        }

        // bm25 回傳負值（愈負愈相關），RankFusion 期望正向相關度，故取負轉正。
        var textScoreByKey = textHits.ToDictionary(
            h => RankFusion.NormalizeKey(SourceName(h.Source), h.SourceId),
            h => h.Rank < 0 ? -h.Rank : h.Rank,
            StringComparer.Ordinal);

        var vectorScoreByKey = vectorHits
            .Where(v => meta.ContainsKey(RankFusion.NormalizeKey(v.SourceType, v.RefId)))
            .GroupBy(v => RankFusion.NormalizeKey(v.SourceType, v.RefId))
            .ToDictionary(g => g.Key, g => g.First().Score, StringComparer.Ordinal);

        var items = meta.Keys.Select(k => new FusionItem(
            k,
            textScoreByKey.TryGetValue(k, out var ts) ? ts : 0.0,
            vectorScoreByKey.TryGetValue(k, out var vs) ? vs : null));

        var result = new List<SemanticSearchHit>();
        foreach (var fused in RankFusion.Fuse(items))
        {
            if (result.Count >= limit)
            {
                break;
            }

            if (meta.TryGetValue(fused.Key, out var h))
            {
                result.Add(new SemanticSearchHit(h.Source, h.SourceId, h.Text, h.OccurredAtUtc, fused.Score, h.Status));
            }
        }

        return result;
    }
}
