namespace HeliVMS.Storage;

/// <summary>POS 交易事件（M93，§14.7 #8）：金額以分（cents）儲存，金額比較不失真。</summary>
public sealed record POSEvent(
    long Id,
    int DeviceId,
    string RegisterId,
    string TransactionNo,
    long AmountCents,
    DateTime OccurredAtUtc);

/// <summary>POS 交易倉儲（M93，SQLite v31）：Insert／Query（device＋左閉右開區間＋limit）。</summary>
public sealed class POSEventRepository
{
    private readonly SqliteStore _store;

    public POSEventRepository(SqliteStore store) => _store = store;

    public long Insert(int deviceId, string registerId, string transactionNo, long amountCents, DateTime occurredAtUtc)
    {
        _store.Execute(
            """
            INSERT INTO pos_events (device_id, register_id, transaction_no, amount_cents, occurred_at_utc)
            VALUES ($d, $reg, $tx, $amt, $t);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$reg", registerId);
                cmd.Parameters.AddWithValue("$tx", transactionNo);
                cmd.Parameters.AddWithValue("$amt", amountCents);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(occurredAtUtc));
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<POSEvent> Query(int? deviceId = null, DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 0)
    {
        var where = new List<string>();
        if (deviceId is not null) where.Add("device_id = $d");
        if (fromUtc is not null) where.Add("occurred_at_utc >= $from");
        if (toUtc is not null) where.Add("occurred_at_utc < $to");

        var sql = "SELECT id, device_id, register_id, transaction_no, amount_cents, occurred_at_utc FROM pos_events";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " ORDER BY occurred_at_utc DESC, id DESC";
        if (limit > 0)
        {
            sql += " LIMIT $lim";
        }

        return _store.Query(sql, reader =>
        {
            var rows = new List<POSEvent>();
            while (reader.Read())
            {
                rows.Add(new POSEvent(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    SqliteStore.FromIso(reader.GetString(5))));
            }

            return rows;
        }, cmd =>
        {
            if (deviceId is { } d) cmd.Parameters.AddWithValue("$d", d);
            if (fromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (toUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
            if (limit > 0) cmd.Parameters.AddWithValue("$lim", limit);
        });
    }
}

/// <summary>POS 交易與事件之時間窗配對（M93）：同設備事件集按 |Δt|≤window 配對、依 |Δt| 排序。</summary>
public static class POSEventMatcher
{
    public static IReadOnlyList<(T Item, TimeSpan Delta)> Match<T>(
        DateTime posTime,
        IEnumerable<T> candidates,
        Func<T, DateTime> timestampOf,
        TimeSpan window)
    {
        return candidates
            .Select(c => (Item: c, Delta: timestampOf(c) - posTime))
            .Where(x => x.Delta.Duration() <= window)
            .OrderBy(x => x.Delta.Duration())
            .ToList();
    }
}