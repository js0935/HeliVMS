using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 頻道管理（§4 channels 表；錄影決策與監看之源頭資料）。
/// </summary>
public sealed class ChannelRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    /// <summary>
    /// 預設自建 <see cref="AuditLogRepository"/>（同 <c>RecordingScheduleRepository</c> 作法）：
    /// 頻道新增/修改/刪除是管理動作，一律留稽核軌跡；測試若要檢視稽核可用 <paramref name="audit"/> 注入。
    /// </summary>
    public ChannelRepository(SqliteStore store, AuditLogRepository? audit = null)
    {
        _store = store;
        _audit = audit ?? new AuditLogRepository(store);
    }

    /// <summary>列出全部頻道（含停用）。</summary>
    public IReadOnlyList<ChannelInfo> List()
    {
        return _store.Query(
            """
            SELECT id, device_id, name, main_rtsp, sub_rtsp, codec,
                   audio_enabled, audio_encoder, recording_mode,
                   motion_enabled, motion_sensitivity, enabled, display_cell
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

    /// <summary>
    /// 此頻道在目前頻道中的建立順序排名（0 基）：比它更早建立（id 更小）的頻道數。
    /// </summary>
    /// <remarks>
    /// 授權路數上限必須以此排名判定，不能直接拿 <c>channels.id</c> 比大小。
    /// id 是 <c>AUTOINCREMENT</c> 且刪除後不回收，反覆新增／刪除（設定精靈、測試）會把
    /// id 推到遠超過實際頻道數；直接比 id 會讓合法頻道被誤擋成「超出授權範圍」。
    /// 排名只取決於「目前還有幾個頻道比它早」，刪除前面的頻道時後面的會自動遞補。
    /// </remarks>
    public int RankOf(int channelId)
    {
        return _store.Query(
            "SELECT COUNT(*) FROM channels WHERE id < $id;",
            static r => r.Read() ? (int)r.GetInt64(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$id", channelId));
    }

    /// <summary>取得單一頻道。</summary>
        /// <summary>Channel VMD sensitivity set (M114, auto-apply).</summary>
    public void SetMotionSensitivity(int id, double sensitivity)
    {
        _store.Execute(
            """
            UPDATE channels SET motion_sensitivity = $ms WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ms", sensitivity);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    public ChannelInfo? Get(int id)
    {
        return _store.Query(
            """
            SELECT id, device_id, name, main_rtsp, sub_rtsp, codec,
                   audio_enabled, audio_encoder, recording_mode,
                   motion_enabled, motion_sensitivity, enabled, display_cell
            FROM channels WHERE id = $id;
            """,
            static r => r.Read() ? ReadChannel(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>新增頻道，回傳新 ID（稽核掛載：channel.add 寫 audit_log）。</summary>
    public int Add(
        string name,
        string mainRtsp,
        string? subRtsp = null,
        int? deviceId = null,
        string codec = "h264",
        bool audioEnabled = true,
        string audioEncoder = "copy",
        RecordingMode recordingMode = RecordingMode.Always,
        int? displayCell = null,
        string? actor = null)
    {
        var id = InsertChannel(name, mainRtsp, subRtsp, deviceId, codec, audioEnabled, audioEncoder, recordingMode, displayCell);
        _audit.Record(actor, "channel.add", AuditCategories.Config, "channel", id, name);
        return id;
    }

    /// <summary>實際寫入頻道列（不含稽核）；供 <see cref="EnsureSeedChannels"/> 等非使用者動作使用。</summary>
    private int InsertChannel(
        string name,
        string mainRtsp,
        string? subRtsp,
        int? deviceId,
        string codec,
        bool audioEnabled,
        string audioEncoder,
        RecordingMode recordingMode,
        int? displayCell)
    {
        return _store.Query(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder, recording_mode, display_cell)
            VALUES ($d, $n, $m, $s, $c, $a, $ae, $rm, $dc);
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
                cmd.Parameters.AddWithValue("$rm", (int)recordingMode);
                cmd.Parameters.AddWithValue("$dc", (object?)displayCell ?? DBNull.Value);
            });
    }

    /// <summary>更新頻道設定（稽核掛載：channel.update 寫 audit_log）。</summary>
    public void Update(ChannelInfo channel, string? actor = null)
    {
        var affected = _store.Query<int>(
            """
            UPDATE channels
            SET name = $n, main_rtsp = $m, sub_rtsp = $s, codec = $c,
                audio_enabled = $a, audio_encoder = $ae, recording_mode = $rm,
                motion_enabled = $me, motion_sensitivity = $ms, enabled = $en,
                display_cell = $dc
            WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
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
                cmd.Parameters.AddWithValue("$en", channel.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$dc", (object?)channel.DisplayCell ?? DBNull.Value);
            });

        if (affected > 0)
        {
            _audit.Record(actor, "channel.update", AuditCategories.Config, "channel", channel.Id,
                $"{channel.Name} enabled={channel.Enabled} cell={channel.DisplayCell?.ToString() ?? "auto"}");
        }
    }

    /// <summary>
    /// 設備的主／子碼流換新時，把「仍然完全等於舊設備值」的綁定頻道一起換掉，回傳受影響筆數。
    /// <para>
    /// 頻道在建立當下複製一份設備的碼流位址（<see cref="ChannelEnrollment"/>），之後就與 devices 分家；
    /// 重新探索攝影機時若只更新 devices，畫面上看到的是新碼流、實際錄影與多分割仍在拉舊位址
    /// （典型症狀：次碼流一直是 Profile102，也就是串流 3）。比對舊值才不會蓋掉操作員手動改過的頻道：
    /// 那條網址是刻意指定的，自動傳播不該有意見。
    /// </para>
    /// 傳播本身不另寫 channel.update 稽核——來源動作已有 device.streams 記錄，
    /// 這裡逐台再記一次只會讓稽核變成同一件事的重複。
    /// </summary>
    public int RefreshDeviceStreamUrls(int deviceId, string? oldMain, string? oldSub, string? main, string? sub)
    {
        return _store.Query<int>(
            """
            UPDATE channels
            SET main_rtsp = $m, sub_rtsp = $s
            WHERE device_id = $d
              AND IFNULL(main_rtsp, '') = IFNULL($om, '')
              AND IFNULL(sub_rtsp, '') = IFNULL($os, '');
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$om", (object?)oldMain ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$os", (object?)oldSub ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$m", (object?)main ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", (object?)sub ?? DBNull.Value);
            });
    }

    /// <summary>刪除頻道（其區段索引連帶刪除）。稽核掛載：channel.delete 寫 audit_log。</summary>
    public void Delete(int id, string? actor = null)
    {
        var name = Get(id)?.Name ?? $"id:{id}";
        _store.Execute("DELETE FROM channels WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        _audit.Record(actor, "channel.delete", AuditCategories.Config, "channel", id, name);
    }

    /// <summary>若無任何頻道，植入測試流兩個頻道（開發／示範用途；非使用者動作，故不寫稽核）。</summary>
    public int EnsureSeedChannels()
    {
        var existing = List();
        if (existing.Count > 0)
        {
            return existing.Count;
        }

        InsertChannel("虛擬測試流 · 主流", "rtsp://127.0.0.1:8554/main", "rtsp://127.0.0.1:8554/sub",
            null, "h264", true, "copy", RecordingMode.Always, null);
        InsertChannel("虛擬測試流 · 次流", "rtsp://127.0.0.1:8554/sub", null,
            null, "h264", false, "copy", RecordingMode.Always, null);
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
            // 漏掉這一欄的話 ChannelInfo.Enabled 會永遠是預設值 true，
            // 於是「停用某台攝影機」讀回來仍是啟用，停用功能等於不存在。
            Enabled = r.GetInt32(11) != 0,
            DisplayCell = r.IsDBNull(12) ? null : r.GetInt32(12),
        };
    }
}