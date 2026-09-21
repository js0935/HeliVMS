namespace HeliVMS.Storage;

/// <summary>動作鏈動作類型（M74，§14.1 #16）。</summary>
public enum IoActionKind
{
    /// <summary>產生警報事件（App 端落 alarm_events）。</summary>
    Alarm,

    /// <summary>切換一顆 DO 輸出的邏輯狀態（反相）。</summary>
    ToggleDo,
}

/// <summary>一筆動作鏈規則（M74）：DI 邏輯上昇沿滿足冷卻→產生 Alarm 事件或切換指定 DO。</summary>
public sealed record IoRuleRecord(
    long Id,
    long InputPortId,
    IoActionKind ActionKind,
    long? OutputPortId,
    string? EventType,
    int RetriggerSec,
    bool Enabled);

/// <summary>感測器動作鏈規則存取（M74，§14.1 #16）。</summary>
public sealed class IoRuleRepository
{
    private readonly SqliteStore _store;

    public IoRuleRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立一條規則；輸入埠須存在。回傳 Id。</summary>
    public long Add(long inputPortId, IoActionKind actionKind, long? outputPortId, string? eventType, int retriggerSec = 30, bool enabled = true)
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

        return _store.Query(
            """
            INSERT INTO io_rules (input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled)
            VALUES ($i, $a, $o, $e, $r, $n);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$i", inputPortId);
                cmd.Parameters.AddWithValue("$a", actionKind == IoActionKind.Alarm ? "alarm" : "toggle_do");
                cmd.Parameters.AddWithValue("$o", outputPortId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$e", eventType ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$r", retriggerSec);
                cmd.Parameters.AddWithValue("$n", enabled ? 1 : 0);
            });
    }

    /// <summary>全部規則，按輸入埠排序。</summary>
    public IReadOnlyList<IoRuleRecord> List()
    {
        return _store.Query(
            """
            SELECT id, input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled
            FROM io_rules ORDER BY input_port_id, id;
            """,
            ReadRecords);
    }

    /// <summary>指定 DI 埠之規則，按 Id 排序。</summary>
    public IReadOnlyList<IoRuleRecord> ListByInput(long inputPortId)
    {
        return _store.Query(
            """
            SELECT id, input_port_id, action_kind, output_port_id, event_type, retrigger_sec, enabled
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
                reader.GetInt32(6) == 1));
        }

        return list;
    }
}