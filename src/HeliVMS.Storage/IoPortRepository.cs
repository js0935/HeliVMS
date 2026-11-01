namespace HeliVMS.Storage;

/// <summary>接點方向。</summary>
public enum IoPortKind
{
    /// <summary>乾接點輸入。</summary>
    Di,

    /// <summary>繼電器輸出。</summary>
    Do,
}

/// <summary>接點極性。</summary>
public enum IoPolarity
{
    /// <summary>常開（NO）：閉合＝觸發。</summary>
    NormallyOpen,

    /// <summary>常閉（NC）：斷開＝觸發。</summary>
    NormallyClosed,
}

/// <summary>
/// 一個接點（§14.1 #16）：綁在頻道上的 DI／DO，攝影機端子與 Modbus 通道統一登記於此，
/// 連動規則（<c>io_rules</c>）只認這張表。
/// </summary>
/// <remarks>
/// <c>TerminalToken</c> 指向攝影機端子；<c>IoChannelId</c> 指向 Modbus 通道
/// （<c>io_channels</c>）。null＝攝影機端子或手動建立的埠。
/// 輸出路由靠 <c>IoChannelId</c> 決定打到哪種硬體（攝影機繼電器 / Modbus 線圈）。
/// </remarks>
public sealed record IoPortRecord(
    long Id,
    int ChannelId,
    IoPortKind Kind,
    int Number,
    string Name,
    IoPolarity Polarity,
    int DebounceMs,
    bool Enabled,
    string TerminalToken = "",
    int? IoChannelId = null);

/// <summary>
/// 感測器埠設定存取（M74，§14.1 #16）。每 (channel, kind, number) 唯一定義一埠；
/// Modbus 通道與攝影機端子都登記在這裡（<c>IoChannelId</c> 欄位區分來源）。
/// </summary>
public sealed class IoPortRepository
{
    private readonly SqliteStore _store;

    public IoPortRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>建立一埠；同 (channel, kind, number) 重複→<see cref="ArgumentException"/>。回傳 Id。</summary>
    public long Add(
        int channelId,
        IoPortKind kind,
        int number,
        string name,
        IoPolarity polarity,
        int debounceMs = 0,
        bool enabled = true,
        string? terminalToken = null,
        int? ioChannelId = null)
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
            INSERT INTO io_ports (channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id)
            VALUES ($c, $k, $n, $m, $p, $d, $e, $t, $ic);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                Fill(cmd, channelId, kind, number, name, polarity, debounceMs, enabled, terminalToken, ioChannelId);
            });
    }

    /// <summary>
    /// 依攝影機端子權杖找埠；找不到回 <c>null</c>。設定中心用它在清單上標示「已對應」。
    /// </summary>
    public IoPortRecord? FindByTerminal(int channelId, IoPortKind kind, string terminalToken)
    {
        if (string.IsNullOrWhiteSpace(terminalToken))
        {
            return null;
        }

        var list = _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id
            FROM io_ports
            WHERE channel_id = $c AND kind = $k AND terminal_token = $t
            LIMIT 1;
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$k", KindText(kind));
                cmd.Parameters.AddWithValue("$t", terminalToken.Trim());
            });
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>把埠綁到攝影機端子（<paramref name="terminalToken"/> 為 null／空＝解除綁定）。</summary>
    public void SetTerminalToken(long id, string? terminalToken)
    {
        _store.Execute(
            "UPDATE io_ports SET terminal_token = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue(
                    "$t",
                    string.IsNullOrWhiteSpace(terminalToken) ? DBNull.Value : terminalToken!.Trim());
            });
    }

    /// <summary>
    /// 建立（或沿用）「攝影機端子 → 埠」的綁定；回傳埠 Id 與是否新建。
    /// </summary>
    /// <remarks>
    /// 設定中心的「綁定為埠」按鈕會被連按，也可能對同一個端子重複按，因此這裡必須冪等：
    /// 先看同一端子是否已綁過（直接沿用），再看同一個端子號是否已有埠（把該埠綁上去，
    /// 不新增第二列——<c>io_ports</c> 對 <c>(channel, kind, number)</c> 有唯一鍵）。
    /// </remarks>
    public (long Id, bool Created) EnsureForTerminal(
        int channelId,
        IoPortKind kind,
        int number,
        string name,
        string terminalToken,
        IoPolarity polarity = IoPolarity.NormallyOpen,
        int debounceMs = 0)
    {
        if (string.IsNullOrWhiteSpace(terminalToken))
        {
            throw new ArgumentException("端子權杖不可為空。", nameof(terminalToken));
        }

        var bound = FindByTerminal(channelId, kind, terminalToken);
        if (bound is not null)
        {
            return (bound.Id, false);
        }

        // 注意：Query 的單值多載只讀第一列；這裡要「有沒有列」與該列 Id，所以自己讀成清單。
        var existing = _store.Query(
            "SELECT id FROM io_ports WHERE channel_id = $c AND kind = $k AND number = $n;",
            static r =>
            {
                var ids = new List<long>();
                while (r.Read())
                {
                    ids.Add(r.GetInt64(0));
                }

                return ids;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$k", KindText(kind));
                cmd.Parameters.AddWithValue("$n", number);
            });
        if (existing.Count > 0)
        {
            SetTerminalToken(existing[0], terminalToken);
            return (existing[0], false);
        }

        var id = Add(channelId, kind, number, name, polarity, debounceMs, enabled: true, terminalToken: terminalToken);
        return (id, true);
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
            "SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id FROM io_ports ORDER BY kind, number;",
            ReadRecords);
    }

    /// <summary>指定通道之埠，按 (kind, number) 排序。</summary>
    public IReadOnlyList<IoPortRecord> ListByChannel(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id
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
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id
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

    /// <summary>依 Modbus 通道找對應的接點；不存在回 <c>null</c>。</summary>
    public IoPortRecord? FindByIoChannel(int ioChannelId)
    {
        var list = _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token, io_channel_id
            FROM io_ports WHERE io_channel_id = $ic LIMIT 1;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$ic", ioChannelId));
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// 為 Modbus 通道建立（或更新）對應的接點；回傳埠 Id 與是否新建。
    /// </summary>
    /// <remarks>
    /// 接點統一登記在 <c>io_ports</c>，Modbus 通道與攝影機端子才能共用同一套連動規則引擎。
    /// 編號從 <c>IoIndex + 1</c> 起找第一個空位：不搶走同一頻道上攝影機端子已佔用的編號
    /// （搶了等於把攝影機端子的接點改指向 Modbus，DO 輸出會打到錯的硬體）。
    /// </remarks>
    public (long Id, bool Created) EnsureForIoChannel(
        int videoChannelId,
        int ioChannelId,
        IoPortKind kind,
        int startNumber,
        string name,
        IoPolarity polarity,
        int debounceMs = 0)
    {
        var existing = FindByIoChannel(ioChannelId);
        if (existing is not null)
        {
            UpdateIoChannelFields(existing.Id, videoChannelId, kind, existing.Number, name, polarity, debounceMs);
            return (existing.Id, false);
        }

        var number = startNumber;
        while (Exists(videoChannelId, kind, number))
        {
            number++;
        }

        var id = Add(videoChannelId, kind, number, name, polarity, debounceMs, enabled: true, ioChannelId: ioChannelId);
        return (id, true);
    }

    /// <summary>更新 Modbus 通道對應接點的欄位（名字改了、極性改了要跟著）。</summary>
    private void UpdateIoChannelFields(
        long id,
        int videoChannelId,
        IoPortKind kind,
        int number,
        string name,
        IoPolarity polarity,
        int debounceMs)
    {
        _store.Execute(
            """
            UPDATE io_ports SET channel_id = $c, kind = $k, number = $n, name = $m,
                polarity = $p, debounce_ms = $d
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$c", videoChannelId);
                cmd.Parameters.AddWithValue("$k", KindText(kind));
                cmd.Parameters.AddWithValue("$n", number);
                cmd.Parameters.AddWithValue("$m", name);
                cmd.Parameters.AddWithValue("$p", PolarityText(polarity));
                cmd.Parameters.AddWithValue("$d", debounceMs);
            });
    }

    /// <summary>刪除 Modbus 通道對應的接點（通道被刪除時；其上的連動規則一併清掉）。</summary>
    public void DeleteByIoChannel(int ioChannelId)
    {
        var port = FindByIoChannel(ioChannelId);
        if (port is null)
        {
            return;
        }

        _store.Execute("DELETE FROM io_rules WHERE input_port_id = $id;", cmd => cmd.Parameters.AddWithValue("$id", port.Id));
        _store.Execute("DELETE FROM io_ports WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", port.Id));
    }

    internal static string KindText(IoPortKind kind) => kind == IoPortKind.Di ? "DI" : "DO";

    internal static IoPortKind KindFromText(string text) => text == "DO" ? IoPortKind.Do : IoPortKind.Di;

    internal static string PolarityText(IoPolarity polarity) => polarity == IoPolarity.NormallyClosed ? "normally_closed" : "normally_open";

    internal static IoPolarity PolarityFromText(string text) => text == "normally_closed" ? IoPolarity.NormallyClosed : IoPolarity.NormallyOpen;

    private static void Fill(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        int channelId,
        IoPortKind kind,
        int number,
        string name,
        IoPolarity polarity,
        int debounceMs,
        bool enabled,
        string? terminalToken,
        int? ioChannelId)
    {
        cmd.Parameters.AddWithValue("$c", channelId);
        cmd.Parameters.AddWithValue("$k", KindText(kind));
        cmd.Parameters.AddWithValue("$n", number);
        cmd.Parameters.AddWithValue("$m", name);
        cmd.Parameters.AddWithValue("$p", PolarityText(polarity));
        cmd.Parameters.AddWithValue("$d", debounceMs);
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "$t",
            string.IsNullOrWhiteSpace(terminalToken) ? DBNull.Value : terminalToken!.Trim());
        cmd.Parameters.AddWithValue("$ic", (object?)ioChannelId ?? DBNull.Value);
    }

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
                reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        }

        return list;
    }
}
