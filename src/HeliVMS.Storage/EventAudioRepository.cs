using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Storage;

/// <summary>
/// 事件錄音資料面（M157，§14.7「事件錄音/語音廣播」之資料側）：儲存單一事件之錄音媒體
/// （bytes＋mime＋duration＋sha256），供後續檢視/法證取回與保留清理。實際音訊擷取/串流
/// 屬硬體縫（待真機，不偽造）；本 Repository 存取純 BCL 可單測。
/// </summary>
public sealed class EventAudioRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public EventAudioRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    /// <summary>儲存錄音（覆寫同事件）；回傳唯一幂等（依 sha256+長度計算，同事件同內容回 OK）。</summary>
    public bool Save(long eventId, long channelId, DateTime startedAtUtc, int durationMs, string mime, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(eventId, 0);
        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException("錄音內容不可為空", nameof(bytes));
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        _store.Execute(
            """
            INSERT INTO event_audio (event_id, channel_id, started_at_utc, duration_ms, mime, bytes, sha256)
            VALUES ($e, $c, $s, $d, $m, $b, $h)
            ON CONFLICT(event_id) DO UPDATE SET
                channel_id = excluded.channel_id,
                started_at_utc = excluded.started_at_utc,
                duration_ms = excluded.duration_ms,
                mime = excluded.mime,
                bytes = excluded.bytes,
                sha256 = excluded.sha256;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", eventId);
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", SqliteStore.Iso(startedAtUtc));
                cmd.Parameters.AddWithValue("$d", durationMs);
                cmd.Parameters.AddWithValue("$m", mime);
                cmd.Parameters.AddWithValue("$b", bytes);
                cmd.Parameters.AddWithValue("$h", sha);
            });
        _audit.Record("system", "audio.save", AuditCategories.Evidence,
            targetType: "event_audio", targetId: eventId,
            detail: $"channel={channelId} duration_ms={durationMs} mime={mime}");
        return true;
    }

    /// <summary>依事件取回錄音；無則 null。</summary>
    public EventAudioRecord? GetByEvent(long eventId)
        => _store.Query(
            """
            SELECT event_id, channel_id, started_at_utc, duration_ms, mime, bytes, sha256
            FROM event_audio WHERE event_id = $e;
            """,
            r => r.Read() ? Map(r) : null,
            cmd => cmd.Parameters.AddWithValue("$e", eventId));

    /// <summary>區間內取回錄音清單（時間升序，至多 limit）。</summary>
    public IReadOnlyList<EventAudioRecord> QueryByTime(DateTime? fromUtc, DateTime? toUtc, int limit = 50)
    {
        var sql = new System.Text.StringBuilder(
            "SELECT event_id, channel_id, started_at_utc, duration_ms, mime, bytes, sha256 FROM event_audio WHERE 1 = 1");
        if (fromUtc is not null)
        {
            sql.Append(" AND started_at_utc >= $from");
        }
        if (toUtc is not null)
        {
            sql.Append(" AND started_at_utc < $to");
        }
        sql.Append(" ORDER BY started_at_utc, event_id LIMIT $limit;");

        return _store.Query(
            sql.ToString(),
            r =>
            {
                var rows = new List<EventAudioRecord>();
                while (r.Read())
                {
                    rows.Add(Map(r));
                }
                return rows;
            },
            cmd =>
            {
                if (fromUtc is not null)
                {
                    cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc.Value));
                }
                if (toUtc is not null)
                {
                    cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc.Value));
                }
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            });
    }

    /// <summary>刪除早於 cutoff 之錄音；回傳刪除筆數。</summary>
    public int PruneOlderThan(DateTime cutoffUtc)
    {
        _store.Execute(
            "DELETE FROM event_audio WHERE started_at_utc < $cutoff;",
            cmd => cmd.Parameters.AddWithValue("$cutoff", SqliteStore.Iso(cutoffUtc)));
        return _store.Query<int>("SELECT changes();", static r => r.Read() ? r.GetInt32(0) : 0);
    }

    private static EventAudioRecord Map(System.Data.Common.DbDataReader r)
        => new(
            r.GetInt64(0),
            r.GetInt64(1),
            SqliteStore.FromIso(r.GetString(2)),
            r.GetInt32(3),
            r.GetString(4),
            (byte[])r[5],
            r.GetString(6));
}

/// <summary>事件錄音紀錄（M157）：media bytes＋識別/時間/長度/mime/sha256。</summary>
public sealed record EventAudioRecord(
    long EventId,
    long ChannelId,
    DateTime StartedAtUtc,
    int DurationMs,
    string Mime,
    byte[] Bytes,
    string Sha256);