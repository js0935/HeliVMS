namespace HeliVMS.Storage;

/// <summary>裝置群組／分區：設備樹的一層節點（§7.4）。</summary>
/// <param name="Id">群組 Id。</param>
/// <param name="Name">顯示名稱（全樹唯一）。</param>
/// <param name="ParentId">上層群組 Id；頂層分區為 <see langword="null"/>。</param>
/// <param name="Position">同一上層內的排序（0 基）。</param>
public sealed record DeviceGroup(long Id, string Name, long? ParentId, int Position);

/// <summary>
/// 裝置群組倉儲（v50，§7.4）：設備樹「群組／分區 → 設備 → 通道」的群組層與設備歸組。
/// </summary>
/// <remarks>
/// <para>
/// <c>parent_id</c> 讓分區可再分子區；<c>devices.group_id</c> 決定設備掛在哪個群組下，
/// 通道隨設備掛入，所以三層樹不必額外的成員表——設備「一台一組」也是由這個單一欄位自然保證。
/// </para>
/// <para>
/// 名稱刻意採全樹唯一（資料庫 <c>UNIQUE(name)</c>）：樹狀 UI 只用名稱當標籤，
/// 允許同名會讓操作員無從分辨要拖到哪一個節點。
/// </para>
/// <para>
/// <see cref="Move"/> 有循環防護：不能把群組移到自己的子樹底下，否則樹會變成環，
/// UI 遞迴展開時無限迴圈、整個裝置頁卡死。
/// </para>
/// </remarks>
public sealed class DeviceGroupRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public DeviceGroupRepository(SqliteStore store, AuditLogRepository audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>列出所有群組（頂層在前，同一上層內依 <c>position</c>）；UI 據此組樹。</summary>
    public IReadOnlyList<DeviceGroup> List()
    {
        return _store.Query(
            "SELECT id, name, parent_id, position FROM device_groups ORDER BY parent_id, position, id;",
            static r =>
            {
                var list = new List<DeviceGroup>();
                while (r.Read())
                {
                    list.Add(new DeviceGroup(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetInt64(2),
                        r.GetInt32(3)));
                }

                return list;
            });
    }

    public DeviceGroup? Get(long id)
    {
        return _store.Query(
            "SELECT id, name, parent_id, position FROM device_groups WHERE id = $id;",
            static r => r.Read()
                ? new DeviceGroup(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetInt64(2),
                    r.GetInt32(3))
                : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>建立群組（可掛在 <paramref name="parentId"/> 之下）；名稱重複時丟 <see cref="ArgumentException"/>。</summary>
    public long Create(string name, long? parentId = null, string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (NameExists(name, exceptId: null))
        {
            throw new ArgumentException($"群組名稱重複：{name}。", nameof(name));
        }

        if (parentId is long parent && Get(parent) is null)
        {
            throw new ArgumentException($"上層群組不存在：{parent}。", nameof(parentId));
        }

        var position = NextPosition(parentId);
        var id = _store.Query(
            """
            INSERT INTO device_groups (name, parent_id, position) VALUES ($n, $p, $pos);
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
                cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pos", position);
            });

        _audit.Record(actor, "device.group.add", AuditCategories.Config, "device_group", id, name);
        return id;
    }

    /// <summary>重新命名；撞到其他群組名（去空白後相同不算）時丟 <see cref="ArgumentException"/>。</summary>
    public void Rename(long id, string name, string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (NameExists(name, exceptId: id))
        {
            throw new ArgumentException($"群組名稱重複：{name}。", nameof(name));
        }

        var affected = _store.Query<int>(
            """
            UPDATE device_groups SET name = $n WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name);
            });

        if (affected > 0)
        {
            _audit.Record(actor, "device.group.rename", AuditCategories.Config, "device_group", id, name);
        }
    }

    /// <summary>
    /// 把群組移到新的上層（<paramref name="newParentId"/> 為 <see langword="null"/> 即移到頂層），
    /// 並排到新上層的末端。移到自己或自己的子樹底下會被拒絕（避免成環）。
    /// </summary>
    public void Move(long id, long? newParentId, string? actor = null)
    {
        if (newParentId == id)
        {
            throw new ArgumentException("不可把群組移到它自己底下。", nameof(newParentId));
        }

        if (newParentId is long parent)
        {
            if (Get(parent) is null)
            {
                throw new ArgumentException($"上層群組不存在：{parent}。", nameof(newParentId));
            }

            if (IsDescendant(ancestorId: id, nodeId: parent))
            {
                throw new ArgumentException("不可把群組移到它自己的子樹底下。", nameof(newParentId));
            }
        }

        var position = NextPosition(newParentId);
        var affected = _store.Query<int>(
            """
            UPDATE device_groups SET parent_id = $p, position = $pos WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$p", (object?)newParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pos", position);
            });

        if (affected > 0)
        {
            _audit.Record(actor, "device.group.move", AuditCategories.Config, "device_group", id, newParentId?.ToString() ?? "(top)");
        }
    }

    /// <summary>刪除群組；子群組與其下設備回到「未分組」（外鍵 <c>SET NULL</c>），不連帶刪除。</summary>
    public void Delete(long id, string? actor = null)
    {
        var name = Get(id)?.Name ?? $"id:{id}";
        _store.Execute("DELETE FROM device_groups WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
        _audit.Record(actor, "device.group.delete", AuditCategories.Config, "device_group", id, name);
    }

    /// <summary>把設備指派到群組；<paramref name="groupId"/> 為 <see langword="null"/> 即移出群組（未分組）。</summary>
    public bool AssignDevice(int deviceId, long? groupId, string? actor = null)
    {
        if (groupId is long group && Get(group) is null)
        {
            throw new ArgumentException($"群組不存在：{group}。", nameof(groupId));
        }

        var affected = _store.Query<int>(
            """
            UPDATE devices SET group_id = $g WHERE id = $id;
            SELECT changes();
            """,
            r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", deviceId);
                cmd.Parameters.AddWithValue("$g", (object?)groupId ?? DBNull.Value);
            });

        if (affected > 0)
        {
            _audit.Record(
                actor,
                "device.group.assign",
                AuditCategories.Config,
                "device",
                deviceId,
                groupId?.ToString() ?? "(ungrouped)");
        }

        return affected > 0;
    }

    /// <summary>該群組直屬的設備 Id（依名稱，其次 Id）。子群組不遞迴展開，交由 UI 逐層呈現。</summary>
    public IReadOnlyList<int> ListDeviceIds(long groupId)
    {
        return _store.Query(
            "SELECT id FROM devices WHERE group_id = $g ORDER BY name, id;",
            static r =>
            {
                var list = new List<int>();
                while (r.Read())
                {
                    list.Add(r.GetInt32(0));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$g", groupId));
    }

    /// <summary>該設備所屬的群組 Id；未分組時為 <see langword="null"/>。</summary>
    public long? GroupOfDevice(int deviceId)
    {
        return _store.Query<long?>(
            "SELECT group_id FROM devices WHERE id = $d;",
            static r => r.Read() && !r.IsDBNull(0) ? r.GetInt64(0) : null,
            cmd => cmd.Parameters.AddWithValue("$d", deviceId));
    }

    private bool NameExists(string name, long? exceptId)
    {
        return _store.Query<bool>(
            exceptId is long id
                ? "SELECT EXISTS(SELECT 1 FROM device_groups WHERE name = $n AND id <> $id);"
                : "SELECT EXISTS(SELECT 1 FROM device_groups WHERE name = $n);",
            static r => r.Read() && r.GetInt32(0) == 1,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                if (exceptId is long id)
                {
                    cmd.Parameters.AddWithValue("$id", id);
                }
            });
    }

    private int NextPosition(long? parentId)
    {
        return _store.Query<int>(
            parentId is long parent
                ? "SELECT COALESCE(MAX(position), -1) + 1 FROM device_groups WHERE parent_id = $p;"
                : "SELECT COALESCE(MAX(position), -1) + 1 FROM device_groups WHERE parent_id IS NULL;",
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                if (parentId is long parent)
                {
                    cmd.Parameters.AddWithValue("$p", parent);
                }
            });
    }

    /// <summary><paramref name="nodeId"/> 是否位於 <paramref name="ancestorId"/> 的子樹內（含自身）；防環用。</summary>
    private bool IsDescendant(long ancestorId, long nodeId)
    {
        var seen = new HashSet<long>();
        var cursor = (long?)nodeId;
        while (cursor is long current && seen.Add(current))
        {
            if (current == ancestorId)
            {
                return true;
            }

            cursor = Get(current)?.ParentId;
        }

        return false;
    }
}
