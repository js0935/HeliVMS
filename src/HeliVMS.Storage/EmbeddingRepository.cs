namespace HeliVMS.Storage;

/// <summary>CLIP 向量語意搜尋之命中（M151，§14.7 #8 管線）：相似度愈高愈相關（餘弦）。</summary>
public sealed record ClipSearchHit(long RefId, string SourceType, string? Label, double Score);

/// <summary>
/// CLIP 向量語意編碼索引（M151，§14.7 #8）：`clip_embeddings` 表持久化向量（BLOB，
/// IEEE754 float32 LE）與其一階範數；Search 以餘弦相似排序回傳 topK。生成 embedding
/// 之模型屬外部縫（真機/AI 阻塞項），本管線僅負責索引與檢索，純數學、可閉環測試。
/// </summary>
public sealed class EmbeddingRepository
{
    public const int DefaultTopK = 20;

    private readonly SqliteStore _store;

    public EmbeddingRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>以 ref_id 為鍵 upsert 一筆向量（同 source 同 ref 只保留新向量）。</summary>
    public void Upsert(string sourceType, long refId, string? label, IReadOnlyList<float> vector)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            throw new ArgumentException("來源類型不可為空白。", nameof(sourceType));
        }

        if (vector is null || vector.Count == 0)
        {
            throw new ArgumentException("向量不可為空。", nameof(vector));
        }

        _store.Execute(
            """
            DELETE FROM clip_embeddings WHERE source_type = $st AND ref_id = $rid;
            INSERT INTO clip_embeddings(source_type, ref_id, label, vector, norm)
            VALUES ($st, $rid, $label, $vec, $norm);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", sourceType);
                cmd.Parameters.AddWithValue("$rid", refId);
                cmd.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vec", ToBytes(vector));
                cmd.Parameters.AddWithValue("$norm", L2Norm(vector));
            });
    }

    /// <summary>以餘弦相似度回傳與目標最相近之 topK 筆（Score＝餘弦相似，愈高愈相關）。</summary>
    public IReadOnlyList<ClipSearchHit> Search(IReadOnlyList<float> query, int topK = DefaultTopK)
    {
        if (query is null || query.Count == 0)
        {
            throw new ArgumentException("查詢向量不可為空。", nameof(query));
        }

        if (topK <= 0)
        {
            topK = DefaultTopK;
        }

        var dim = query.Count;
        var qNorm = L2Norm(query);

        var hits = _store.Query(
            """
            SELECT ref_id, source_type, label, vector, norm FROM clip_embeddings;
            """,
            static r =>
            {
                var list = new List<EmbeddingRow>();
                while (r.Read())
                {
                    list.Add(new EmbeddingRow(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2),
                        (byte[])r[3],
                        r.GetDouble(4)));
                }

                return list;
            });

        return hits
            .Select(h => new ClipSearchHit(h.RefId, h.SourceType, h.Label, Score(h.Vector, h.Norm, query, qNorm, dim)))
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>依來源與 ref 讀取單筆向量（無則 null；M153 索引管理）。</summary>
    public IReadOnlyList<float>? ByRef(string sourceType, long refId) =>
        _store.Query(
            """
            SELECT vector FROM clip_embeddings WHERE source_type = $st AND ref_id = $rid;
            """,
            r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                var bytes = (byte[])r[0];
                var count = bytes.Length / 4;
                var values = new float[count];
                for (var i = 0; i < count; i++)
                {
                    values[i] = BitConverter.ToSingle(bytes, i * 4);
                }

                return values;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", sourceType);
                cmd.Parameters.AddWithValue("$rid", refId);
            });

    /// <summary>依來源與 ref 刪除單筆向量；回傳是否命中（M153 索引管理）。</summary>
    public bool Delete(string sourceType, long refId)
    {
        _store.Execute(
            "DELETE FROM clip_embeddings WHERE source_type = $st AND ref_id = $rid;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", sourceType);
                cmd.Parameters.AddWithValue("$rid", refId);
            });
        return _store.Query<int>("SELECT changes();", r => r.Read() ? r.GetInt32(0) : 0) > 0;
    }

    /// <summary>清除全部向量（供測試與重建索引）。</summary>
    public void Clear()
    {
        _store.Execute("DELETE FROM clip_embeddings;");
    }

    private static double Score(byte[] bytes, double rowNorm, IReadOnlyList<float> query, double qNorm, int dim)
    {
        var dot = 0.0;
        for (var i = 0; i < dim; i++)
        {
            dot += BitConverter.IsLittleEndian
                ? BitConverter.ToSingle(bytes, i * 4) * query[i]
                : BitConverter.ToSingle(BitConverter.GetBytes(
                      BitConverter.ToSingle(bytes, i * 4)).Reverse().ToArray()) * query[i];
        }

        var denom = rowNorm * qNorm;
        return denom <= 0 ? 0 : dot / denom;
    }

    private static double L2Norm(IReadOnlyList<float> v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += (double)x * x;
        }

        return Math.Sqrt(sum);
    }

    private static byte[] ToBytes(IReadOnlyList<float> v)
    {
        var bytes = new byte[v.Count * 4];
        for (var i = 0; i < v.Count; i++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(v[i]), 0, bytes, i * 4, 4);
        }

        return bytes;
    }

    private sealed record EmbeddingRow(long RefId, string SourceType, string? Label, byte[] Vector, double Norm);
}