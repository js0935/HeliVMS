using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影排程索引（M10 §錄影排程）：recording_schedule 表之查詢與維護。
/// 星期以位元遮罩（bit0＝週日…bit6＝週六）儲存，時段為本地分鐘（0..1439）。
/// </summary>
public sealed class RecordingScheduleRepository
{
    private readonly SqliteStore _store;

    public RecordingScheduleRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增（Id＝0）或更新既有排程（Id＞0）。</summary>
    public RecordingScheduleRecord Upsert(RecordingScheduleRecord schedule)
    {
        if (schedule.Id > 0)
        {
            _store.Execute(
                """
                UPDATE recording_schedule
                SET channel_id = $c, days_mask = $m, start_min = $s, end_min = $e, enabled = $n
                WHERE id = $id;
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$c", schedule.ChannelId);
                    cmd.Parameters.AddWithValue("$m", schedule.DaysMask);
                    cmd.Parameters.AddWithValue("$s", schedule.StartMinute);
                    cmd.Parameters.AddWithValue("$e", schedule.EndMinute);
                    cmd.Parameters.AddWithValue("$n", schedule.Enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("$id", schedule.Id);
                });
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

        return new RecordingScheduleRecord
        {
            Id = id,
            ChannelId = schedule.ChannelId,
            DaysMask = schedule.DaysMask,
            StartMinute = schedule.StartMinute,
            EndMinute = schedule.EndMinute,
            Enabled = schedule.Enabled,
        };
    }

    /// <summary>刪除排程。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM recording_schedule WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
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
    public void DeleteByChannel(int channelId)
    {
        _store.Execute(
            "DELETE FROM recording_schedule WHERE channel_id = $c;",
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

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