namespace HeliVMS.Storage;

/// <summary>巡航步驟（M72，§47）：名稱＝ONVIF 預設點名稱；dwell＝停留秒數（欽 PatrolController 等待）。</summary>
public sealed record PatrolStepRow(string PresetName, int DwellSeconds);

/// <summary>一筆巡航排程（M72，§47）：每通道單一套；聯欄 window 為每日巡航時窗（HH:mm），範本排程 M69 用。</summary>
public sealed record StoredPatrol(
    long? Id,
    string Name,
    int ChannelId,
    bool Enabled,
    string WindowStart,
    string WindowEnd,
    DateTime UpdatedAtUtc,
    IReadOnlyList<PatrolStepRow> Steps);

/// <summary>
/// 巡航排程存取（M72，§47）。時間戳一律 ISO8601 UTC；整存整覆：儲存時重建步驟序（seq 0..n-1），
/// 同頻道第二次儲存即覆寫該套（取原 Id），不致多套巡航。UI（PatrolWindow）與引擎共用。
/// </summary>
public sealed class PatrolRepository
{
    private readonly SqliteStore _store;

    public PatrolRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 儲存一套巡航：<paramref name="id"/> 為 null 則新增，否則覆寫該筆與其步驟；回傳實際 Id。
    /// 條件：名稱不可空白、window 前提（start/end 可相等＝全天候通訊處理）、通道須為正。
    /// </summary>
    public long Save(
        long? id,
        string name,
        int channelId,
        bool enabled,
        string windowStart,
        string windowEnd,
        DateTime updatedAtUtc,
        IReadOnlyList<PatrolStepRow> steps)
    {
        if (id is not null && id.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "巡航 Id 須為正整數。");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("巡航名稱不可為空。", nameof(name));
        }

        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "頻道須為正整數。");
        }

        string start = string.IsNullOrWhiteSpace(windowStart) ? "00:00" : windowStart.Trim();
        string end = string.IsNullOrWhiteSpace(windowEnd) ? "23:59" : windowEnd.Trim();
        var normalizedSteps = steps?.ToList() ?? new List<PatrolStepRow>();
        if (normalizedSteps.Any(s => string.IsNullOrWhiteSpace(s.PresetName)))
        {
            throw new ArgumentException("預設點名稱不可為空。", nameof(steps));
        }

        if (normalizedSteps.Any(s => s.DwellSeconds < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "停留秒數不可為負。");
        }

        long actualId;
        if (id is null)
        {
            actualId = GetByChannel(channelId)?.Id ?? _store.Query(
                """
                INSERT INTO patrols (name, channel_id, enabled, window_start, window_end, created_at, updated_at)
                VALUES ($n, $c, $e, $s, $x, $t, $t);
                SELECT last_insert_rowid();
                """,
                static r =>
                {
                    r.Read();
                    return r.GetInt64(0);
                },
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$n", name);
                    cmd.Parameters.AddWithValue("$c", channelId);
                    cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("$s", start);
                    cmd.Parameters.AddWithValue("$x", end);
                    cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(updatedAtUtc));
                });
        }
        else
        {
            actualId = id.Value;
        }

        _store.Execute(
            """
            UPDATE patrols
            SET name = $n, channel_id = $c, enabled = $e, window_start = $s, window_end = $x, updated_at = $t
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$s", start);
                cmd.Parameters.AddWithValue("$x", end);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(updatedAtUtc));
                cmd.Parameters.AddWithValue("$id", actualId);
            });

        _store.Execute(
            "DELETE FROM patrol_steps WHERE patrol_id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", actualId));

        for (var i = 0; i < normalizedSteps.Count; i++)
        {
            var step = normalizedSteps[i];
            _store.Execute(
                """
                INSERT INTO patrol_steps (patrol_id, seq, preset_name, dwell_seconds)
                VALUES ($p, $s, $n, $d);
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$p", actualId);
                    cmd.Parameters.AddWithValue("$s", i);
                    cmd.Parameters.AddWithValue("$n", step.PresetName);
                    cmd.Parameters.AddWithValue("$d", step.DwellSeconds);
                });
        }

        return actualId;
    }

    /// <summary>全部巡航（含步驟），按通道升序。</summary>
    public IReadOnlyList<StoredPatrol> ListAll()
    {
        return _store.Query(
            """
            SELECT id, name, channel_id, enabled, window_start, window_end, updated_at
            FROM patrols
            ORDER BY channel_id;
            """,
            ReadPatrolsWithSteps);
    }

    /// <summary>指定通道之巡航；不存在→null。</summary>
    public StoredPatrol? GetByChannel(int channelId)
    {
        var list = _store.Query(
            """
            SELECT id, name, channel_id, enabled, window_start, window_end, updated_at
            FROM patrols
            WHERE channel_id = $c
            ORDER BY id LIMIT 1;
            """,
            ReadPatrolsWithSteps,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>刪除巡航與其步驟；回傳是否實際存在。</summary>
    public bool Delete(long id)
    {
        var exists = _store.Query(
            "SELECT EXISTS(SELECT 1 FROM patrols WHERE id = $id);",
            static r =>
            {
                r.Read();
                return r.GetInt32(0) == 1;
            },
            cmd => cmd.Parameters.AddWithValue("$id", id));
        if (!exists)
        {
            return false;
        }

        _store.Execute("DELETE FROM patrol_steps WHERE patrol_id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        _store.Execute("DELETE FROM patrols WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        return true;
    }

    private IReadOnlyList<StoredPatrol> ReadPatrolsWithSteps(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var list = new List<StoredPatrol>();
        while (reader.Read())
        {
            var steps = _store.Query(
                """
                SELECT preset_name, dwell_seconds
                FROM patrol_steps
                WHERE patrol_id = $p
                ORDER BY seq;
                """,
                static r =>
                {
                    var rows = new List<PatrolStepRow>();
                    while (r.Read())
                    {
                        rows.Add(new PatrolStepRow(r.GetString(0), r.GetInt32(1)));
                    }

                    return rows;
                },
                cmd => cmd.Parameters.AddWithValue("$p", reader.GetInt64(0)));

            list.Add(new StoredPatrol(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3) == 1,
                reader.GetString(4),
                reader.GetString(5),
                SqliteStore.FromIso(reader.GetString(6)),
                steps));
        }

        return list;
    }
}