using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>一筆網路 IO 模組（DI/DO 乾接點；§16.2）。protocol 目前僅 modbus_tcp。</summary>
public sealed record IoDevice(
    int Id,
    string Name,
    string Protocol,
    string Host,
    int Port,
    int UnitId,
    bool Enabled,
    int PollMs);

/// <summary>一條 IO 通道：DI（數位輸入）或 DO（數位輸出）。camera_id 為 DI 綁定鏡頭（可 null）。</summary>
public sealed record IoChannel(
    int Id,
    int DeviceId,
    string Direction,
    int IoIndex,
    string Name,
    bool Enabled,
    int DebounceMs,
    bool Polarity,
    int? CameraId,
    string AlarmPriority);

/// <summary>
/// 警報 IO（§16.2）存取：網路 IO 模組與 DI/DO 通道的 CRUD。
/// </summary>
public sealed class IoRepository
{
    private readonly SqliteStore _store;

    public IoRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增模組，回傳新 ID。</summary>
    public int AddDevice(string name, string host, int port = 502, int unitId = 1, int pollMs = 500)
    {
        return _store.Query(
            """
            INSERT INTO io_devices (name, protocol, host, port, unit_id, enabled, poll_ms)
            VALUES ($n, 'modbus_tcp', $h, $p, $u, 1, $pm);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$h", host);
                cmd.Parameters.AddWithValue("$p", port);
                cmd.Parameters.AddWithValue("$u", unitId);
                cmd.Parameters.AddWithValue("$pm", pollMs);
            });
    }

    /// <summary>列出全部模組（按 id）。</summary>
    public IReadOnlyList<IoDevice> ListDevices()
    {
        return _store.Query(
            """
            SELECT id, name, protocol, host, port, unit_id, enabled, poll_ms
            FROM io_devices ORDER BY id;
            """,
            static r =>
            {
                var list = new List<IoDevice>();
                while (r.Read())
                {
                    list.Add(ReadDevice(r));
                }

                return list;
            });
    }

    /// <summary>取得單一模組。</summary>
    public IoDevice? GetDevice(int id)
    {
        return _store.Query(
            """
            SELECT id, name, protocol, host, port, unit_id, enabled, poll_ms
            FROM io_devices WHERE id = $id;
            """,
            static r => r.Read() ? ReadDevice(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>啟用/停用模組（停用＝停止輪詢）。</summary>
    public void SetDeviceEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE io_devices SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除模組（CASCADE 移除其通道）。</summary>
    public void DeleteDevice(int id)
    {
        _store.Execute(
            "DELETE FROM io_devices WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>新增通道，回傳新 ID。</summary>
    public int AddChannel(
        int deviceId,
        string direction,
        int ioIndex,
        string name,
        int debounceMs = 200,
        bool polarity = false,
        int? cameraId = null,
        string alarmPriority = "normal")
    {
        return _store.Query(
            """
            INSERT INTO io_channels (device_id, direction, io_index, name, enabled, debounce_ms, polarity, camera_id, alarm_priority)
            VALUES ($d, $dir, $i, $n, 1, $db, $pol, $cam, $prio);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$dir", direction);
                cmd.Parameters.AddWithValue("$i", ioIndex);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$db", debounceMs);
                cmd.Parameters.AddWithValue("$pol", polarity ? 1 : 0);
                cmd.Parameters.AddWithValue("$cam", (object?)cameraId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$prio", alarmPriority);
            });
    }

    /// <summary>列出通道（依模組、方向、啟用過濾可選；未指定則全部）。</summary>
    public IReadOnlyList<IoChannel> ListChannels(int? deviceId = null, string? direction = null, bool? enabledOnly = null)
    {
        var where = new List<string>();
        var bind = new Action<SqliteCommand>(_ => { });
        if (deviceId is int dv)
        {
            where.Add("device_id = $d");
            bind += cmd => cmd.Parameters.AddWithValue("$d", dv);
        }

        if (direction is not null)
        {
            where.Add("direction = $dir");
            bind += cmd => cmd.Parameters.AddWithValue("$dir", direction);
        }

        if (enabledOnly is bool eo)
        {
            where.Add("enabled = $e");
            bind += cmd => cmd.Parameters.AddWithValue("$e", eo ? 1 : 0);
        }

        var sql = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        return _store.Query(
            $"""
            SELECT id, device_id, direction, io_index, name, enabled, debounce_ms, polarity, camera_id, alarm_priority
            FROM io_channels{sql}
            ORDER BY device_id, io_index;
            """,
            static r =>
            {
                var list = new List<IoChannel>();
                while (r.Read())
                {
                    list.Add(ReadChannel(r));
                }

                return list;
            },
            bind);
    }

    /// <summary>取得單一通道。</summary>
    public IoChannel? GetChannel(int id)
    {
        return _store.Query(
            """
            SELECT id, device_id, direction, io_index, name, enabled, debounce_ms, polarity, camera_id, alarm_priority
            FROM io_channels WHERE id = $id;
            """,
            static r => r.Read() ? ReadChannel(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>啟用/停用通道。</summary>
    public void SetChannelEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE io_channels SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除通道。</summary>
    public void DeleteChannel(int id)
    {
        _store.Execute(
            "DELETE FROM io_channels WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    private static IoDevice ReadDevice(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetInt32(6) != 0,
            r.GetInt32(7));

    private static IoChannel ReadChannel(SqliteDataReader r)
    {
        var cam = r.IsDBNull(8) ? (int?)null : r.GetInt32(8);
        return new IoChannel(
            r.GetInt32(0),
            r.GetInt32(1),
            r.GetString(2),
            r.GetInt32(3),
            r.GetString(4),
            r.GetInt32(5) != 0,
            r.GetInt32(6),
            r.GetInt32(7) != 0,
            cam,
            r.GetString(9));
    }
}