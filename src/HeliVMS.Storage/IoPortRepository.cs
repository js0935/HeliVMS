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
/// <remarks>
/// <c>TerminalToken</c> 是攝影機端子權杖（ONVIF 的 <c>RelayOutputToken_1</c>／
/// <c>DigitalInputToken_2</c>），空字串＝未綁定；未綁定時才用端子號去猜
/// （見 <c>OnvifIoOutputWriter.RelayTokenFor</c> 的三段規則）。為什麼要存：權杖是不透明字串，
/// 端子號對應只是猜測；在設定中心明確綁定之後，動作一定要打在那顆端子上——猜錯等於驅動錯的
/// 閘門，或讓 A 端子的訊號觸發 B 端子的規則。
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
    string TerminalToken = "");

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
    public long Add(
        int channelId,
        IoPortKind kind,
        int number,
        string name,
        IoPolarity polarity,
        int debounceMs = 0,
        bool enabled = true,
        string? terminalToken = null)
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
            INSERT INTO io_ports (channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token)
            VALUES ($c, $k, $n, $m, $p, $d, $e, $t);
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
                cmd.Parameters.AddWithValue(
                    "$t",
                    string.IsNullOrWhiteSpace(terminalToken) ? DBNull.Value : terminalToken!.Trim());
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
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token
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
            "SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token FROM io_ports ORDER BY kind, number;",
            ReadRecords);
    }

    /// <summary>指定通道之埠，按 (kind, number) 排序。</summary>
    public IReadOnlyList<IoPortRecord> ListByChannel(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token
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
            SELECT id, channel_id, kind, number, name, polarity, debounce_ms, enabled, terminal_token
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
                reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        }

        return list;
    }
}