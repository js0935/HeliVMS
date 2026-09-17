using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>一張電子地圖（平面圖基底；§16.1）。位置座標一律 0..1 比例。</summary>
public sealed record MapRecord(
    int Id,
    string Name,
    string Type,
    string ImagePath,
    int Width,
    int Height,
    bool Enabled,
    int SortOrder);

/// <summary>地圖上一個圖釘：camera→channels.id、io→io_channels.id。座標為 0..1 比例。</summary>
public sealed record MapDeviceRecord(
    int Id,
    int MapId,
    string DeviceType,
    int ChannelId,
    double X,
    double Y,
    double Angle,
    double FovDeg,
    double FovDepth,
    bool Enabled);

/// <summary>
/// 電子地圖（§16.1）存取：地圖 CRUD 與圖釘（map_devices）管理。
/// </summary>
public sealed class MapRepository
{
    private readonly SqliteStore _store;

    public MapRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增地圖，回傳新 ID。</summary>
    public int AddMap(string name, string imagePath, int width, int height, int sortOrder = 0)
    {
        return _store.Query(
            """
            INSERT INTO maps (name, type, image_path, width, height, enabled, sort_order)
            VALUES ($n, 'plan', $p, $w, $h, 1, $so);
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
                cmd.Parameters.AddWithValue("$p", imagePath);
                cmd.Parameters.AddWithValue("$w", width);
                cmd.Parameters.AddWithValue("$h", height);
                cmd.Parameters.AddWithValue("$so", sortOrder);
            });
    }

    /// <summary>列出全部地圖（按 sort_order, id）。</summary>
    public IReadOnlyList<MapRecord> ListMaps()
    {
        return _store.Query(
            """
            SELECT id, name, type, image_path, width, height, enabled, sort_order
            FROM maps ORDER BY sort_order, id;
            """,
            static r =>
            {
                var list = new List<MapRecord>();
                while (r.Read())
                {
                    list.Add(ReadMap(r));
                }

                return list;
            });
    }

    /// <summary>取得單一地圖。</summary>
    public MapRecord? GetMap(int id)
    {
        return _store.Query(
            """
            SELECT id, name, type, image_path, width, height, enabled, sort_order
            FROM maps WHERE id = $id;
            """,
            static r => r.Read() ? ReadMap(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>啟用/停用地圖（停用＝地圖檢視不顯示）。</summary>
    public void SetMapEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE maps SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除地圖（CASCADE 移除其圖釘）。</summary>
    public void DeleteMap(int id)
    {
        _store.Execute(
            "DELETE FROM maps WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>新增圖釘，回傳新 ID。</summary>
    public int AddDevice(
        int mapId,
        string deviceType,
        int channelId,
        double x = 0.5,
        double y = 0.5,
        double angle = 0,
        double fovDeg = 90,
        double fovDepth = 3)
    {
        return _store.Query(
            """
            INSERT INTO map_devices (map_id, device_type, channel_id, x, y, angle, fov_deg, fov_depth, enabled)
            VALUES ($m, $t, $c, $x, $y, $a, $fd, $fp, 1);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$m", mapId);
                cmd.Parameters.AddWithValue("$t", deviceType);
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$x", x);
                cmd.Parameters.AddWithValue("$y", y);
                cmd.Parameters.AddWithValue("$a", angle);
                cmd.Parameters.AddWithValue("$fd", fovDeg);
                cmd.Parameters.AddWithValue("$fp", fovDepth);
            });
    }

    /// <summary>列出某地圖的全部圖釘（依 device_type, channel_id）。</summary>
    public IReadOnlyList<MapDeviceRecord> ListDevices(int mapId)
    {
        return _store.Query(
            """
            SELECT id, map_id, device_type, channel_id, x, y, angle, fov_deg, fov_depth, enabled
            FROM map_devices WHERE map_id = $m
            ORDER BY device_type, channel_id;
            """,
            static r =>
            {
                var list = new List<MapDeviceRecord>();
                while (r.Read())
                {
                    list.Add(ReadDevice(r));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$m", mapId));
    }

    /// <summary>取得單一圖釘。</summary>
    public MapDeviceRecord? GetDevice(int id)
    {
        return _store.Query(
            """
            SELECT id, map_id, device_type, channel_id, x, y, angle, fov_deg, fov_depth, enabled
            FROM map_devices WHERE id = $id;
            """,
            static r => r.Read() ? ReadDevice(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>更新圖釘位置（拖放後儲存）。</summary>
    public void SetDevicePosition(int id, double x, double y)
    {
        _store.Execute(
            "UPDATE map_devices SET x = $x, y = $y WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$x", x);
                cmd.Parameters.AddWithValue("$y", y);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>啟用/停用圖釘（停用＝地圖檢視不顯示）。</summary>
    public void SetDeviceEnabled(int id, bool enabled)
    {
        _store.Execute(
            "UPDATE map_devices SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除圖釘。</summary>
    public void DeleteDevice(int id)
    {
        _store.Execute(
            "DELETE FROM map_devices WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>依通道尋找最早（sort_order, id）出現的地圖 id（事件定位用）。</summary>
    public int? FindMapByChannel(int channelId, string deviceType)
    {
        return _store.Query(
            """
            SELECT m.id
            FROM maps m
            JOIN map_devices d ON d.map_id = m.id
            WHERE m.enabled = 1 AND d.enabled = 1
              AND d.device_type = $t AND d.channel_id = $c
            ORDER BY m.sort_order, m.id
            LIMIT 1;
            """,
            static r => r.Read() ? (int)r.GetInt32(0) : (int?)null,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", deviceType);
                cmd.Parameters.AddWithValue("$c", channelId);
            });
    }

    private static MapRecord ReadMap(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetInt32(6) != 0,
            r.GetInt32(7));

    private static MapDeviceRecord ReadDevice(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetInt32(1),
            r.GetString(2),
            r.GetInt32(3),
            r.GetDouble(4),
            r.GetDouble(5),
            r.GetDouble(6),
            r.GetDouble(7),
            r.GetDouble(8),
            r.GetInt32(9) != 0);
}