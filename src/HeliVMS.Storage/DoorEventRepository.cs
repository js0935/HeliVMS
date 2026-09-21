namespace HeliVMS.Storage;

/// <summary>門禁刷卡事件（M92，§14.7 #8）：direction 為 In（進）／Out（出）；granted 是否放行。</summary>
public sealed record DoorEvent(
    long Id,
    int DeviceId,
    int DoorId,
    string CardId,
    string Direction,
    bool Granted,
    string Reason,
    DateTime OccurredAtUtc);

/// <summary>門禁事件查詢條件（M92）：全 null＝不過濾；occurred 區間左閉右開；limit≤0＝無上限。</summary>
public sealed record DoorEventQuery(
    int? DeviceId = null,
    int? DoorId = null,
    string? CardId = null,
    bool? Granted = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 0);

/// <summary>門禁事件倉儲（M92，§14.7 #8，SqliteStore v30）：Insert／Query／CountByCard。</summary>
public sealed class DoorEventRepository
{
    private readonly SqliteStore _store;

    public DoorEventRepository(SqliteStore store) => _store = store;

    public long Insert(int deviceId, int doorId, string cardId, string direction, bool granted, string reason, DateTime occurredAtUtc)
    {
        _store.Execute(
            """
            INSERT INTO door_events (device_id, door_id, card_id, direction, granted, reason, occurred_at_utc)
            VALUES ($d, $door, $card, $dir, $g, $r, $t);
            SELECT last_insert_rowid();
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$door", doorId);
                cmd.Parameters.AddWithValue("$card", cardId);
                cmd.Parameters.AddWithValue("$dir", direction);
                cmd.Parameters.AddWithValue("$g", granted ? 1 : 0);
                cmd.Parameters.AddWithValue("$r", reason);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(occurredAtUtc));
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<DoorEvent> Query(DoorEventQuery q)
    {
        var where = new List<string>();
        if (q.DeviceId is not null) where.Add("device_id = $dev");
        if (q.DoorId is not null) where.Add("door_id = $door");
        if (q.CardId is not null) where.Add("card_id = $card");
        if (q.Granted is { } g) where.Add($"granted = {(g ? 1 : 0)}");
        if (q.FromUtc is not null) where.Add("occurred_at_utc >= $from");
        if (q.ToUtc is not null) where.Add("occurred_at_utc < $to");

        var sql = "SELECT id, device_id, door_id, card_id, direction, granted, reason, occurred_at_utc FROM door_events";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }

        sql += " ORDER BY occurred_at_utc DESC, id DESC";
        if (q.Limit > 0)
        {
            sql += " LIMIT $lim";
        }

        return _store.Query(sql, reader =>
        {
            var rows = new List<DoorEvent>();
            while (reader.Read())
            {
                rows.Add(new DoorEvent(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    reader.GetString(6),
                    SqliteStore.FromIso(reader.GetString(7))));
            }

            return rows;
        }, cmd =>
        {
            if (q.DeviceId is { } dev) cmd.Parameters.AddWithValue("$dev", dev);
            if (q.DoorId is { } door) cmd.Parameters.AddWithValue("$door", door);
            if (q.CardId is { } card) cmd.Parameters.AddWithValue("$card", card);
            if (q.FromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (q.ToUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
            if (q.Limit > 0) cmd.Parameters.AddWithValue("$lim", q.Limit);
        });
    }

    public long CountByCard(string cardId, DateTime? fromUtc, DateTime? toUtc)
    {
        var where = "card_id = $card";
        if (fromUtc is not null) where += " AND occurred_at_utc >= $from";
        if (toUtc is not null) where += " AND occurred_at_utc < $to";
        var sql = $"SELECT count(*) FROM door_events WHERE {where}";
        return _store.Query(sql, r => r.Read() ? r.GetInt64(0) : 0, cmd =>
        {
            cmd.Parameters.AddWithValue("$card", cardId);
            if (fromUtc is { } f) cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(f));
            if (toUtc is { } t) cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(t));
        });
    }
}