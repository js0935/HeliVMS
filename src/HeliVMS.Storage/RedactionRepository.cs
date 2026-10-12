using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>遮蔽來源種類（M101，§14.7 #5）：影片檔某時間點或獨立快照。</summary>
public static class RedactionSources
{
    public const string Clip = "clip";
    public const string Snapshot = "snapshot";
}

/// <summary>錄影遮蔽區域（M101，§14.7 #5）：以對應影像的像素座標存放；filled＝實心遮罩，否則盒狀模糊。</summary>
public sealed record RedactionRegion(
    long Id,
    string SourceType,
    long RefId,
    int ChannelId,
    DateTime OccurredAtUtc,
    int X,
    int Y,
    int Width,
    int Height,
    bool Filled,
    DateTime CreatedAtUtc);

/// <summary>錄影遮蔽區域倉儲（M101，SQLite v37）：Add／QueryBySource／QueryByTime／Remove。個資法遮蔽標記（§14.7 #5）。</summary>
public sealed class RedactionRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    /// <summary>預設自建 <see cref="AuditLogRepository"/>（同 <c>ChannelRepository</c>）：遮蔽區域登錄／移除寫入稽核
    /// （<see cref="AuditCategories.Evidence"/>，個資法隱私操作）。</summary>
    public RedactionRepository(SqliteStore store, AuditLogRepository? audit = null)
    {
        _store = store;
        _audit = audit ?? new AuditLogRepository(store);
    }

    public long Add(RedactionRegion region, string? actor = null)
        => Add(region.SourceType, region.RefId, region.ChannelId, region.OccurredAtUtc,
            region.X, region.Y, region.Width, region.Height, region.Filled, region.CreatedAtUtc, actor);

    public long Add(
        string sourceType,
        long refId,
        int channelId,
        DateTime occurredAtUtc,
        int x,
        int y,
        int width,
        int height,
        bool filled,
        DateTime createdAtUtc,
        string? actor = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "遮蔽區域寬高必須為正。");
        }

        _store.Execute(
            """
            INSERT INTO redaction_regions
                (source_type, ref_id, channel_id, occurred_at_utc, x, y, width, height, filled, created_at_utc)
            VALUES ($st, $ref, $ch, $t, $x, $y, $w, $h, $f, $created);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", sourceType);
                cmd.Parameters.AddWithValue("$ref", refId);
                cmd.Parameters.AddWithValue("$ch", channelId);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(occurredAtUtc));
                cmd.Parameters.AddWithValue("$x", x);
                cmd.Parameters.AddWithValue("$y", y);
                cmd.Parameters.AddWithValue("$w", width);
                cmd.Parameters.AddWithValue("$h", height);
                cmd.Parameters.AddWithValue("$f", filled ? 1 : 0);
                cmd.Parameters.AddWithValue("$created", SqliteStore.Iso(createdAtUtc));
            });
        var id = _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
        _audit.Record(actor, "redaction.add", AuditCategories.Evidence, "redaction", id,
            $"channel={channelId} source={sourceType}");
        return id;
    }

    /// <summary>列出某來源（影片/快照）的全部遮蔽區域（M101 輸出側讀取）。</summary>
    public IReadOnlyList<RedactionRegion> QueryBySource(string sourceType, long refId)
        => QueryCore(
            "WHERE source_type = $st AND ref_id = $ref ORDER BY occurred_at_utc, id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", sourceType);
                cmd.Parameters.AddWithValue("$ref", refId);
            });

    /// <summary>依頻道＋時間區間（左閉右開）列出遮蔽區域。</summary>
    public IReadOnlyList<RedactionRegion> QueryByTime(int channelId, DateTime fromUtc, DateTime toUtc)
        => QueryCore(
            @"WHERE channel_id = $ch AND occurred_at_utc >= $from AND occurred_at_utc < $to
              ORDER BY occurred_at_utc, id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ch", channelId);
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });

    /// <summary>跨頻道時間區間查詢（REST 缺省 channel 時用）。</summary>
    public IReadOnlyList<RedactionRegion> QueryByTimeGlobal(DateTime fromUtc, DateTime toUtc)
        => QueryCore(
            @"WHERE occurred_at_utc >= $from AND occurred_at_utc < $to
              ORDER BY occurred_at_utc, id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });

    /// <summary>移除一筆遮蔽區域（區位轉移／解除後）。回 true＝實際刪除。</summary>
    public bool Remove(long id, string? actor = null)
    {
        _store.Execute("DELETE FROM redaction_regions WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
        var removed = _store.Query("SELECT changes();", r => r.Read() && r.GetInt32(0) > 0);
        if (removed)
        {
            _audit.Record(actor, "redaction.remove", AuditCategories.Evidence, "redaction", id);
        }

        return removed;
    }

    private IReadOnlyList<RedactionRegion> QueryCore(string tail, Action<SqliteCommand> bind)
    {
        return _store.Query(
            $"SELECT id, source_type, ref_id, channel_id, occurred_at_utc, x, y, width, height, filled, created_at_utc " +
            $"FROM redaction_regions {tail}",
            reader =>
            {
                var rows = new List<RedactionRegion>();
                while (reader.Read())
                {
                    rows.Add(new RedactionRegion(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        SqliteStore.FromIso(reader.GetString(4)),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9) != 0,
                        SqliteStore.FromIso(reader.GetString(10))));
                }

                return rows;
            },
            bind);
    }
}