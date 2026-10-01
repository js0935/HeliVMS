namespace HeliVMS.Storage;

/// <summary>CLIP 向量語意搜尋之命中（M151，§14.7 #8 管線）：相似度愈高愈相關（餘弦）。</summary>
public sealed record ClipSearchHit(long RefId, string SourceType, string? Label, double Score);

/// <summary>
/// CLIP 向量語意編碼索引（M151，M242 修正維度與位元組序，§14.7 #8 管線）：`clip_embeddings` 表持久化向量
/// （BLOB，IEEE754 float32 **小端**，與讀取端約定一致）與其一階範數；Search 以餘弦相似排序回傳 topK。
/// 生成 embedding 之模型屬外部縫（真機/AI 阻塞項），本管線僅負責索引與檢索，純數學、可閉環測試。
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
    /// <remarks>
    /// M242：維度不一致時必須跳過而非越界。若放任查詢維度大於資料庫向量長度，
    /// <see cref="Score"/> 會在讀 BLOB 時拋出與呼叫端無關的例外——模型換版（512→768）
    /// 這種日常事件不該變成一個看不懂的崩潰。
    /// </remarks>
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

        var rows = _store.Query(
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

        // 不同維度的向量本來就不可比（內積的長度都不同），跳過它們。
        var mismatched = 0;
        var scored = new List<ClipSearchHit>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Vector.Length != dim * sizeof(float))
            {
                mismatched++;
                continue;
            }

            scored.Add(new ClipSearchHit(row.RefId, row.SourceType, row.Label, Score(row.Vector, row.Norm, query, qNorm)));
        }

        if (mismatched > 0)
        {
            LastMismatchedCount = mismatched;
        }

        return scored
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>最近一次 <see cref="Search"/> 因維度不符而略過的向量筆數（M242，換版時用來判斷需重建索引）。</summary>
    public int LastMismatchedCount { get; private set; }

    /// <summary>
    /// 資料庫中已索引向量的維度（M242）。模型換版後可據此判定舊向量需要重建，
    /// 也讓呼叫端不必靠猜測查詢向量的長度。
    /// </summary>
    public int? StoredDimension()
    {
        var length = _store.Query(
            "SELECT length(vector) FROM clip_embeddings LIMIT 1;",
            r => r.Read() ? r.GetInt32(0) : 0);
        return length > 0 ? length / sizeof(float) : null;
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

    private static double Score(byte[] bytes, double rowNorm, IReadOnlyList<float> query, double qNorm)
    {
        var dot = 0.0;
        for (var i = 0; i < query.Count; i++)
        {
            dot += BitConverter.ToSingle(bytes, i * sizeof(float)) * query[i];
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
        var bytes = new byte[v.Count * sizeof(float)];
        Span<byte> slot = stackalloc byte[sizeof(float)];
        for (var i = 0; i < v.Count; i++)
        {
            // 明確以小端寫入：舊實作依賴 BitConverter 的主機位元組序，
            // 但 ByRef／Search 一律以小端解讀，換台大端機器寫的資料就會全錯。
            BitConverter.TryWriteBytes(slot, v[i]);
            if (!BitConverter.IsLittleEndian)
            {
                slot.Reverse();
            }

            slot.CopyTo(bytes.AsSpan(i * sizeof(float)));
        }

        return bytes;
    }

    private sealed record EmbeddingRow(long RefId, string SourceType, string? Label, byte[] Vector, double Norm);
}