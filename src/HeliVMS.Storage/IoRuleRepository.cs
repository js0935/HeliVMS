namespace HeliVMS.Storage;

/// <summary>動作鏈動作類型（M74，§14.1 #16）。</summary>
public enum IoActionKind
{
    /// <summary>產生警報事件（App 端落 alarm_events）。</summary>
    Alarm,

    /// <summary>切換一顆 DO 輸出的邏輯狀態（反相）。</summary>
    ToggleDo,
}

/// <summary>
/// 一筆動作鏈規則（M74）：DI 邏輯上昇沿滿足冷卻→產生 Alarm 事件或切換指定 DO。
/// </summary>
/// <remarks>
/// <para>
/// 「觸發後要做什麼」的警報動作（Link* 欄位）由警報中心設定；接點本身在設定中心 → IO 登記。
/// 這是對照 Home Assistant「裝置／實體」與「自動化」分離的設計：接點頁不兼管規則。
/// </para>
/// <para>
/// <see cref="LinkChannelId"/>／<see cref="LinkFullscreen"/>／<see cref="LinkSnapshot"/>／
/// <see cref="LinkSoundPath"/>／<see cref="LinkMap"/> 是畫面側的連動（由 App 層執行）；
/// <see cref="OutputSeconds"/> 是繼電器輸出秒數（>0 ＝輸出 N 秒後自動賦歸，由宿主層計時）；
/// <see cref="ResetSeconds"/> 是畫面賦歸秒數（0＝不自動恢復）。
/// </para>
/// </remarks>
public sealed record IoRuleRecord(
    long Id,
    long InputPortId,
    IoActionKind ActionKind,
    long? OutputPortId,
    string? EventType,
    int RetriggerSec,
    bool Enabled,
    int? LinkChannelId = null,
    bool LinkFullscreen = false,
    bool LinkSnapshot = false,
    string? LinkSoundPath = null,
    bool LinkMap = false,
    int ResetSeconds = 0,
    int OutputSeconds = 0);

/// <summary>感測器動作鏈規則存取（M74，§14.1 #16）。</summary>
public sealed class IoRuleRepository
{
    private readonly SqliteStore _store;

    public IoRuleRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立一條規則；輸入埠須存在。回傳 Id。</summary>
    public long Add(
        long inputPortId,
        IoActionKind actionKind,
        long? outputPortId,
        string? eventType,
        int retriggerSec = 30,
        bool enabled = true,
        int? linkChannelId = null,
        bool linkFullscreen = false,
        bool linkSnapshot = false,
        string? linkSoundPath = null,
        bool linkMap = false,
        int resetSeconds = 0,
        int outputSeconds = 0)
    {
        Validate(inputPortId, actionKind, outputPortId, eventType, retriggerSec);

        return _store.Query(
            """
            INSERT INTO io_rules (input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled,
                                  link_channel_id, link_fullscreen, link_snapshot, link_sound_path, link_map,
                                  reset_seconds, output_seconds)
            VALUES ($i, $a, $o, $e, $r, $n, $lc, $lf, $ls, $lsp, $lm, $rs, $os);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                Fill(cmd, inputPortId, actionKind, outputPortId, eventType, retriggerSec, enabled,
                    linkChannelId, linkFullscreen, linkSnapshot, linkSoundPath, linkMap, resetSeconds, outputSeconds);
            });
    }

    /// <summary>全部規則，按輸入埠排序。</summary>
    public IReadOnlyList<IoRuleRecord> List()
    {
        return _store.Query(
            """
            SELECT id, input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled,
                   link_channel_id, link_fullscreen, link_snapshot, link_sound_path, link_map,
                   reset_seconds, output_seconds
            FROM io_rules ORDER BY input_port_id, id;
            """,
            ReadRecords);
    }

    /// <summary>指定 DI 埠之規則，按 Id 排序。</summary>
    public IReadOnlyList<IoRuleRecord> ListByInput(long inputPortId)
    {
        return _store.Query(
            """
            SELECT id, input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled,
                   link_channel_id, link_fullscreen, link_snapshot, link_sound_path, link_map,
                   reset_seconds, output_seconds
            FROM io_rules WHERE input_port_id = $i ORDER BY id;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$i", inputPortId));
    }

    /// <summary>刪除規則；回傳是否實際存在。</summary>
    public bool Delete(long id)
    {
        _store.Execute("DELETE FROM io_rules WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        return true;
    }

    /// <summary>
    /// 更新一條規則（整列覆寫）；回傳是否實際存在。
    /// </summary>
    /// <remarks>
    /// 規則在警報中心編輯，引擎由 <c>IoRuleHost.Reload</c> 重建——這裡只管存取，
    /// 不負責讓執行中的監視器生效。
    /// </remarks>
    public bool Update(
        long id,
        long inputPortId,
        IoActionKind actionKind,
        long? outputPortId,
        string? eventType,
        int retriggerSec,
        bool enabled,
        int? linkChannelId = null,
        bool linkFullscreen = false,
        bool linkSnapshot = false,
        string? linkSoundPath = null,
        bool linkMap = false,
        int resetSeconds = 0,
        int outputSeconds = 0)
    {
        Validate(inputPortId, actionKind, outputPortId, eventType, retriggerSec);

        return _store.Query(
            """
            UPDATE io_rules
            SET input_port_id = $i, action_kind = $a, output_port_id = $o,
                event_type = $e, retrigger_sec = $r, enabled = $n,
                link_channel_id = $lc, link_fullscreen = $lf, link_snapshot = $ls,
                link_sound_path = $lsp, link_map = $lm, reset_seconds = $rs, output_seconds = $os
            WHERE id = $id;
            SELECT changes();
            """
            ,static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                Fill(cmd, inputPortId, actionKind, outputPortId, eventType, retriggerSec, enabled,
                    linkChannelId, linkFullscreen, linkSnapshot, linkSoundPath, linkMap, resetSeconds, outputSeconds);
            });
    }

    /// <summary>只切換啟用／停用（警報中心列表的開關；不動其他欄位）。</summary>
    public bool SetEnabled(long id, bool enabled)
    {
        return _store.Query(
            "UPDATE io_rules SET enabled = $n WHERE id = $id; SELECT changes();",
            static r => r.Read() && r.GetInt32(0) > 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", enabled ? 1 : 0);
            });
    }

    /// <summary>Add／Update 共用的參數填法（同一口徑，兩邊不會漂移）。</summary>
    private static void Fill(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        long inputPortId,
        IoActionKind actionKind,
        long? outputPortId,
        string? eventType,
        int retriggerSec,
        bool enabled,
        int? linkChannelId,
        bool linkFullscreen,
        bool linkSnapshot,
        string? linkSoundPath,
        bool linkMap,
        int resetSeconds,
        int outputSeconds)
    {
        cmd.Parameters.AddWithValue("$i", inputPortId);
        cmd.Parameters.AddWithValue("$a", actionKind == IoActionKind.Alarm ? "alarm" : "toggle_do");
        cmd.Parameters.AddWithValue("$o", outputPortId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$e", eventType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$r", retriggerSec);
        cmd.Parameters.AddWithValue("$n", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$lc", (object?)linkChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lf", linkFullscreen ? 1 : 0);
        cmd.Parameters.AddWithValue("$ls", linkSnapshot ? 1 : 0);
        cmd.Parameters.AddWithValue("$lsp", (object?)linkSoundPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lm", linkMap ? 1 : 0);
        cmd.Parameters.AddWithValue("$rs", resetSeconds);
        cmd.Parameters.AddWithValue("$os", outputSeconds);
    }

    /// <summary>規則欄位的共同檢查（Add／Update 同一口徑，不合法就擋在儲存之前）。</summary>
    private static void Validate(
        long inputPortId,
        IoActionKind actionKind,
        long? outputPortId,
        string? eventType,
        int retriggerSec)
    {
        if (inputPortId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputPortId), "輸入埠 Id 須為正整數。");
        }

        if (actionKind == IoActionKind.ToggleDo && outputPortId is null)
        {
            throw new ArgumentException("ToggleDo 動作須指定輸出埠。", nameof(outputPortId));
        }

        if (actionKind == IoActionKind.Alarm && string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Alarm 動作須指定事件類型。", nameof(eventType));
        }

        if (retriggerSec < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retriggerSec), "冷卻秒數不可為負。");
        }
    }

    private static IReadOnlyList<IoRuleRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var list = new List<IoRuleRecord>();
        while (reader.Read())
        {
            list.Add(new IoRuleRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2) == "alarm" ? IoActionKind.Alarm : IoActionKind.ToggleDo,
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8) != 0,
                reader.GetInt32(9) != 0,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt32(11) != 0,
                reader.GetInt32(12),
                reader.GetInt32(13)));
        }

        return list;
    }
}
