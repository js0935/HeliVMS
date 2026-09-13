using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 頻道管理（§4 channels 表；錄影決策與監看之源頭資料）。
/// </summary>
public sealed class ChannelRepository
{
    private readonly SqliteStore _store;

    public ChannelRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>列出全部頻道（含停用）。</summary>
    public IReadOnlyList<ChannelInfo> List()
    {
        return _store.Query(
            """
            SELECT id, device_id, name, main_rtsp, sub_rtsp, codec,
                   audio_enabled, audio_encoder, recording_mode,
                   motion_enabled, motion_sensitivity
            FROM channels ORDER BY id;
            """,
            static r =>
            {
                var list = new List<ChannelInfo>();
                while (r.Read())
                {
                    list.Add(ReadChannel(r));
                }

                return list;
            });
    }

    /// <summary>取得單一頻道。</summary>
    public ChannelInfo? Get(int id)
    {
        return _store.Query(
            """
            SELECT id, device_id, name, main_rtsp, sub_rtsp, codec,
                   audio_enabled, audio_encoder, recording_mode,
                   motion_enabled, motion_sensitivity
            FROM channels WHERE id = $id;
            """,
            static r => r.Read() ? ReadChannel(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>新增頻道，回傳新 ID。</summary>
    public int Add(
        string name,
        string mainRtsp,
        string? subRtsp = null,
        int? deviceId = null,
        string codec = "h264",
        bool audioEnabled = true,
        string audioEncoder = "copy")
    {
        return _store.Query(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES ($d, $n, $m, $s, $c, $a, $ae);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", (object?)deviceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$m", mainRtsp);
                cmd.Parameters.AddWithValue("$s", (object?)subRtsp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", codec);
                cmd.Parameters.AddWithValue("$a", audioEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ae", audioEncoder);
            });
    }

    /// <summary>更新頻道設定。</summary>
    public void Update(ChannelInfo channel)
    {
        _store.Execute(
            """
            UPDATE channels
            SET name = $n, main_rtsp = $m, sub_rtsp = $s, codec = $c,
                audio_enabled = $a, audio_encoder = $ae, recording_mode = $rm,
                motion_enabled = $me, motion_sensitivity = $ms
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", channel.Id);
                cmd.Parameters.AddWithValue("$n", channel.Name);
                cmd.Parameters.AddWithValue("$m", channel.MainStreamUrl);
                cmd.Parameters.AddWithValue(
                    "$s",
                    string.IsNullOrEmpty(channel.SubStreamUrl) ? DBNull.Value : channel.SubStreamUrl);
                cmd.Parameters.AddWithValue("$c", channel.Codec);
                cmd.Parameters.AddWithValue("$a", channel.AudioEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ae", channel.AudioEncoder);
                cmd.Parameters.AddWithValue("$rm", (int)channel.RecordingMode);
                cmd.Parameters.AddWithValue("$me", channel.MotionEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ms", channel.MotionSensitivity);
            });
    }

    /// <summary>刪除頻道（其區段索引連帶刪除）。</summary>
    public void Delete(int id)
    {
        _store.Execute("DELETE FROM channels WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>若無任何頻道，植入測試流兩個頻道（開發／示範用途）。</summary>
    public int EnsureSeedChannels()
    {
        var existing = List();
        if (existing.Count > 0)
        {
            return existing.Count;
        }

        Add("虛擬測試流 · 主流", "rtsp://127.0.0.1:8554/main", "rtsp://127.0.0.1:8554/sub", audioEnabled: true);
        Add("虛擬測試流 · 次流", "rtsp://127.0.0.1:8554/sub", null, codec: "h264", audioEnabled: false);
        return 2;
    }

    private static ChannelInfo ReadChannel(SqliteDataReader r)
    {
        return new ChannelInfo
        {
            Id = r.GetInt32(0),
            DeviceId = r.IsDBNull(1) ? null : r.GetInt32(1),
            Name = r.GetString(2),
            MainStreamUrl = r.GetString(3),
            SubStreamUrl = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            Codec = r.GetString(5),
            AudioEnabled = r.GetInt32(6) != 0,
            AudioEncoder = r.GetString(7),
            RecordingMode = (RecordingMode)r.GetInt32(8),
            MotionEnabled = r.GetInt32(9) != 0,
            MotionSensitivity = r.GetDouble(10),
        };
    }
}