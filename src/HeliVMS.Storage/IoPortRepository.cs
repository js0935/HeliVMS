namespace HeliVMS.Storage;

/// <summary>感測器埠類型（M74，§14.1 #16）：DI＝乾接點輸入，DO＝乾接點輸出。</summary>
public enum IoPortKind
{
    /// <summary>數位輸入（感測器）。</summary>
    Di,

    /// <summary>數位輸出（繼電器/指示燈）。</summary>
    Do,
}

/// <summary>埠極性（M74）：常開＝閉合才接通；常閉＝斷開才動作。Engine 以「邏輯值」運算。</summary>
public enum IoPolarity
{
    /// <summary>常開：實體閉合→邏輯開。</summary>
    NormallyOpen,

    /// <summary>常閉：實體閉合→邏輯關。</summary>
    NormallyClosed,
}

/// <summary>一顆感測器埠（M74）：實體接點以 polarity 轉邏輯值，Engine 只在邏輯上昇沿觸發動作。</summary>
public sealed record IoPortRecord(
    long Id,
    int ChannelId,
    IoPortKind Kind,
    int Number,
    string Name,
    IoPolarity Polarity,
    int DebounceMs,
    bool Enabled);

/// <summary>
/// 感測器埠設定存取（M74，§14.1 #16）。每 (channel, kind, number) 唯一定義一埠；
/// 刪除 port 時連帶刪除其動作鏈規則。App 層提供可讀/測試視窗（M75），Engine 於 Storage 側。
/// </summary>
public sealed class IoPortRepository
{
    private readonly SqliteStore _store;

    public IoPortRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立一埠；同 (channel, kind, number) 重複→<see cref="ArgumentException"/>。回傳 Id。</summary>
    public long Add(int channelId, IoPortKind kind, int number, string name, IoPolarity polarity, int debounceMs = 0, bool enabled = true)
    {
        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "頻道須為正整數。");
        }

        if (number < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "端子號不可為負。");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("埠名稱不可為空。", nameof(name));
        }

        if (Exists(channelId, kind, number))
        {
            throw new ArgumentException($"埠已存在：channel={channelId} {kind} #{number}。");
        }

        return _store.Query(
            """
            INSERT INTO io_ports (channel_id, kind, number, name, polarity, debounce_ms, enabled)
            VALUES ($c, $k, $n, $m, $p, $d, $e);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$k", KindText(kind));
                cmd.Parameters.AddWithValue("$n", number);
                cmd.Parameters.AddWithValue("$m", name);
                cmd.Parameters.AddWithValue("$p", PolarityText(polarity));
                cmd.Parameters.AddWithValue("$d", debounceMs);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            });
    }

    /// <summary>同 (channel, kind, number) 是否已存在。</summary>
    public bool Exists(int channelId, IoPortKind kind, int number)
    {
        return _store.Query(
            "SELECT EXISTS(SELECT 1 FROM io_ports WHERE channel_id = $c AND kind = $k AND number = $n);",
            static r =>
            {
                r.Read();
                return r.GetInt32(0) == 1;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$k", KindText(kind));
                cmd.Parameters.AddWithValue("$n", number);
            });
    }

    /// <summary>全部埠，按 (kind, number) 排序。</summary>
    public IReadOnlyList<IoPortRecord> List()
    {
        return _store.Query(
            "SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled FROM io_ports ORDER BY kind, number;",
            ReadRecords);
    }

    /// <summary>指定通道之埠，按 (kind, number) 排序。</summary>
    public IReadOnlyList<IoPortRecord> ListByChannel(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled
            FROM io_ports
            WHERE channel_id = $c
            ORDER BY kind, number;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>依 Id 讀單一埠；不存在→null。</summary>
    public IoPortRecord? Get(long id)
    {
        var list = _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled
            FROM io_ports WHERE id = $id;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$id", id));
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>刪除埠（連帶其規則）；回傳是否實際存在。</summary>
    public bool Delete(long id)
    {
        var exists = _store.Query(
            "SELECT EXISTS(SELECT 1 FROM io_ports WHERE id = $id);",
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

        _store.Execute("DELETE FROM io_rules WHERE input_port_id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        _store.Execute("DELETE FROM io_ports WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        return true;
    }

    internal static string KindText(IoPortKind kind) => kind == IoPortKind.Di ? "DI" : "DO";

    internal static IoPortKind KindFromText(string text) => text == "DO" ? IoPortKind.Do : IoPortKind.Di;

    internal static string PolarityText(IoPolarity polarity) => polarity == IoPolarity.NormallyClosed ? "normally_closed" : "normally_open";

    internal static IoPolarity PolarityFromText(string text) => text == "normally_closed" ? IoPolarity.NormallyClosed : IoPolarity.NormallyOpen;

    private static IReadOnlyList<IoPortRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var list = new List<IoPortRecord>();
        while (reader.Read())
        {
            list.Add(new IoPortRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                KindFromText(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetString(4),
                PolarityFromText(reader.GetString(5)),
                reader.GetInt32(6),
                reader.GetInt32(7) == 1));
        }

        return list;
    }
}