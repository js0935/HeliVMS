using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// AI 物件偵測 metadata 存取（§4 detections 表；全量記錄可條件查詢與統計）。
/// </summary>
public sealed class DetectionRepository
{
    private readonly SqliteStore _store;

    public DetectionRepository(SqliteStore store)
    {
        _store = store;
    }

    public sealed class QueryArgs
    {
        public int? ChannelId { get; init; }

        public string? Class { get; init; }

        public DateTime FromUtc { get; init; }

        public DateTime ToUtc { get; init; }

        public float? MinConfidence { get; init; }

        public int Limit { get; init; } = 500;
    }

    /// <summary>批次寫入多筆偵測（單一交易 INSERT，高效處理高頻全量記錄）。</summary>
    public void AddBatch(IReadOnlyList<DetectionRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var values = new List<string>(records.Count);
        var binds = new List<(string Name, object? Value)>(records.Count * 8);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            values.Add($"($c{i}, $cls{i}, $conf{i}, $x{i}, $y{i}, $w{i}, $h{i}, $t{i})");
            binds.Add(($"c{i}", r.ChannelId));
            binds.Add(($"cls{i}", r.Class));
            binds.Add(($"conf{i}", r.Confidence));
            binds.Add(($"x{i}", r.X));
            binds.Add(($"y{i}", r.Y));
            binds.Add(($"w{i}", r.W));
            binds.Add(($"h{i}", r.H));
            binds.Add(($"t{i}", SqliteStore.Iso(r.DetectedUtc)));
        }

        _store.Execute(
            $"INSERT INTO detections (channel_id, class, confidence, x, y, w, h, detected_at) VALUES {string.Join(", ", values)};",
            cmd =>
            {
                foreach (var (name, value) in binds)
                {
                    cmd.Parameters.AddWithValue($"${name}", value ?? DBNull.Value);
                }
            });
    }

    /// <summary>依條件查詢偵測（時間範圍必填；其餘可空），由新到舊、限量。</summary>
    public IReadOnlyList<DetectionRecord> ListByQuery(QueryArgs q)
    {
        var where = new List<string> { "detected_at >= $from", "detected_at <= $to" };
        if (q.ChannelId is int c)
        {
            where.Add("channel_id = $c");
        }

        if (!string.IsNullOrEmpty(q.Class))
        {
            where.Add("class = $cls");
        }

        if (q.MinConfidence is float min)
        {
            where.Add("confidence >= $min");
        }

        var sql = string.Join(" AND ", where);
        return _store.Query(
            $"""
            SELECT id, channel_id, class, confidence, x, y, w, h, detected_at
            FROM detections
            WHERE {sql}
            ORDER BY detected_at DESC
            LIMIT {Math.Max(1, q.Limit)};
            """,
            static r =>
            {
                var list = new List<DetectionRecord>();
                while (r.Read())
                {
                    list.Add(new DetectionRecord
                    {
                        Id = r.GetInt64(0),
                        ChannelId = r.GetInt32(1),
                        Class = r.GetString(2),
                        Confidence = r.GetFloat(3),
                        X = r.GetFloat(4),
                        Y = r.GetFloat(5),
                        W = r.GetFloat(6),
                        H = r.GetFloat(7),
                        DetectedUtc = SqliteStore.FromIso(r.GetString(8)),
                    });
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(q.FromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(q.ToUtc));
                if (q.ChannelId is int c2)
                {
                    cmd.Parameters.AddWithValue("$c", c2);
                }

                if (!string.IsNullOrEmpty(q.Class))
                {
                    cmd.Parameters.AddWithValue("$cls", q.Class);
                }

                if (q.MinConfidence is float min2)
                {
                    cmd.Parameters.AddWithValue("$min", min2);
                }
            });
    }

    public sealed record ClassCount(string Class, int Count);

    /// <summary>時間範圍內各類別出現次數（由多至少），供統計面板。</summary>
    public IReadOnlyList<ClassCount> CountByClass(QueryArgs q)
    {
        var where = new List<string> { "detected_at >= $from", "detected_at <= $to" };
        if (q.ChannelId is int c)
        {
            where.Add("channel_id = $c");
        }

        var sql = string.Join(" AND ", where);
        return _store.Query(
            $"""
            SELECT class, COUNT(1) AS cnt
            FROM detections
            WHERE {sql}
            GROUP BY class
            ORDER BY cnt DESC, class ASC;
            """,
            static r =>
            {
                var list = new List<ClassCount>();
                while (r.Read())
                {
                    list.Add(new ClassCount(r.GetString(0), r.GetInt32(1)));
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(q.FromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(q.ToUtc));
                if (q.ChannelId is int c2)
                {
                    cmd.Parameters.AddWithValue("$c", c2);
                }
            });
    }

    /// <summary>刪除指定時間範圍前的舊偵測（FIFO 保留策略，回傳刪除筆數）。</summary>
    public int DeleteBefore(DateTime beforeUtc)
    {
        return _store.Query(
            """
            DELETE FROM detections WHERE detected_at < $before;
            SELECT changes();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            },
            cmd => cmd.Parameters.AddWithValue("$before", SqliteStore.Iso(beforeUtc)));
    }
}