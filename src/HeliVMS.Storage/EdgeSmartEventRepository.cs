namespace HeliVMS.Storage;

/// <summary>消費邊緣 AI 定案事件（M95，§14.7 #13）：軌跡收尾（Disappear/timeout）時的穩定分類產物。</summary>
public sealed record EdgeSmartEvent(
    long Id,
    int DeviceId,
    string ClassName,
    string TrackId,
    string Direction,
    int X1,
    int Y1,
    int X2,
    int Y2,
    DateTime OccurredAtUtc);

/// <summary>查詢條件：device/class/direction 皆可選；occurred 左閉右開。</summary>
public sealed record EdgeSmartEventQuery(
    int? DeviceId = null,
    string? ClassName = null,
    string? Direction = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 0);

/// <summary>方向摘要（M95）：用於「消費分析」——各方向事件數。</summary>
public sealed record EdgeDirectionSummary(string Direction, long Count);

/// <summary>Edge AI metadata 倉儲（M95，SQLite v33）：Append／Query／AggregateByDirection。</summary>
public sealed class EdgeSmartEventRepository
{
    private readonly SqliteStore _store;

    public EdgeSmartEventRepository(SqliteStore store) => _store = store;

    public long Append(int deviceId, string className, string trackId, string direction, int x1, int y1, int x2, int y2, DateTime occurredAtUtc)
    {
        _store.Execute(
            """
            INSERT INTO edge_smart_events (device_id, class_name, track_id, direction, x1, y1, x2, y2, occurred_at_utc)
            VALUES ($d, $c, $t, $dir, $x1, $y1, $x2, $y2, $o);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$c", className);
                cmd.Parameters.AddWithValue("$t", trackId);
                cmd.Parameters.AddWithValue("$dir", direction);
                cmd.Parameters.AddWithValue("$x1", x1);
                cmd.Parameters.AddWithValue("$y1", y1);
                cmd.Parameters.AddWithValue("$x2", x2);
                cmd.Parameters.AddWithValue("$y2", y2);
                cmd.Parameters.AddWithValue("$o", SqliteStore.Iso(occurredAtUtc));
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<EdgeSmartEvent> Query(EdgeSmartEventQuery q)
    {
        var where = new List<string>();
        if (q.DeviceId is not null) where.Add("device_id = $d");
        if (q.ClassName is not null) where.Add("class_name = $c");
        if (q.Direction is not null) where.Add("direction = $dir");
        if (q.FromUtc is not null) where.Add("occurred_at_utc >= $from");
        if (q.ToUtc is not null) where.Add("occurred_at_utc < $to");

        var sql = "SELECT id, device_id, class_name, track_id, direction, x1, y1, x2, y2, occurred_at_utc FROM edge_smart_events";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " ORDER BY occurred_at_utc DESC, id DESC";
        if (q.Limit > 0)
        {
            sql += " LIMIT $lim";
        }

        return _store.Query(sql, ReadEvents, cmd =>
        {
            if (q.DeviceId is { } d) cmd.Parameters.AddWithValue("$d", d);
            if (q.ClassName is { } c) cmd.Parameters.AddWithValue("$c", c);
            if (q.Direction is { } dir) cmd.Parameters.AddWithValue("$dir", dir);
            if (q.FromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (q.ToUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
            if (q.Limit > 0) cmd.Parameters.AddWithValue("$lim", q.Limit);
        });
    }

    public IReadOnlyList<EdgeDirectionSummary> AggregateByDirection(int? deviceId, DateTime? fromUtc, DateTime? toUtc)
    {
        var where = new List<string>();
        if (deviceId is not null) where.Add("device_id = $d");
        if (fromUtc is not null) where.Add("occurred_at_utc >= $from");
        if (toUtc is not null) where.Add("occurred_at_utc < $to");

        var sql = "SELECT direction, count(*) FROM edge_smart_events";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " GROUP BY direction ORDER BY direction;";
        return _store.Query(sql, r =>
        {
            var rows = new List<EdgeDirectionSummary>();
            while (r.Read())
            {
                rows.Add(new EdgeDirectionSummary(r.GetString(0), r.GetInt64(1)));
            }

            return rows;
        }, cmd =>
        {
            if (deviceId is { } d) cmd.Parameters.AddWithValue("$d", d);
            if (fromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (toUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
        });
    }

    private static IReadOnlyList<EdgeSmartEvent> ReadEvents(System.Data.Common.DbDataReader r)
    {
        var rows = new List<EdgeSmartEvent>();
        while (r.Read())
        {
            rows.Add(new EdgeSmartEvent(
                r.GetInt64(0),
                r.GetInt32(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt32(5),
                r.GetInt32(6),
                r.GetInt32(7),
                r.GetInt32(8),
                SqliteStore.FromIso(r.GetString(9))));
        }

        return rows;
    }
}