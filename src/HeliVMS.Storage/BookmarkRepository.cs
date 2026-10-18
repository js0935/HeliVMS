namespace HeliVMS.Storage;

/// <summary>回放書籤（§8.5 §11.4）：操作員在某通道某絕對時刻標記的重點與筆記。</summary>
public sealed record BookmarkRecord(
    long Id,
    int ChannelId,
    DateTime AtUtc,
    string Note,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>
/// 回放書籤存取（SQLite v56）。時間戳一律 ISO8601 UTC，與其他倉儲一致。
/// </summary>
/// <remarks>
/// 不寫稽核：書籤是操作員自己的導覽標記，不是對受保護資料的狀態變更，
/// 也不是證據鏈的一環——若哪天書籤會影響匯出／保存，再補稽核。
/// </remarks>
public sealed class BookmarkRepository
{
    private readonly SqliteStore _store;

    public BookmarkRepository(SqliteStore store) => _store = store;

    /// <summary>新增一筆書籤，回傳 ID。筆記可空字串（允許先標點、稍後再補）。</summary>
    public long Add(int channelId, DateTime atUtc, string note, string createdBy, DateTime createdAtUtc)
    {
        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "頻道須為正整數。");
        }

        _store.Execute(
            """
            INSERT INTO bookmarks (channel_id, at_utc, note, created_by, created_at_utc)
            VALUES ($c, $t, $n, $b, $a);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(atUtc));
                cmd.Parameters.AddWithValue("$n", note ?? string.Empty);
                cmd.Parameters.AddWithValue("$b", createdBy ?? string.Empty);
                cmd.Parameters.AddWithValue("$a", SqliteStore.Iso(createdAtUtc));
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    /// <summary>改寫筆記；回傳是否實際命中（不存在→false）。</summary>
    public bool UpdateNote(long id, string note)
    {
        _store.Execute(
            "UPDATE bookmarks SET note = $n WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", note ?? string.Empty);
                cmd.Parameters.AddWithValue("$id", id);
            });
        return _store.Query("SELECT changes();", r => r.Read() && r.GetInt32(0) > 0);
    }

    /// <summary>刪除一筆書籤；回傳是否實際刪除。</summary>
    public bool Remove(long id)
    {
        _store.Execute(
            "DELETE FROM bookmarks WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
        return _store.Query("SELECT changes();", r => r.Read() && r.GetInt32(0) > 0);
    }

    /// <summary>某通道時間範圍（左閉右開）的書籤，按時刻升序。</summary>
    public IReadOnlyList<BookmarkRecord> ListByRange(int channelId, DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT id, channel_id, at_utc, note, created_by, created_at_utc
            FROM bookmarks
            WHERE channel_id = $c AND at_utc >= $from AND at_utc < $to
            ORDER BY at_utc, id;
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>全部書籤，按時刻降序（管理／清理用）。</summary>
    public IReadOnlyList<BookmarkRecord> ListAll()
    {
        return _store.Query(
            """
            SELECT id, channel_id, at_utc, note, created_by, created_at_utc
            FROM bookmarks
            ORDER BY at_utc DESC, id DESC;
            """,
            ReadRecords);
    }

    private static IReadOnlyList<BookmarkRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var list = new List<BookmarkRecord>();
        while (reader.Read())
        {
            list.Add(new BookmarkRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                SqliteStore.FromIso(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                SqliteStore.FromIso(reader.GetString(5))));
        }

        return list;
    }
}
