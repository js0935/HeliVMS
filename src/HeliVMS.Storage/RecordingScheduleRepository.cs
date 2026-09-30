using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影排程索引（M10 §錄影排程）：recording_schedule 表之查詢與維護。
/// 星期以位元遮罩（bit0＝週日…bit6＝週六）儲存，時段為本地分鐘（0..1439）。
/// M159：新增/更新/刪除寫稽核。
/// </summary>
public sealed class RecordingScheduleRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public RecordingScheduleRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    /// <summary>新增（Id＝0）或更新既有排程（Id＞0）。</summary>
    public RecordingScheduleRecord Upsert(RecordingScheduleRecord schedule, string actor = "system")
    {
        if (schedule.Id > 0)
        {
            var affected = _store.Query<int>(
                """
                UPDATE recording_schedule
                SET channel_id = $c, days_mask = $m, start_min = $s, end_min = $e, enabled = $n
                WHERE id = $id;
                SELECT changes();
                """,
                static r => r.Read() ? r.GetInt32(0) : 0,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$c", schedule.ChannelId);
                    cmd.Parameters.AddWithValue("$m", schedule.DaysMask);
                    cmd.Parameters.AddWithValue("$s", schedule.StartMinute);
                    cmd.Parameters.AddWithValue("$e", schedule.EndMinute);
                    cmd.Parameters.AddWithValue("$n", schedule.Enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("$id", schedule.Id);
                });
            if (affected > 0)
            {
                _audit.Record(actor, "schedule.update", AuditCategories.Config, targetType: "recording_schedule",
                    targetId: schedule.Id, detail: ScheduleDetail(schedule));
            }

            return schedule;
        }

        var id = _store.Query(
            """
            INSERT INTO recording_schedule (channel_id, days_mask, start_min, end_min, enabled)
            VALUES ($c, $m, $s, $e, $n);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", schedule.ChannelId);
                cmd.Parameters.AddWithValue("$m", schedule.DaysMask);
                cmd.Parameters.AddWithValue("$s", schedule.StartMinute);
                cmd.Parameters.AddWithValue("$e", schedule.EndMinute);
                cmd.Parameters.AddWithValue("$n", schedule.Enabled ? 1 : 0);
            });

        var saved = new RecordingScheduleRecord
        {
            Id = id,
            ChannelId = schedule.ChannelId,
            DaysMask = schedule.DaysMask,
            StartMinute = schedule.StartMinute,
            EndMinute = schedule.EndMinute,
            Enabled = schedule.Enabled,
        };
        _audit.Record(actor, "schedule.add", AuditCategories.Config, targetType: "recording_schedule",
            targetId: id, detail: ScheduleDetail(saved));
        return saved;
    }

    /// <summary>刪除排程。</summary>
    public void Delete(long id, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            DELETE FROM recording_schedule WHERE id = $id;
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$id", id));
        if (affected > 0)
        {
            _audit.Record(actor, "schedule.delete", AuditCategories.Config, targetType: "recording_schedule", targetId: id);
        }
    }

    /// <summary>列出全部排程（依頻道與開始分鐘排序）。</summary>
    public IReadOnlyList<RecordingScheduleRecord> List()
    {
        return _store.Query(
            """
            SELECT id, channel_id, days_mask, start_min, end_min, enabled
            FROM recording_schedule
            ORDER BY channel_id, start_min;
            """,
            ReadRecords);
    }

    /// <summary>列出指定頻道之排程。</summary>
    public IReadOnlyList<RecordingScheduleRecord> ListByChannel(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, days_mask, start_min, end_min, enabled
            FROM recording_schedule
            WHERE channel_id = $c
            ORDER BY start_min;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>移除某頻道的全部排程（頻道刪除時）。</summary>
    public void DeleteByChannel(int channelId, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            DELETE FROM recording_schedule WHERE channel_id = $c;
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
        if (affected > 0)
        {
            _audit.Record(actor, "schedule.delete_by_channel", AuditCategories.Config, targetType: "recording_schedule",
                detail: $"channel={channelId} deleted={affected}");
        }
    }

    private static string ScheduleDetail(RecordingScheduleRecord schedule)
        => $"channel={schedule.ChannelId} mask={schedule.DaysMask} start={schedule.StartMinute} end={schedule.EndMinute} enabled={schedule.Enabled}";

    private static IReadOnlyList<RecordingScheduleRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<RecordingScheduleRecord>();
        while (reader.Read())
        {
            list.Add(new RecordingScheduleRecord
            {
                Id = reader.GetInt64(0),
                ChannelId = reader.GetInt32(1),
                DaysMask = reader.GetInt32(2),
                StartMinute = reader.GetInt32(3),
                EndMinute = reader.GetInt32(4),
                Enabled = reader.GetInt32(5) != 0,
            });
        }

        return list;
    }
}